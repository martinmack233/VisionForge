"""Check the immutable Phase 4A baseline without changing the working tree."""
import hashlib
import json
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parent.parent
baseline = "visionstudio-phase4a"
protected = ("src/VisionPlatform.Runtime/", "src/VisionPlatform.Contracts/", "src/VisionPlatform.Imaging/",
             "src/VisionPlatform.Algorithms/", "src/VisionPlatform.Plugins/", "src/VisionPlatform.Editor.Wpf/ROI/", "tests/")
approved = {"src/VisionPlatform.Plugins/Samples/Phase4AExampleFactory.cs": "Add optional ExternalDependencies manifest metadata only; existing A projects unchanged"}

def git(*args): return subprocess.check_output(["git", *args], cwd=root)
def digest(data): return hashlib.sha256(data.replace(b"\r\n", b"\n")).hexdigest()
names = git("ls-tree", "-r", "--name-only", baseline).decode("utf-8").splitlines()
rows = []
for name in names:
    if not name.endswith(".cs") or not name.startswith(protected): continue
    old = digest(git("show", baseline + ":" + name))
    new = digest((root / name).read_bytes()) if (root / name).exists() else None
    rows.append(dict(path=name, before=old, after=new, unchanged=old == new, approved=approved.get(name)))
unexpected = [r for r in rows if not r["unchanged"] and not r["approved"]]
result = dict(baseline=baseline, commit=git("rev-parse", baseline).decode().strip(), checked=len(rows),
              unchanged=sum(r["unchanged"] for r in rows), compatibleExtensions=[r for r in rows if not r["unchanged"] and r["approved"]], unexpected=unexpected, files=rows)
target = root / "test-results/phase4b/protected-source-check.json"
target.parent.mkdir(parents=True, exist_ok=True)
target.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({k: v for k, v in result.items() if k != "files"}, ensure_ascii=False, indent=2))
raise SystemExit(bool(unexpected))
