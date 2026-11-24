"""Record actual restored NuGet versions, license metadata and native hashes; standard library only."""
from pathlib import Path
import hashlib, json, os, xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent.parent
cache = Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget" / "packages")))
source = json.loads((root / "docs/packages.json").read_text(encoding="utf-8-sig"))
packages = {}
for project in source["projects"]:
    scope = "test" if "/tests/" in project["path"].replace("\\", "/") else "runtime"
    for framework in project["frameworks"]:
        for item in framework.get("topLevelPackages", []) + framework.get("transitivePackages", []):
            key = (item["id"], item["resolvedVersion"])
            packages.setdefault(key, set()).add(scope)
rows = []
for (name, version), scopes in sorted(packages.items()):
    folder = cache / name.lower() / version.lower()
    nuspec = ET.parse(next(folder.glob("*.nuspec"))).getroot()
    def element(name):
        return next((v for v in nuspec.iter() if v.tag.split("}")[-1] == name), None)
    license = element("license")
    url = element("projectUrl")
    license_url = element("licenseUrl")
    package_file = next(folder.glob("*.nupkg"))
    rows.append(dict(id=name, version=version, scope=sorted(scopes),
        licenseType=license.get("type") if license is not None else None,
        license=license.text if license is not None else None,
        licenseUrl=license_url.text if license_url is not None else None,
        projectUrl=url.text if url is not None else None,
        nupkgSha256=hashlib.sha256(package_file.read_bytes()).hexdigest()))
native = cache / "opencvsharp4.runtime.win.slim/4.13.0.20260627/runtimes/win-x64/native"
native_rows = [dict(file=p.name, bytes=p.stat().st_size, sha256=hashlib.sha256(p.read_bytes()).hexdigest(),
    distribution="excluded optional video plugin" if "ffmpeg" in p.name else "included") for p in native.glob("*.dll")]
(root / "docs/sbom-nuget.json").write_text(json.dumps(dict(packages=rows, native=native_rows), indent=2), encoding="utf-8")
print(f"Recorded {len(rows)} exact package/version entries and {len(native_rows)} native files.")
