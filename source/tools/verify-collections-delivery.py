"""Actual published Console/Host, old-test preservation, and protection evidence."""
from pathlib import Path
import hashlib
import json
import subprocess
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent.parent
release = root / 'artifacts/VisionStudio-Collections-20261116-win-x64'
evidence = root / 'test-results/collections'
cli = evidence / 'published-cli'
cli.mkdir(parents=True, exist_ok=True)
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def suite(folder):
    names, counters = set(), []
    for p in folder.glob('*.trx'):
        tree = ET.parse(p)
        names.update(r.attrib['testName'] for r in tree.findall('t:Results/t:UnitTestResult', ns))
        counters.append({k: int(v) for k, v in tree.find('t:ResultSummary/t:Counters', ns).attrib.items()})
    return names, counters
old, old_counts = suite(root / 'test-results/phase5-ai-fix/final-validation-r3')
current, counts = suite(evidence / 'regression-final-r5')
assert len(counts) == 9 and all(c['total'] == c['passed'] and c['failed'] == c['notExecuted'] == 0 for c in counts)
assert old <= current and sum(c['passed'] for c in old_counts) == 690
assert json.loads((evidence / 'ui-final/summary.json').read_text())['Passed']
python = ET.parse(evidence / 'python.xml').getroot().find('testsuite').attrib
assert int(python['tests']) == 13 and int(python['failures']) == int(python['errors']) == int(python['skipped']) == 0
checks = []
for case, expected in [('collection-multi-ocr', ['VISION', '123456', 'PART42']), ('collection-blob-measure', [10, 15, 25])]:
    outputs = {}
    for backend, exe, args in [('console', 'VisionPlatform.Console.exe', ['run']), ('host', 'VisionPlatform.RuntimeHost.exe', ['--project'])]:
        project = release / 'Examples' / case / 'project.json'
        run = subprocess.run([str(release / exe), *args, str(project), *(['--once'] if backend == 'host' else [])], cwd=release, capture_output=True, encoding='utf-8', errors='replace', timeout=60)
        (cli / f'{backend}-{case}.log').write_text(run.stdout + '\n' + run.stderr, encoding='utf-8')
        assert run.returncode == 0, (case, backend, run.stdout, run.stderr)
        if backend == 'host':
            response = json.loads(run.stdout[run.stdout.index('{'):])
            assert response['Success'] and response['Payload']['Success']
            node = next(n for n in response['Payload']['Nodes'] if n['NodeId'] == 'each')
            result = node['Outputs']['Results']['Value']
            fields = [i['Fields'] for i in result['Items']]
            actual = [f['Text']['Text'] for f in fields] if case.endswith('ocr') else [f['Number']['Number'] for f in fields]
            assert actual == expected, actual
            outputs['actual'] = actual
        else:
            assert 'Complete=True, Cancelled=False, Completed=3/3' in run.stdout
            for value in expected: assert str(value) in run.stdout
    checks.append(dict(case=case, consoleExit=0, hostExit=0, **outputs))
# Verify the actual native UI-saved formal subflow with a non-UI host process.
native = evidence / 'native-ui/ocr-project/project.json'
run = subprocess.run([str(release / 'VisionPlatform.RuntimeHost.exe'), '--project', str(native), '--once'], cwd=release, capture_output=True, encoding='utf-8', timeout=60)
(cli / 'host-native-ui-saved-project.log').write_text(run.stdout + '\n' + run.stderr, encoding='utf-8')
assert run.returncode == 0
protected = []
for item in json.loads((evidence / 'baseline/protected.json').read_text(encoding='utf-8-sig')):
    with Path(item['Path']).open('rb') as stream: observed = hashlib.file_digest(stream, 'sha256').hexdigest()
    assert observed.lower() == item['Hash'].lower(), item['Path']
    protected.append(dict(path=item['Path'], unchanged=True, sha256=observed))
summary = dict(dotnetPassed=sum(c['passed'] for c in counts), dotnetFailed=0, dotnetSkipped=0, original690Preserved=True, pythonPassed=13,
               publishedChecks=checks, nativeUiSavedProjectRunsInHost=True, protected=protected,
               realOcr=True, syntheticImages=True, realYoloModelTested=False, gpuUsed=False, trainingStarted=False, physicalCameraOpened=False,
               nativeUiStoppedByPhysicalEscape=True, allDayStabilityVerified=False, cleanWindowsVmVerified=False, systemDpiSwitchVerified=False)
(cli / 'summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(summary, ensure_ascii=False, indent=2))
