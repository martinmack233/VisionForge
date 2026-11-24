# Nodify source provenance

Upstream https://github.com/miroiu/nodify, exact previously audited commit `8e7c93b4a11d3b06e257938ae0f06bd09e427c78`, source version 7.3.0, MIT. Original LICENSE retained. All tracked files under upstream Nodify/ copied without control/source changes. upstream-sha256.json preserves original hashes.

Nodify.csproj is replaced: target net8.0-windows, no packaging/signing, explicit source project dependency. Original Properties/AssemblyInfo.cs is retained but excluded from compilation; PlatformAssemblyInfo.cs repeats its WPF theme/XML namespace attributes and omits the upstream signing-key attribute.

The cached NuGet package bearing the same version identifies a different commit d3b84436; this platform uses the audited source commit above, avoiding that ambiguity. Original repository is read-only and unchanged.
