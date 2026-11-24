"""Exercise the staged self-contained release outside the repository, with no optional SDK."""
from pathlib import Path
import json
import shutil
import subprocess
import tempfile

root = Path(__file__).resolve().parent.parent
release = root / 'artifacts/VisionStudio-Collections-20261116-win-x64'
outside = Path(tempfile.mkdtemp(prefix='VisionStudio-Collections-Package-')).resolve()
assert outside.is_relative_to(Path(tempfile.gettempdir()).resolve()) and not outside.is_relative_to(root)
program = outside / 'VisionStudio'
shutil.copytree(release, program)
sdk = program / 'CameraAdapters/Boq/BoqApi_CSharp.dll'
assert sdk.resolve().is_relative_to(outside)
sdk.rename(sdk.with_suffix('.disabled-package-test'))
checks = []
evidence = root / 'test-results/collections/published-cli'
for name, args in [
    ('outside-ocr', ['VisionPlatform.Console.exe', 'run', 'Examples/collection-multi-ocr/project.json']),
    ('outside-blob', ['VisionPlatform.RuntimeHost.exe', '--project', 'Examples/collection-blob-measure/project.json', '--once']),
    ('outside-virtual', ['VisionPlatform.RuntimeHost.exe', '--project', 'Examples/virtual-camera/project.json', '--once']),
]:
    run = subprocess.run([str(program / args[0]), *args[1:]], cwd=program, capture_output=True, encoding='utf-8', errors='replace', timeout=60)
    (evidence / (name + '.log')).write_text(run.stdout + '\n' + run.stderr, encoding='utf-8')
    assert run.returncode == 0, (name, run.stdout, run.stderr)
    checks.append(dict(name=name, exitCode=run.returncode))
for relative in ['tools/ai_workspace_service/service.py', 'tools/yolo_bridge/yolo_bridge.py']:
    assert (program / relative).read_bytes() == (root / relative).read_bytes()
report = dict(passed=True, outsideDirectory=str(program), sdkAbsent=True, pythonInstalled=False, modelDownloaded=False, checks=checks, cleanWindowsVmTested=False)
(evidence / 'outside-package.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False, indent=2))
