using System.Security.Cryptography;
using System.Text.Json;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Matching;
using VisionPlatform.Imaging;

namespace VisionPlatform.Algorithms.Matching;

/// <summary>Content-addressed PNG assets and atomically replaced versioned template manifest.</summary>
public static class TemplateResourceStore
{
    private sealed record Manifest(int SchemaVersion, string ModelId, string AlgorithmId, string AlgorithmVersion,
        string ImagePath, string Sha256, int Width, int Height, PixelFormat PixelFormat, double ReferenceX, double ReferenceY);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Save(string path, TemplateModel template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(parent);
        var assets = Path.Combine(parent, Path.GetFileNameWithoutExtension(fullPath) + ".templates");
        Directory.CreateDirectory(assets);
        var assetTemp = Path.Combine(assets, Guid.NewGuid().ToString("N") + ".tmp.png");
        var jsonTemp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            ImageFile.Save(template.Image, assetTemp);
            var hash = Hash(assetTemp);
            var assetPath = Path.Combine(assets, hash + ".png");
            if (File.Exists(assetPath))
            {
                if (Hash(assetPath) != hash) throw new InvalidDataException("Existing content-addressed template asset is corrupt.");
            }
            else File.Move(assetTemp, assetPath);
            var matcher = new CvInspectTemplateMatcher();
            var manifest = new Manifest(1, template.ModelId, matcher.AlgorithmId, matcher.Version,
                Path.GetRelativePath(parent, assetPath).Replace('\\', '/'), hash,
                template.Image.Width, template.Image.Height, template.Image.PixelFormat, template.ReferenceX, template.ReferenceY);
            using (var stream = new FileStream(jsonTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, manifest, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(jsonTemp, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(assetTemp)) File.Delete(assetTemp);
            if (File.Exists(jsonTemp)) File.Delete(jsonTemp);
        }
    }

    public static void Save(TemplateModel template, string path) => Save(path, template);

    public static TemplateModel Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Template manifest is missing.", fullPath);
        if (new FileInfo(fullPath).Length > 1_000_000) throw new InvalidDataException("Template manifest is too large.");
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(fullPath), JsonOptions)
            ?? throw new InvalidDataException("Template manifest is empty.");
        var matcher = new CvInspectTemplateMatcher();
        if (manifest.SchemaVersion != 1) throw new InvalidDataException($"Unsupported template schema {manifest.SchemaVersion}.");
        if (manifest.AlgorithmId != matcher.AlgorithmId || manifest.AlgorithmVersion != matcher.Version)
            throw new InvalidDataException("Template algorithm/version is unavailable or incompatible.");
        if (!Enum.IsDefined(manifest.PixelFormat) || string.IsNullOrWhiteSpace(manifest.ModelId) ||
            string.IsNullOrWhiteSpace(manifest.ImagePath) || string.IsNullOrWhiteSpace(manifest.Sha256))
            throw new InvalidDataException("Template manifest fields are invalid.");
        var parent = Path.GetDirectoryName(fullPath)!;
        var assetPath = Path.GetFullPath(Path.Combine(parent, manifest.ImagePath));
        var parentPrefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(manifest.ImagePath) || !assetPath.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Template resource must remain inside the manifest directory.");
        if (!File.Exists(assetPath)) throw new FileNotFoundException("Template image resource is missing.", assetPath);
        if (!string.Equals(Hash(assetPath), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Template image SHA-256 verification failed.");
        using var image = ImageFile.Load(assetPath, manifest.PixelFormat);
        if (image.Width != manifest.Width || image.Height != manifest.Height)
            throw new InvalidDataException("Template dimensions do not match the manifest.");
        return new TemplateModel(image, manifest.ModelId, manifest.ReferenceX, manifest.ReferenceY);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
