using System.Text.Json.Serialization;
namespace VisionPlatform.Runtime.Projects;

public sealed record PackageFile(string Path, string Sha256, long Bytes);
public sealed record PackageResource(string Id, string Kind, string PortablePath, bool Included, string Redistribution,
    IReadOnlyList<PackageFile> Files, IReadOnlyList<string> Bindings);
public sealed record PackageManifest
{
    [JsonRequired] public int PackageVersion { get; init; } = 1;
    [JsonRequired] public int ProjectSchema { get; init; } = 3;
    public string ProductVersion { get; init; } = Contracts.Build.ProductBuild.Version;
    public string SourceIdentity { get; init; } = Contracts.Build.ProductBuild.Identity;
    [JsonRequired] public Guid ProjectId { get; init; }
    public string ProjectName { get; init; } = "";
    [JsonRequired] public List<PackageFile> Files { get; init; } = [];
    [JsonRequired] public List<PackageResource> Resources { get; init; } = [];
    [JsonRequired] public Dictionary<string, string> Algorithms { get; init; } = [];
    public List<string> MachineRequirements { get; init; } = [];
    public string? AiProjectId { get; init; }
    public string? AiWorkspaceId { get; init; }
}
public sealed record PackageExportOptions(bool IncludeModels = false, bool ConfirmAssetRights = false);
public sealed record PackageInspection(PackageManifest Manifest, IReadOnlyList<string> Problems, IReadOnlyList<PackageResource> ExternalResources)
{
    public bool CanImport => Problems.Count == 0;
}
public sealed record PackageImportOptions(string Destination, string? ProjectName = null,
    IReadOnlyDictionary<string, string>? Relocations = null);
