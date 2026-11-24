using System.Text.Json;
using System.Text.Json.Serialization;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Runtime.Graph;

namespace VisionPlatform.Runtime.Projects;

public sealed record TemplateResourceReference(string ResourceId, string Path, string? Sha256 = null);
/// <summary>Association only: deleting a vision project never deletes the independently owned AI data.</summary>
public sealed record AiProjectLink(string ProjectId, string WorkspaceId, string DataHome);

/// <summary>Versioned data only. Neither execution results nor native handles enter JSON.</summary>
public sealed record ProjectDocument
{
    public const int CurrentSchemaVersion = 3;
    [JsonRequired] public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    [JsonRequired] public Guid ProjectId { get; init; } = Guid.NewGuid();
    [JsonRequired] public string ProjectName { get; init; } = "Untitled";
    [JsonRequired] public List<NodeSpecification> Nodes { get; init; } = [];
    [JsonRequired] public List<NodeConnection> Connections { get; init; } = [];
    [JsonRequired] public List<RotatedRectRoi> ROI { get; init; } = [];
    [JsonRequired] public List<TemplateResourceReference> TemplateResources { get; init; } = [];
    [JsonRequired] public Dictionary<string, string> AlgorithmVersion { get; init; } = new(StringComparer.Ordinal);
    public AiProjectLink? AiProject { get; init; }
    public NodeGraph ToGraph() => new(Nodes, Connections);
}

public sealed class ProjectValidationException(IEnumerable<string> errors)
    : Exception("Project validation failed: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors.ToArray();
}

public sealed class ProjectStore(NodeRegistry registry, IReadOnlyDictionary<string, string>? installedAlgorithms = null)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static JsonSerializerOptions CreateJsonOptions() => new(JsonDefaults.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64
    };

    public IReadOnlyList<string> Validate(ProjectDocument project, string resourceRoot)
    {
        ArgumentNullException.ThrowIfNull(project);
        var errors = new List<string>();
        if (project.SchemaVersion is not (1 or 2 or ProjectDocument.CurrentSchemaVersion))
            errors.Add($"Unsupported SchemaVersion {project.SchemaVersion}; supported 1, 2 and {ProjectDocument.CurrentSchemaVersion}.");
        if (project.ProjectId == Guid.Empty) errors.Add("ProjectId must be non-empty.");
        if (string.IsNullOrWhiteSpace(project.ProjectName)) errors.Add("ProjectName must be non-empty.");
        if (project.Nodes is null || project.Connections is null || project.ROI is null || project.TemplateResources is null || project.AlgorithmVersion is null)
        { errors.Add("Project collections must not be null."); return errors; }
        if (project.SchemaVersion == 1 && project.Nodes.Any(n => n is { IsEnabled: false }))
            errors.Add("Disabled nodes require SchemaVersion 2; Schema1 has no disabled-node semantics.");
        if (project.AiProject is { } ai)
        {
            if (project.SchemaVersion < 3) errors.Add("AI project association requires SchemaVersion 3.");
            if (string.IsNullOrWhiteSpace(ai.ProjectId) || ai.ProjectId.Length > 128 || ai.ProjectId.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(ai.WorkspaceId) || ai.WorkspaceId.Length > 128 || ai.WorkspaceId.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(ai.DataHome) || !Path.IsPathFullyQualified(ai.DataHome) || ai.DataHome.Length > 4096)
                errors.Add("AI association requires independent project/workspace identities and an absolute data-home path.");
        }
        var validator = new GraphValidator(registry);
        errors.AddRange(project.Nodes.Any(n => n is { IsEnabled: false })
            ? validator.ValidateStructure(project.ToGraph()) : validator.Validate(project.ToGraph()));
        foreach (var roi in project.ROI)
        {
            try { roi.Validate(); }
            catch (Exception ex) { errors.Add("ROI: " + ex.Message); }
        }
        var resourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in project.TemplateResources)
        {
            if (resource is null) { errors.Add("Template resource must not be null."); continue; }
            if (string.IsNullOrWhiteSpace(resource.ResourceId) || !resourceIds.Add(resource.ResourceId))
                errors.Add($"Duplicate or empty template ResourceId '{resource.ResourceId}'.");
            try
            {
                var path = ResolveResourcePath(resourceRoot, resource.Path);
                if (!File.Exists(path)) { errors.Add($"Template resource '{resource.ResourceId}' is missing: {resource.Path}"); continue; }
                if (resource.Sha256 is not null)
                {
                    using var stream = File.OpenRead(path);
                    var observed = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                    if (!observed.Equals(resource.Sha256, StringComparison.OrdinalIgnoreCase))
                        errors.Add($"Template resource '{resource.ResourceId}' SHA-256 does not match.");
                }
            }
            catch (Exception ex) { errors.Add($"Template resource '{resource.ResourceId}': {ex.Message}"); }
        }
        foreach (var (id, version) in project.AlgorithmVersion)
        {
            if (installedAlgorithms is null || !installedAlgorithms.TryGetValue(id, out var installed))
                errors.Add($"Required algorithm '{id}' is not installed.");
            else if (!string.Equals(version, installed, StringComparison.Ordinal))
                errors.Add($"Algorithm '{id}' requires version '{version}', installed '{installed}'.");
        }
        return errors;
    }

    public async Task SaveAsync(string path, ProjectDocument project, CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target)!;
        var errors = Validate(project, directory);
        if (errors.Count != 0) throw new ProjectValidationException(errors);
        cancellationToken.ThrowIfCancellationRequested();
        // Serialize a detached document before touching the original file.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(project with { SchemaVersion = ProjectDocument.CurrentSchemaVersion }, JsonOptions);
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(target))
            {
                // Loading a legacy document is read-only. Its first schema-changing save keeps
                // the exact previous bytes in the same atomic replacement operation.
                using var previous = JsonDocument.Parse(await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false));
                var priorVersion = previous.RootElement.GetProperty("SchemaVersion").GetInt32();
                var backup = priorVersion != ProjectDocument.CurrentSchemaVersion ? target + ".schema" + priorVersion + "." + Guid.NewGuid().ToString("N") + ".bak" : null;
                File.Replace(temporary, target, backup);
            }
            else File.Move(temporary, target);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<ProjectDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(path);
        await using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<ProjectDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new ProjectValidationException(["Project JSON is null."]);
        // Schema1 never defined IsEnabled. Do not accept a disguised Schema2 document as legacy.
        if (document.SchemaVersion == 1 && document.Nodes is not null && document.Nodes.Any(n => n is { IsEnabled: false }))
            throw new ProjectValidationException(["Schema1 cannot contain disabled nodes; use SchemaVersion 2."]);
        var errors = Validate(document, Path.GetDirectoryName(target)!);
        if (errors.Count != 0) throw new ProjectValidationException(errors);
        return document with { SchemaVersion = ProjectDocument.CurrentSchemaVersion };
    }

    public static string ResolveResourcePath(string resourceRoot, string resourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        return Path.GetFullPath(resourcePath, Path.GetFullPath(resourceRoot));
    }
}
