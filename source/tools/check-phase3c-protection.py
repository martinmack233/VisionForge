"""Check frozen B core, ROI, algorithms, and all existing test sources against the B source archive."""
from pathlib import Path
import hashlib
import json
import zipfile

root = Path(__file__).resolve().parent.parent
archive = root / "artifacts/VisionPlatform-Phase3B-Source.zip"
protected = ("src/VisionPlatform.Contracts/", "src/VisionPlatform.Runtime/", "src/VisionPlatform.Imaging/",
             "src/VisionPlatform.Algorithms/", "src/VisionPlatform.Plugins/", "src/VisionPlatform.Editor.Wpf/ROI/", "tests/")
rows = []
with zipfile.ZipFile(archive) as source:
    for name in source.namelist():
        relative = name.removeprefix("VisionPlatform/")
        if not relative.endswith(".cs") or not relative.startswith(protected):
            continue
        expected = hashlib.sha256(source.read(name)).hexdigest()
        actual = hashlib.sha256((root / relative).read_bytes()).hexdigest() if (root / relative).exists() else None
        rows.append(dict(path=relative, expected=expected, actual=actual, unchanged=actual == expected))
result = dict(checked=len(rows), changed=[row for row in rows if not row["unchanged"]], files=rows)
target = root / "test-results/phase3c/protected-source-check.json"; target.parent.mkdir(parents=True, exist_ok=True)
target.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(dict(checked=len(rows), changed=result["changed"]), ensure_ascii=False, indent=2))
raise SystemExit(0 if not result["changed"] else 1)
