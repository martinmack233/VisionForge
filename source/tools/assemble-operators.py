"""Stage the operator expansion directory only; never archives or alters old releases."""
from pathlib import Path
import hashlib,json,os,shutil,xml.etree.ElementTree as ET
root=Path(__file__).resolve().parent.parent
version=ET.parse(root/'build/Version.props').find('.//VisionProductVersion').text
target=root/'artifacts'/f'VisionStudio-{version}-win-x64'
evidence=root/'test-results/operators-20261117'
def sha(p):
 with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
assert (target/'VisionStudio.exe').is_file(),'Publish to a NEW version directory first'
assert version=='0.6.3-preview.1','Review staging paths for next version'
identity=json.loads((root/'build/source-manifest.json').read_text(encoding='utf-8-sig'))
for row in identity['files']:assert sha(root/row['path'])==row['sha256'],'Stale build identity: '+row['path']
for row in json.loads((evidence/'baseline/protected.json').read_text(encoding='utf-8-sig')):assert sha(Path(row['path']))==row['sha256'],'Protected resource changed: '+row['path']
for source,destination in [('Examples','Examples'),('samples','Samples'),('AIExamples','AIExamples'),('docs','docs'),('components','components'),('third_party/licenses','licenses'),('licenses/phase4b','licenses/phase4b')]:shutil.copytree(root/source,target/destination,dirs_exist_ok=True)
for component in ['CvInspect','Nodify']:shutil.copy2(root/'third_party'/component/'LICENSE',target/'licenses'/(component+'-LICENSE.txt'))
sdk=Path(os.environ['DOTNET_ROOT']);(target/'licenses/dotnet10').mkdir(parents=True,exist_ok=True)
for file in ['LICENSE.txt','ThirdPartyNotices.txt']:shutil.copy2(sdk/file,target/'licenses/dotnet10'/file)
shutil.copy2(root/'build/source-manifest.json',target/'source-manifest.json')
# Read the reviewed component/license baseline without changing it. No new external dependency.
bom=json.loads((root/'artifacts/VisionStudio-0.6.2-preview.1-win-x64/component-manifest.json').read_text(encoding='utf-8-sig'))
bom.update(product_version=version,source_identity=identity['sha256'],source_state=identity['sourceState'])
for component in bom['components']:
 if component['id'] in ['visionstudio.core','visionstudio.boq-adapter']:component['version']=version
bom['operator_expansion']=dict(new_nodes=24,new_third_party_dependencies=[],modbus='own C# over existing TCP/System.IO.Ports',modbus_hardware_verified=False,project_schema=3,package_schema=1,new_resource_kind='DataFile: bounded local FileRead.Path',float_contract='FloatPlaneFrame signed Float32; not Mono8',matrix_contract='NumericMatrix Float64 / ProjectiveTransform2D forward 3x3',old_nodes_retained=True,reverse_compatibility_claimed=False)
(target/'component-manifest.json').write_text(json.dumps(bom,ensure_ascii=False,indent=2),encoding='utf-8')
(target/'README.txt').write_text(f'''VisionStudio {version} / Windows x64 / self-contained .NET 10.0.15
Start VisionStudio.exe in THIS directory. About shows dirty/source hash and actual process location.
See docs/算子扩充-0.6.3使用与验收.md for contracts, ranges, coordinates, compatibility and evidence.
Examples -> 算子扩充: 4 offline image/matrix/data examples; 2 explicit finite loopback examples.
Use 启动本地通信测试 for TCP/Modbus; RTU协议模拟 never opens COM. Default Modbus example is read-only.
Console operator-rtu-sim explicitly runs protocol simulation. No production endpoints in acceptance.
Core needs no Python/GPU/vendor SDK. Existing YOLO ONNX CPU retained; no new backend.
NO environments, weights, vendor SDK/driver or business databases included. No pip/download/training/automatic connection.
Old releases retained. No ZIP. No Git commit/push/GitHub Release.
Physical serial/Modbus/camera/PLC, clean Windows, full high-DPI and long-term performance NOT certified.
''',encoding='utf-8')
packages=target/'ProjectPackages';packages.mkdir(exist_ok=True)
for directory in [evidence/'examples-final',evidence/'communications-final']:
 for p in directory.glob('*.vspkg'):shutil.copy2(p,packages/p.name)
# Communication packages contain ephemeral test endpoints, not persistent device configuration.
verify=target/'verification/operators';verify.mkdir(parents=True,exist_ok=True)
for folder in ['regression-confirmed','examples-final','communications-final','ui-final','native-ui','published']:
 source=evidence/folder
 if source.exists():
  for p in source.iterdir():
   if p.is_file() and p.suffix.lower() in {'.json','.trx','.png','.jpg','.log','.txt'}:
    d=verify/folder/p.name;d.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(p,d)
for name in ['final-summary.json','source-changes.json','build-final.log','regression-confirmed.log','restore-final.log','dependency-comparison.json','ui-final.log','examples-final.log','communications-final.log']:
 if (evidence/name).is_file():shutil.copy2(evidence/name,verify/name)
failures=verify/'development-failures';failures.mkdir(exist_ok=True)
for name in ['A-build.log','B-regression-third.log','C-first.log','subflow-first.log','ui-second.log','regression-delivery.log']:
 if (evidence/name).is_file():shutil.copy2(evidence/name,failures/name)
if (evidence/'ui-second/harness-interruption.txt').exists():shutil.copy2(evidence/'ui-second/harness-interruption.txt',failures/'ui-harness-interruption.txt')
assert not any(target.rglob('*.pt')) and not any(target.rglob('*.onnx'))
assert not any(target.rglob('*.db')) and not any(target.rglob('*.sqlite'))
assert not (target/'CameraAdapters/Boq/BoqApi_CSharp.dll').exists()
manifest=target/'file-sha256.json'
manifest.write_text(json.dumps([dict(path=p.relative_to(target).as_posix(),sha256=sha(p),bytes=p.stat().st_size) for p in sorted(target.rglob('*')) if p.is_file() and p!=manifest],ensure_ascii=False,indent=2),encoding='utf-8')
summary=dict(version=version,directory=str(target),exe=str(target/'VisionStudio.exe'),source_hash=identity['sha256'],source_state=identity['sourceState'],archives_created=False,git_commit_created=False,github_release_created=False,clean_windows_verified=False,physical_hardware_verified=False)
(root/'artifacts/operators-0.6.3-delivery.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(summary,ensure_ascii=False,indent=2))
