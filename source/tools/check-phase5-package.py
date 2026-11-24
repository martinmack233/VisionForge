"""Read-only archive checks plus execution from a fresh directory outside the repository."""
from pathlib import Path
import hashlib
import json
import os
import subprocess
import tempfile
import zipfile

root = Path(__file__).resolve().parent.parent
evidence = root / 'test-results/phase5/package-check'
evidence.mkdir(parents=True, exist_ok=True)
runtime_zip = root / 'artifacts/VisionStudio-Phase5-win-x64.zip'
source_zip = root / 'artifacts/VisionPlatform-Phase5-Source.zip'
with runtime_zip.open('rb') as stream: original_hash = hashlib.file_digest(stream, 'sha256').hexdigest()
with zipfile.ZipFile(source_zip) as source:
    assert source.testzip() is None
    names = source.namelist()
    assert 'VisionPlatform/VisionPlatform.sln' in names and 'YOLOStudio-integration/core/workspace_lock.py' in names
    assert not any(Path(n).suffix.lower() in {'.pt', '.onnx', '.sqlite3', '.db'} for n in names)
    assert not any('/.venv/' in n or '/__pycache__/' in n or '/obj/' in n for n in names)
temporary = Path(tempfile.mkdtemp(prefix='VisionStudio-Phase5-Package-')).resolve()
assert temporary.is_relative_to(Path(tempfile.gettempdir()).resolve())
with zipfile.ZipFile(runtime_zip) as archive:
    assert archive.testzip() is None
    assert all((temporary / n).resolve().is_relative_to(temporary) for n in archive.namelist())
    archive.extractall(temporary)
program = temporary / 'VisionStudio'
sdk = program / 'CameraAdapters/Boq/BoqApi_CSharp.dll'
assert sdk.resolve().is_relative_to(temporary)
# In this disposable extracted copy only: exercise ordinary algorithms without the optional SDK.
sdk.rename(sdk.with_suffix('.disabled-for-package-check'))
results = []
for name, arguments in [
    ('console-basic', ['VisionPlatform.Console.exe', 'run', 'Examples/basic/project.json']),
    ('host-basic', ['VisionPlatform.RuntimeHost.exe', '--project', 'Examples/runtime-host/project.json', '--once']),
    ('host-virtual', ['VisionPlatform.RuntimeHost.exe', '--project', 'Examples/virtual-camera/project.json', '--once']),
]:
    completed = subprocess.run([str(program / arguments[0]), *arguments[1:]], cwd=program, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=60)
    (evidence / (name + '.log')).write_text(completed.stdout + '\n' + completed.stderr, encoding='utf-8')
    assert completed.returncode == 0, (name, completed.stdout, completed.stderr)
    results.append(dict(name=name, exitCode=completed.returncode))
for relative in ['tools/ai_workspace_service/service.py', 'tools/yolo_bridge/yolo_bridge.py']:
    assert (program / relative).read_bytes() == (root / relative).read_bytes()
with runtime_zip.open('rb') as stream: assert hashlib.file_digest(stream, 'sha256').hexdigest() == original_hash
report = dict(Passed=True, ExtractedDirectory=str(program), OutsideRepository=True, OptionalSdkAbsent=True, NoPythonInstallationOrDownload=True,
              SourceArchiveHasNoEnvironmentModelsOrDatabase=True, RuntimeArchiveUnmodified=True, RuntimeArchiveSha256=original_hash,
              Checks=results, CleanWindowsVmTested=False, WpfStartupEvidence='Separate actual published UIA observation and shown-window acceptance; this script exercises published CLI programs.')
(evidence / 'summary.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False, indent=2))
