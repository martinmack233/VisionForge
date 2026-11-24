using System.Security.Cryptography;
using System.Text.Json;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Editor.Core.Examples;

public sealed record ExampleEntry(string Directory, ExampleManifest Manifest)
{
    public string PreviewPath => Path.Combine(Directory, Manifest.Preview);
    public string ProjectPath => Path.Combine(Directory, Manifest.Project);
    public string TagsText => string.Join(" · ", Manifest.Tags);
}

/// <summary>Separate metadata catalog; projects are always the existing Schema1 ProjectStore documents.</summary>
public sealed class ExampleCatalog(string root, ProjectStore store)
{
    public string Root { get; } = Path.GetFullPath(root);
    public IReadOnlyList<string> LoadErrors { get; private set; } = [];
    public async Task<IReadOnlyList<ExampleEntry>> LoadAsync(CancellationToken ct = default)
    {
        var result = new List<ExampleEntry>(); var errors = new List<string>();
        if (!System.IO.Directory.Exists(Root)) { LoadErrors = ["示例目录不存在：" + Root]; return result; }
        var options = new EnumerationOptions { RecurseSubdirectories=true,MaxRecursionDepth=4,AttributesToSkip=FileAttributes.ReparsePoint,IgnoreInaccessible=true };
        foreach(var path in System.IO.Directory.EnumerateFiles(Root,"manifest.json",options).Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if(new FileInfo(path).Length>1_000_000)throw new InvalidDataException("示例manifest超过1MB。");
                var manifest=JsonSerializer.Deserialize<ExampleManifest>(await File.ReadAllTextAsync(path,ct).ConfigureAwait(false),JsonDefaults.Options)??throw new InvalidDataException("空示例元数据。");
                if(manifest.SchemaVersion!=1||string.IsNullOrWhiteSpace(manifest.Id)||string.IsNullOrWhiteSpace(manifest.Name)||manifest.Files is null||manifest.Expected is null||manifest.Cases is null)throw new InvalidDataException("示例元数据版本或必填字段无效。");
                if(result.Any(e=>e.Manifest.Id==manifest.Id))throw new InvalidDataException("重复示例ID："+manifest.Id);
                var entry=new ExampleEntry(Path.GetDirectoryName(path)!,manifest);
                SafeFile(entry.Directory,manifest.Project);SafeFile(entry.Directory,manifest.Preview);
                result.Add(entry);
            }
            catch(Exception ex) when(ex is not OperationCanceledException){errors.Add(Path.GetRelativePath(Root,path)+"："+ex.Message);}
        }
        LoadErrors=errors.AsReadOnly(); return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<string>> ValidateAsync(ExampleEntry entry,CancellationToken ct=default)
    {
        var errors=new List<string>();
        try
        {
            if(!Within(Root,entry.Directory))throw new InvalidDataException("示例不属于当前示例目录。");
            var manifest=entry.Manifest;
            if(manifest.Files.Count==0||manifest.Files.Count>2000)throw new InvalidDataException("示例资源清单为空或过大。");
            foreach(var (relative,hash) in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();var path=SafeFile(entry.Directory,relative);
                if(!File.Exists(path)){errors.Add("缺少资源："+relative);continue;}
                await using var stream=File.OpenRead(path);
                var observed=Convert.ToHexString(await SHA256.HashDataAsync(stream,ct).ConfigureAwait(false));
                if(!observed.Equals(hash,StringComparison.OrdinalIgnoreCase))errors.Add("资源校验失败："+relative);
            }
            foreach(var required in new[]{manifest.Project,manifest.Preview,"README.md"})
                if(!manifest.Files.ContainsKey(required))errors.Add("资源清单未包含："+required);
            var project=await store.LoadAsync(SafeFile(entry.Directory,manifest.Project),ct).ConfigureAwait(false);
            foreach(var node in project.Nodes)
            {
                if(node.NodeTypeId=="VirtualCamera")
                {
                    var folder=node.Parameters.Deserialize<VirtualCameraParameters>(JsonDefaults.Options)!.FolderPath;
                    var cameraFolder=SafeFile(entry.Directory,folder);
                    if(!System.IO.Directory.Exists(cameraFolder))errors.Add("相机文件夹不存在："+folder);
                    else foreach(var file in System.IO.Directory.EnumerateFiles(cameraFolder))
                        CheckListed(Path.GetRelativePath(entry.Directory,file).Replace('\\','/'));
                }
                string? resource=node.NodeTypeId switch
                {
                    "FileRead"=>node.Parameters.TryGetProperty("Path",out var filePath)?filePath.GetString():null,
                    "ImageInput"=>node.Parameters.Deserialize<ImageInputParameters>(JsonDefaults.Options)?.Path,
                    "TemplateMatching"=>node.Parameters.Deserialize<TemplateMatchingParameters>(JsonDefaults.Options)?.TemplatePath,
                    _=>null
                };
                if(resource is not null)CheckListed(resource);
            }
            foreach(var reference in project.TemplateResources)
            {
                CheckListed(reference.Path);
                using var template=JsonDocument.Parse(await File.ReadAllTextAsync(SafeFile(entry.Directory,reference.Path),ct).ConfigureAwait(false));
                var imagePath=template.RootElement.GetProperty("ImagePath").GetString()!;
                var asset=Path.GetRelativePath(entry.Directory,Path.GetFullPath(imagePath,Path.GetDirectoryName(SafeFile(entry.Directory,reference.Path))!)).Replace('\\','/');
                CheckListed(asset);
            }
            foreach(var variant in manifest.Cases)CheckListed(variant.ImagePath);
            void CheckListed(string relative)
            {
                var resolved=SafeFile(entry.Directory,relative);
                var normalized=Path.GetRelativePath(entry.Directory,resolved).Replace('\\','/');
                if(!manifest.Files.ContainsKey(normalized))errors.Add("未列入清单的引用："+relative);
                if(!File.Exists(resolved))errors.Add("引用资源不存在："+relative);
            }
        }
        catch(Exception ex) when(ex is not OperationCanceledException){errors.Add(ex.Message);}
        return errors.AsReadOnly();
    }

    /// <summary>Copies to a new unique project directory. Never writes to any built-in example directory.</summary>
    public async Task<string> CopyAsync(ExampleEntry entry,string destinationDirectory,string? projectName=null,CancellationToken ct=default)
    {
        var errors=await ValidateAsync(entry,ct).ConfigureAwait(false);if(errors.Count>0)throw new InvalidDataException(string.Join("；",errors));
        var destination=Path.GetFullPath(destinationDirectory);
        if(destination.Equals(Root,StringComparison.OrdinalIgnoreCase)||Within(Root,destination))throw new InvalidOperationException("不能将用户工程保存到内置示例目录。");
        System.IO.Directory.CreateDirectory(destination);
        var target=Path.Combine(destination,"example-"+Guid.NewGuid().ToString("N"));System.IO.Directory.CreateDirectory(target);
        // Every copy is new. If interrupted, a clearly identifiable partial folder is left for inspection;
        // the current project is not switched until the copy validates. No user directory is recursively deleted.
        var sourceProject=await store.LoadAsync(entry.ProjectPath,ct).ConfigureAwait(false);
        var projectRelative=Path.GetRelativePath(entry.Directory,entry.ProjectPath);
        foreach(var relative in entry.Manifest.Files.Keys.Append("manifest.json").Where(relative=>!Path.GetRelativePath(entry.Directory,SafeFile(entry.Directory,relative)).Equals(projectRelative,StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();var from=SafeFile(entry.Directory,relative);var to=SafeFile(target,relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            await using var source=File.OpenRead(from);await using var output=new FileStream(to,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true);
            await source.CopyToAsync(output,ct).ConfigureAwait(false);
        }
        // Publish the detached project once under its new identity. Do not copy then immediately replace it.
        var path=SafeFile(target,entry.Manifest.Project);
        await store.SaveAsync(path,sourceProject with { ProjectId=Guid.NewGuid(),ProjectName=projectName??entry.Manifest.Name+" · 副本" },ct).ConfigureAwait(false);
        return path;
    }

    public static string SafeFile(string directory,string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        if(Path.IsPathRooted(relative))throw new InvalidDataException("示例资源必须使用相对路径。");
        var folder=Path.GetFullPath(directory);var file=Path.GetFullPath(relative,folder);
        if(!Within(folder,file))throw new InvalidDataException("示例资源路径越界。");
        if(System.IO.Directory.Exists(folder)&&(File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("示例根目录不允许链接。");
        for(var current=new DirectoryInfo(Path.GetDirectoryName(file)!);current is not null&&Within(folder,current.FullName);current=current.Parent)
            if(current.Exists&&(current.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("示例资源不允许链接目录。");
        if(File.Exists(file)&&(File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("示例资源不允许文件链接。");
        return file;
    }
    private static bool Within(string parent,string child)=>Path.GetFullPath(child).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
}
