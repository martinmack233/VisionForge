using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Runtime.Graph;

namespace VisionPlatform.Runtime.Projects;

/// <summary>One portable envelope around ProjectStore. Does not execute or initialize plugins/devices.</summary>
public sealed class ProjectPackageStore(NodeRegistry registry, IReadOnlyDictionary<string, string>? algorithms = null)
{
    public const int MaximumEntries = 4096;
    public const long MaximumFileBytes = 512L * 1024 * 1024;
    public const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    private const string ProjectEntry = "project.json";
    private static readonly JsonSerializerOptions Json = new(JsonDefaults.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 64 };
    private readonly ProjectStore _store = new(registry, algorithms);
    private sealed record Content(string Name, byte[]? Bytes, string? Source, string Hash, long Length);
    private static bool IsImage(string path) => new[] { ".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    private static bool IsDataFile(string path)=>new[]{".txt",".json",".csv",".bin"}.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase)&&!Path.GetFileName(path).StartsWith(".env",StringComparison.OrdinalIgnoreCase);
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    public static string HashFile(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
    private static byte[] Bytes(object value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    private static async Task<string> TransferAsync(Stream input, Stream? output, long expected, CancellationToken ct)
    {
        if (expected < 0 || expected > MaximumFileBytes) throw new InvalidDataException("文件预算无效。");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long total = 0; int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read; if (total > expected) throw new InvalidDataException("实际解压内容超过声明大小；已拒绝。");
            hash.AppendData(buffer, 0, read); if (output is not null) await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        if (total != expected) throw new InvalidDataException("实际内容长度与清单不一致。");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name.Contains('\\') || name.Contains(':') || name.StartsWith('/') || name.EndsWith('/') ||
            name.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(p.Split('.')[0], StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("工程包包含不安全路径：" + name);
        return name;
    }
    private static string Inside(string root, string relative)
    {
        SafeName(relative);
        var full = Path.GetFullPath(relative, root);
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("资源越界。");
        return full;
    }
    private static IEnumerable<(JsonObject Node, string Pointer)> Nodes(JsonObject document)
    {
        static IEnumerable<(JsonObject, string)> Walk(JsonArray nodes, string prefix, int depth)
        {
            if (depth > 1) throw new InvalidDataException("不支持嵌套子流程。");
            for (var i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i]?.AsObject() ?? throw new InvalidDataException("空节点。"); var pointer = prefix + "/" + i;
                yield return (n, pointer);
                if (n["NodeTypeId"]?.GetValue<string>() == "ForEach" && n["Parameters"]?["Body"]?["Nodes"] is JsonArray child)
                    foreach (var item in Walk(child, pointer + "/Parameters/Body/Nodes", depth + 1)) yield return item;
            }
        }
        return Walk(document["Nodes"]!.AsArray(), "/Nodes", 0);
    }
    private static void Set(JsonObject document, string pointer, string value)
    {
        var parts = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries); JsonNode node = document;
        foreach (var part in parts[..^1]) node = node is JsonArray a ? a[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)]! : node[part]!;
        node[parts[^1]] = value;
    }
    public async Task<PackageManifest> ExportAsync(string projectPath, string packagePath, PackageExportOptions? options = null, CancellationToken ct = default)
    {
        options ??= new();
        if (options.IncludeModels && !options.ConfirmAssetRights) throw new ArgumentException("携带模型/语言资源需要明确确认有权携带；默认仅记录外部引用。");
        var target = Path.GetFullPath(packagePath); if (File.Exists(target)) throw new IOException("包文件已存在，请使用新名称，未覆盖。");
        var source = Path.GetFullPath(projectPath); var root = Path.GetDirectoryName(source)!;
        if (new FileInfo(source).Length > 4_000_000) throw new InvalidDataException("工程JSON超出4MB预算。");
        var project = await _store.LoadAsync(source, ct).ConfigureAwait(false);
        var doc = JsonSerializer.SerializeToNode(project, Json)!.AsObject();
        // Independent AI identities survive in the envelope, never a business DB/home directory.
        doc["AiProject"] = null;
        var manifest = new PackageManifest { ProjectId = project.ProjectId, ProjectName = project.ProjectName, Algorithms = new(project.AlgorithmVersion), AiProjectId = project.AiProject?.ProjectId, AiWorkspaceId = project.AiProject?.WorkspaceId };
        var contents = new List<Content>(); var resourceCounter = 0;
        Content FileContent(string name, string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("导出依赖缺失：" + path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("资源不能是符号链接/重解析点。");
            var size = new FileInfo(path).Length; if (size > MaximumFileBytes) throw new InvalidDataException("资源超过512 MiB预算。");
            return new(SafeName(name), null, path, HashFile(path), size);
        }
        string AddResource(string kind, string path, string binding, bool include)
        {
            ct.ThrowIfCancellationRequested(); path = ProjectStore.ResolveResourcePath(root, path);
            var id = "r" + (++resourceCounter).ToString("D4"); var prefix = "resources/" + id + "/";
            var local = prefix + Path.GetFileName(path); var files = new List<Content>();
            if (kind is "OcrLanguage" or "VirtualFolder")
            {
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException("外部资源目录缺失：" + path);
                var accepted = Directory.EnumerateFiles(path).Where(p => kind == "OcrLanguage" ? Path.GetExtension(p) == ".traineddata" : new[] { ".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff" }.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).ToArray();
                if (accepted.Length is 0 or > 256) throw new InvalidDataException("目录资源需要1..256个明确的图像/字库文件。");
                local = prefix.TrimEnd('/');
                files.AddRange(accepted.Select(p => FileContent(prefix + Path.GetFileName(p), p)));
            }
            else
            {
                if (kind == "Image" && !IsImage(path) || kind == "Template" && Path.GetExtension(path).ToLowerInvariant() != ".json") throw new InvalidDataException("只允许明确的图像/模板文件，不打包数据库、日志或其他任意文件。");
                if(kind=="DataFile"&&(!IsDataFile(path)||new FileInfo(path).Length>16*1024*1024))throw new InvalidDataException("文件读取资源仅允许不超过16MiB的.txt/.json/.csv/.bin，不打包环境、密钥或数据库。");
                files.Add(FileContent(local, path));
                if (kind == "Model")
                {
                    if (Path.GetExtension(path).ToLowerInvariant() != ".onnx") throw new InvalidDataException("当前可部署模型仅支持ONNX及其deployment.json。");
                    files.Add(FileContent(prefix + "deployment.json", Path.Combine(Path.GetDirectoryName(path)!, "deployment.json")));
                    var deployment = JsonNode.Parse(File.ReadAllText(files[^1].Source!))!;
                    if (deployment["onnx_sha256"]?.GetValue<string>() != files[0].Hash || deployment["model_file"]?.GetValue<string>() != Path.GetFileName(path)) throw new InvalidDataException("模型与部署契约哈希/文件名不一致，须重新校验。");
                }
                if (kind == "Template")
                {
                    if (files[0].Length > 1_000_000) throw new InvalidDataException("模板manifest超过预算。");
                    var template = JsonNode.Parse(File.ReadAllText(path))!; var image = template["ImagePath"]!.GetValue<string>();
                    if (Path.GetExtension(image).ToLowerInvariant() != ".png") throw new InvalidDataException("模板图像必须是原模板存储契约的PNG资源。");
                    var asset = Inside(Path.GetDirectoryName(path)!, image.Replace('\\', '/'));
                    var assetFile = FileContent(prefix + image.Replace('\\', '/'), asset);
                    if (!string.Equals(assetFile.Hash, template["Sha256"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("模板图像哈希错误。");
                    files.Add(assetFile);
                }
            }
            if (include) contents.AddRange(files);
            manifest.Resources.Add(new(id, kind, local, include, kind is "Model" or "OcrLanguage" ? (include ? "user-confirmed-carry-rights-not-a-commercial-license" : "external-not-redistributed") : "project-owned-resource", files.Select(f => new PackageFile(f.Name, f.Hash, f.Length)).ToArray(), [binding]));
            return local;
        }
        foreach (var (node, pointer) in Nodes(doc))
        {
            var type = node["NodeTypeId"]!.GetValue<string>(); var p = node["Parameters"]!.AsObject();
            foreach (var key in p.Select(k => k.Key)) if (key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.Contains("apikey", StringComparison.OrdinalIgnoreCase) || key.Contains("secret", StringComparison.OrdinalIgnoreCase))
                if (p[key]?.ToString().Length > 0) throw new InvalidDataException("工程含敏感参数，请先移除或使用机器端安全配置：" + key);
            (string Key, string Kind, bool Include)? resource = type switch
            {
                "FileRead" => ("Path", "DataFile", true),
                "ImageInput" => ("Path", "Image", true), "TemplateMatching" => ("TemplatePath", "Template", true),
                "YoloInference" => ("ModelPath", "Model", options.IncludeModels), "OCR" => ("DataPath", "OcrLanguage", options.IncludeModels),
                "VirtualCamera" => ("FolderPath", "VirtualFolder", false), _ => null
            };
            if (resource is { } field && p[field.Key]?.GetValue<string>() is { Length: > 0 } value)
            {
                var replacement = AddResource(field.Kind, value, pointer + "/Parameters/" + field.Key, field.Include); p[field.Key] = replacement;
                if (type == "YoloInference")
                {
                    var item = manifest.Resources[^1];
                    if (p["ExpectedModelSha256"]?.GetValue<string>() is { Length: > 0 } expected && !string.Equals(expected, item.Files[0].Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("节点已验证模型发生变化，不能导出旧验证状态。");
                    if (p["ExpectedDeploymentSha256"]?.GetValue<string>() is { Length: > 0 } ds && !string.Equals(ds, item.Files[1].Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("节点部署契约已变化，须重新验证。");
                    p["ExpectedModelSha256"] = item.Files[0].Sha256; p["ExpectedDeploymentSha256"] = item.Files[1].Sha256;
                    manifest.MachineRequirements.Add("AI detection component 1.0.0 / protocol 1 / ONNX CPU; local Python configuration required");
                }
            }
            if (type == "YoloInference") foreach (var key in new[] { "PythonExecutable", "YoloStudioRoot" }) p[key] = "";
            if (type == "CameraInput")
            {
                manifest.MachineRequirements.Add("Camera provider/driver and explicit local device identity configuration required; no SDK included");
                if (p["Options"] is JsonObject camera) foreach (var key in camera.Select(k => k.Key).Where(k => k.Contains("Sdk", StringComparison.OrdinalIgnoreCase) || k.Contains("Worker", StringComparison.OrdinalIgnoreCase)).ToArray()) camera[key] = "";
            }
            foreach (var pathField in p)
                if ((pathField.Key.EndsWith("Path", StringComparison.OrdinalIgnoreCase) || pathField.Key.EndsWith("Directory", StringComparison.OrdinalIgnoreCase) || pathField.Key.EndsWith("Executable", StringComparison.OrdinalIgnoreCase)) &&
                    pathField.Value is JsonValue valueNode && valueNode.TryGetValue<string>(out var text) && text.Length > 0 && pathField.Key != resource?.Key && type != "YoloInference")
                    throw new InvalidDataException($"尚未定义可携带资源规则：{type}.{pathField.Key}；请显式调整为空或使用受支持资源节点。");
        }
        var resources = doc["TemplateResources"]!.AsArray();
        for (var i = 0; i < resources.Count; i++)
        {
            var t = resources[i]!.AsObject(); t["Path"] = AddResource("Template", t["Path"]!.GetValue<string>(), "/TemplateResources/" + i + "/Path", true);
            t["Sha256"] = manifest.Resources[^1].Files[0].Sha256;
        }
        void AddBytes(string name, byte[] bytes) => contents.Add(new(name, bytes, null, Hash(bytes), bytes.Length));
        AddBytes(ProjectEntry, System.Text.Encoding.UTF8.GetBytes(doc.ToJsonString(Json)));
        if (File.Exists(source + ".editor.json"))
        {
            var layout = await File.ReadAllBytesAsync(source + ".editor.json", ct); if (layout.Length > 1_000_000) throw new InvalidDataException("布局超出预算。"); AddBytes("project.json.editor.json", layout);
        }
        if (contents.Count > MaximumEntries - 1 || contents.Sum(c => c.Length) > MaximumExpandedBytes) throw new InvalidDataException("工程包超过资源预算。");
        if (contents.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != contents.Count) throw new InvalidDataException("工程资源同名冲突。");
        manifest.Files.AddRange(contents.Select(c => new PackageFile(c.Name, c.Hash, c.Length)));
        var parent = Path.GetDirectoryName(target)!; Directory.CreateDirectory(parent); var temporary = Path.Combine(parent, ".package-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var c in contents)
                {
                    ct.ThrowIfCancellationRequested(); var entry = zip.CreateEntry(c.Name, CompressionLevel.Optimal); await using var output = entry.Open();
                    if (c.Bytes is not null) await output.WriteAsync(c.Bytes, ct); else { await using var input = File.OpenRead(c.Source!); var observed = await TransferAsync(input, output, c.Length, ct); if (!observed.Equals(c.Hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("导出期间资源变化，请重新检查。"); }
                }
                var metadata = zip.CreateEntry("manifest.json"); await using var stream = metadata.Open(); await stream.WriteAsync(Bytes(manifest), ct);
            }
            _ = await ReadAsync(temporary, ct); // Content changed during export cannot produce a successful package.
            ct.ThrowIfCancellationRequested(); File.Move(temporary, target); return manifest;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<PackageInspection> InspectAsync(string package, CancellationToken ct = default)
    {
        try
        {
            var (manifest, doc) = await ReadAsync(package, ct); var errors = Compatibility(manifest, doc);
            return new(manifest, errors, manifest.Resources.Where(r => !r.Included).ToArray());
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or ArgumentException or NotSupportedException or InvalidOperationException or NullReferenceException or KeyNotFoundException or FormatException or OverflowException or IndexOutOfRangeException)
        { return new(new(), [ex.Message], []); }
    }
    private IReadOnlyList<string> Compatibility(PackageManifest manifest, ProjectDocument doc)
    {
        var errors = new List<string>();
        if (doc.SchemaVersion is not (1 or 2 or 3) || doc.SchemaVersion != manifest.ProjectSchema) errors.Add("工程Schema不兼容。");
        if (doc.ProjectId != manifest.ProjectId) errors.Add("工程身份与清单不一致。");
        if (doc.Nodes is null || doc.Connections is null || doc.ROI is null || doc.TemplateResources is null || doc.AlgorithmVersion is null) { errors.Add("工程集合为空。"); return errors; }
        if (!doc.AlgorithmVersion.OrderBy(x => x.Key).SequenceEqual(manifest.Algorithms.OrderBy(x => x.Key))) errors.Add("工程算法依赖与清单不一致。");
        errors.AddRange(new GraphValidator(registry).ValidateStructure(doc.ToGraph()));
        foreach (var (id, version) in manifest.Algorithms)
            if (algorithms is null || !algorithms.TryGetValue(id, out var installed) || version != installed) errors.Add($"算法依赖不兼容：{id} requires {version}。");
        return errors;
    }
    private static async Task<(PackageManifest Manifest, ProjectDocument Project)> ReadAsync(string package, CancellationToken ct)
    {
        if (new FileInfo(package).Length > MaximumExpandedBytes) throw new InvalidDataException("压缩包超过2 GiB预算。");
        using var zip = ZipFile.OpenRead(package);
        if (zip.Entries.Count is < 2 or > MaximumEntries || zip.Entries.Sum(e => e.Length) > MaximumExpandedBytes) throw new InvalidDataException("工程包条数/展开大小超出预算。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            SafeName(entry.FullName);
            if (!names.Add(entry.FullName) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || entry.Length > MaximumFileBytes)
                throw new InvalidDataException("工程包包含同名冲突、链接或超额文件。");
        }
        var metadata = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("缺少manifest.json。");
        if (metadata.Length > 1_000_000) throw new InvalidDataException("清单超过预算。");
        PackageManifest manifest;
        await using (var stream = metadata.Open()) manifest = await JsonSerializer.DeserializeAsync<PackageManifest>(stream, Json, ct) ?? throw new InvalidDataException("空清单。");
        if (manifest.PackageVersion != 1 || manifest.Files is null || manifest.Resources is null || manifest.Algorithms is null || manifest.MachineRequirements is null) throw new InvalidDataException("不支持的包版本/清单结构。");
        if (manifest.Files.Count != zip.Entries.Count - 1 || manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count || manifest.Resources.Select(r => r.Id).Distinct().Count() != manifest.Resources.Count) throw new InvalidDataException("文件清单重复或存在未声明内容。");
        foreach (var file in manifest.Files)
        {
            SafeName(file.Path); var entry = zip.GetEntry(file.Path) ?? throw new InvalidDataException("声明的资源缺失：" + file.Path);
            if (entry.Length != file.Bytes || file.Path == "manifest.json" || file.Sha256.Length != 64) throw new InvalidDataException("文件大小/哈希声明错误。");
            await using var stream = entry.Open(); var hash = await TransferAsync(stream, null, file.Bytes, ct);
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("内容哈希不一致：" + file.Path);
        }
        foreach (var resource in manifest.Resources)
        {
            SafeName(resource.PortablePath);
            if (resource.Files is null || resource.Bindings is null || resource.Files.Count == 0 || resource.Bindings.Count == 0 || resource.Kind is not ("Model" or "Template" or "Image" or "OcrLanguage" or "VirtualFolder" or "DataFile")) throw new InvalidDataException("资源类型/绑定不支持。");
            var portable = resource.Files.SingleOrDefault(f => f.Path == resource.PortablePath);
            if (resource.Kind == "DataFile" && (!resource.Included||resource.Files.Count!=1||portable is null||!IsDataFile(portable.Path)||portable.Bytes>16*1024*1024) ||
                resource.Kind == "Image" && (!resource.Included || resource.Files.Count != 1 || portable is null || !IsImage(portable.Path)) ||
                resource.Kind == "Template" && (!resource.Included || resource.Files.Count != 2 || portable is null || Path.GetExtension(portable.Path).ToLowerInvariant() != ".json") ||
                resource.Kind == "Model" && (resource.Files.Count != 2 || portable is null || Path.GetExtension(portable.Path).ToLowerInvariant() != ".onnx" || !resource.Files.Any(f => f.Path == Path.GetDirectoryName(resource.PortablePath)!.Replace('\\', '/') + "/deployment.json")) ||
                resource.Kind == "VirtualFolder" && resource.Files.Any(f => !IsImage(f.Path)) ||
                resource.Kind == "OcrLanguage" && resource.Files.Any(f => Path.GetExtension(f.Path).ToLowerInvariant() != ".traineddata")) throw new InvalidDataException("资源文件形态不支持；不接受数据库/环境/可执行文件作为图像资源。");
            foreach (var file in resource.Files)
            {
                SafeName(file.Path);
                if (file.Bytes < 0 || file.Bytes > MaximumFileBytes || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit) || resource.Included && !manifest.Files.Contains(file) || !resource.Included && manifest.Files.Any(f => f.Path == file.Path)) throw new InvalidDataException("资源清单与文件内容不符。");
            }
            if (resource.Kind == "Template")
            {
                if (portable!.Bytes > 1_000_000) throw new InvalidDataException("模板清单超出预算。");
                using var reader = new StreamReader(zip.GetEntry(portable.Path)!.Open()); var template = JsonNode.Parse(await reader.ReadToEndAsync(ct))!;
                if (template["SchemaVersion"]?.GetValue<int>() != 1) throw new InvalidDataException("模板资源版本不兼容。");
                var asset = template["ImagePath"]!.GetValue<string>().Replace('\\', '/'); SafeName(asset);
                var expected = Path.GetDirectoryName(portable.Path)!.Replace('\\', '/') + "/" + asset;
                if (!resource.Files.Any(f => f.Path == expected && f.Sha256.Equals(template["Sha256"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("模板内部图像引用/哈希与包清单不一致。");
            }
        }
        var projectEntry = zip.GetEntry(ProjectEntry) ?? throw new InvalidDataException("缺少project.json。");
        if (projectEntry.Length > 4_000_000) throw new InvalidDataException("工程JSON超出4MB预算。");
        await using var input = projectEntry.Open(); var project = await JsonSerializer.DeserializeAsync<ProjectDocument>(input, Json, ct) ?? throw new InvalidDataException("空工程。");
        ValidateResourceBindings(manifest, project);
        return (manifest, project);
    }
    private static void ValidateResourceBindings(PackageManifest manifest, ProjectDocument project)
    {
        var document = JsonSerializer.SerializeToNode(project, Json)!.AsObject(); var expected = new Dictionary<string, string>();var expectedKinds=new Dictionary<string,string>();
        foreach (var (node, pointer) in Nodes(document))
        {
            var p = node["Parameters"]!.AsObject(); var type = node["NodeTypeId"]!.GetValue<string>();
            var key = type switch { "FileRead" => "Path", "ImageInput" => "Path", "TemplateMatching" => "TemplatePath", "YoloInference" => "ModelPath", "OCR" => "DataPath", "VirtualCamera" => "FolderPath", _ => null };
            if (key is not null && p[key]?.GetValue<string>() is { Length: > 0 } path) {expected.Add(pointer + "/Parameters/" + key, path);expectedKinds.Add(pointer+"/Parameters/"+key,type switch{"FileRead"=>"DataFile","ImageInput"=>"Image","TemplateMatching"=>"Template","YoloInference"=>"Model","OCR"=>"OcrLanguage",_=>"VirtualFolder"});}
            if (type == "YoloInference" && new[] { "PythonExecutable", "YoloStudioRoot" }.Any(k => p[k]?.GetValue<string>() is { Length: > 0 })) throw new InvalidDataException("包中含机器环境路径；须重新导出。");
        }
        for (var i = 0; i < project.TemplateResources.Count; i++){expected.Add("/TemplateResources/" + i + "/Path", project.TemplateResources[i].Path);expectedKinds.Add("/TemplateResources/"+i+"/Path","Template");}
        var bound = new HashSet<string>();
        foreach (var resource in manifest.Resources)
        {
            var prefix = "resources/" + resource.Id + "/"; SafeName(prefix + "x");
            if (!resource.PortablePath.StartsWith(prefix, StringComparison.Ordinal) && resource.PortablePath != prefix.TrimEnd('/') || resource.Files.Any(f => !f.Path.StartsWith(prefix, StringComparison.Ordinal))) throw new InvalidDataException("资源未处于自己的资源ID目录。");
            foreach (var pointer in resource.Bindings)
                if (!bound.Add(pointer) || !expected.TryGetValue(pointer, out var actual) || actual != resource.PortablePath||expectedKinds[pointer]!=resource.Kind) throw new InvalidDataException("资源绑定无效或工程引用未重定位。");
        }
        if (expected.Keys.Any(p => !bound.Contains(p))) throw new InvalidDataException("工程引用了未声明的外部资源。");
        var resourceFiles = manifest.Resources.Where(r => r.Included).SelectMany(r => r.Files).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        if (manifest.Files.Any(f => f.Path is not (ProjectEntry or "project.json.editor.json") && !resourceFiles.Contains(f.Path))) throw new InvalidDataException("清单含未声明的额外文件。");
    }
    public async Task<string> ImportAsync(string package, PackageImportOptions options, CancellationToken ct = default)
    {
        var (manifest, project) = await ReadAsync(package, ct); var errors = Compatibility(manifest, project);
        if (errors.Count > 0) throw new ProjectValidationException(errors);
        var destination = Path.GetFullPath(options.Destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("目标同名冲突：请选择不存在的新目录，不覆盖原工程。");
        var name = options.ProjectName?.Trim() ?? project.ProjectName;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120) throw new ArgumentException("工程名称需要1..120字符。");
        var doc = JsonSerializer.SerializeToNode(project, Json)!.AsObject();
        if (options.Relocations is not null && options.Relocations.Keys.Any(k => !manifest.Resources.Any(r => !r.Included && r.Id == k))) throw new InvalidDataException("重定位清单包含未知或随包资源ID。");
        foreach (var resource in manifest.Resources.Where(r => !r.Included))
        {
            if (options.Relocations is null || !options.Relocations.TryGetValue(resource.Id, out var replacement) || !Path.IsPathFullyQualified(replacement)) throw new InvalidDataException("外部资源尚未重定位：" + resource.Id + " (" + resource.Kind + ")。");
            if (resource.Kind == "Model" && Path.GetFileName(replacement) != Path.GetFileName(resource.PortablePath)) throw new InvalidDataException("重定位模型文件名必须与原deployment.json契约保持一致；请选择正确文件，不自动改写模型部署契约。");
            foreach (var file in resource.Files)
            {
                var path = resource.Kind is "VirtualFolder" or "OcrLanguage" ? Path.Combine(replacement, Path.GetFileName(file.Path)) :
                    file.Path == resource.PortablePath ? replacement : Path.Combine(Path.GetDirectoryName(replacement)!, Path.GetFileName(file.Path));
                if (!File.Exists(path)) throw new FileNotFoundException("重定位资源缺失：" + resource.Id);
                if (new FileInfo(path).Length != file.Bytes || !HashFile(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("重定位资源哈希变化：" + resource.Id + "；拒绝沿用旧验证状态，请在源工程重新校验后导出。");
            }
            foreach (var binding in resource.Bindings) Set(doc, binding, Path.GetFullPath(replacement));
        }
        doc["ProjectName"] = name;
        var importProject = doc.Deserialize<ProjectDocument>(Json)!;
        var parent = Path.GetDirectoryName(destination)!; Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".vision-import-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        try
        {
            using (var zip = ZipFile.OpenRead(package))
                foreach (var file in manifest.Files.Where(f => f.Path != ProjectEntry))
                {
                    ct.ThrowIfCancellationRequested(); var output = Inside(staging, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    await using var input = zip.GetEntry(file.Path)!.Open(); await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var observed = await TransferAsync(input, stream, file.Bytes, ct); await stream.FlushAsync(ct); stream.Close();
                    if (!observed.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("导入期间包内容改变；未提交导入目录。");
                }
            // Reject absolute/traversing portable references even if a malicious manifest provided valid hashes.
            var stagedDoc = JsonSerializer.SerializeToNode(importProject, Json)!.AsObject();
            foreach (var (node, _) in Nodes(stagedDoc))
            {
                var p = node["Parameters"]!.AsObject(); var type = node["NodeTypeId"]!.GetValue<string>();
                if (type == "YoloInference" && new[] { "PythonExecutable", "YoloStudioRoot" }.Any(k => p[k]?.GetValue<string>() is { Length: > 0 })) throw new InvalidDataException("工程包不能携带机器Python/代码路径。");
            }
            await _store.SaveAsync(Path.Combine(staging, ProjectEntry), importProject, ct);
            ct.ThrowIfCancellationRequested(); Directory.Move(staging, destination); return Path.Combine(destination, ProjectEntry);
        }
        finally
        {
            if (Directory.Exists(staging) && Path.GetDirectoryName(staging) == parent && Path.GetFileName(staging).StartsWith(".vision-import-", StringComparison.Ordinal)) Directory.Delete(staging, true);
        }
    }
}
