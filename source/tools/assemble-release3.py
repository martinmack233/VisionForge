"""Stage the third-stage DIRECTORY only. Never emits release/source archives."""
from pathlib import Path
import hashlib,json,runpy,shutil,sys,xml.etree.ElementTree as ET
root=Path(__file__).resolve().parent.parent
version=ET.parse(root/'build/Version.props').find('.//VisionProductVersion').text
target=root/'artifacts'/f'VisionStudio-{version}-win-x64'
assert (target/'VisionStudio.exe').is_file(),'Publish binaries first'
original_argv=sys.argv[:]
try:
 sys.argv=[str(root/'tools/assemble-release-prep2.py'),'--stage-only']
 runpy.run_path(str(root/'tools/assemble-release-prep2.py'),run_name='__main__')
except SystemExit as e:
 if e.code not in (None,0):raise
finally:sys.argv=original_argv
bom=json.loads((target/'component-manifest.json').read_text(encoding='utf-8'))
bom['components'].append(dict(id='visionstudio.resident-yolo-bridge',version='2.0.0',protocol=2,transport='private stdio; owned PNG/uncompressed TIFF leases; no listener',cache_models=1,admitted_frames=2,backend='existing ONNX CPU only',redistribution='own connector included; engine/Python/weights external and NOT collectively authorized'))
bom['reference_review']=dict(repository='https://github.com/Augenstern-create/VisionStudio',commit='f7bd87b37c3075c60508cb97a0feaa16576db89d',copied_code=False,new_third_party_dependencies=[],emgu_included=False,vision_ai_dll_included=False)
(target/'component-manifest.json').write_text(json.dumps(bom,ensure_ascii=False,indent=2),encoding='utf-8')
(target/'README.txt').write_text(f'''VisionStudio {version} / Windows x64 / .NET10.0.15 self-contained
Start VisionStudio.exe HERE. See docs/发布准备第三阶段交付说明.md.
Core needs no Python/GPU/vendor SDK. Existing setup/diagnostic and package import remain.
Examples -> 传统视觉检测: aligned difference, color shapes, Hough sequential measurement.
Copy examples to a writable project directory before editing. Difference example intentionally returns NG for2 defects.
Resident YOLO is existing ONNX CPU ONLY; configure the versioned detection component and existing Python environment explicitly.
Private stdio protocol2, bounded1-model cache,2 admitted frames, wait/reject policy; no preview-style detection drops.
No Python environment, YOLO engine, model weights or vendor SDK/driver are included. No automatic pip/download/training/camera connection.
Reports and actual evidence: docs/发布准备第三阶段3A记录.md,3B记录.md,3C验收.md and verification/release3.
This is a local directory delivery, not a GitHub Release or commercial/hardware/all-day certification. No ZIP generated.
''',encoding='utf-8')
packages=target/'ProjectPackages';packages.mkdir(exist_ok=True)
for p in (root/'test-results/release3/examples-fourth').glob('inspection-*.vspkg'):shutil.copy2(p,packages/p.name)
evidence=root/'test-results/release3';verify=target/'verification/release3';verify.mkdir(parents=True,exist_ok=True)
for folder in ['regression-final','regression-delivery','bench-delivery','stress-delivery','ui-final','native-ui','bench-final2','stress-final2','lifecycle-final','faults-final','examples-fourth']:
 source=evidence/folder
 if not source.exists():continue
 for p in source.iterdir():
  if p.is_file() and p.suffix in {'.json','.trx','.png','.jpg','.log','.txt'}:
   d=verify/folder/p.name;d.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(p,d)
for name in ['final-summary.json','source-changes.json','build-final.log','regression-final.log','restore-final.log','published-validation.json','ui-final.log','published-validation.log']:
 if (evidence/name).exists():shutil.copy2(evidence/name,verify/name)
def sha(p):
 with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
for item in json.loads((evidence/'baseline/protected.json').read_text(encoding='utf-8-sig')):
 assert sha(Path(item['path']))==item['sha256'],'Protected resource changed: '+item['path']
assert not any(target.rglob('*.onnx')) and not any(target.rglob('*.pt'))
manifest=target/'file-sha256.json'
manifest.write_text(json.dumps([dict(path=p.relative_to(target).as_posix(),sha256=sha(p),bytes=p.stat().st_size) for p in sorted(target.rglob('*')) if p.is_file() and p!=manifest],ensure_ascii=False,indent=2),encoding='utf-8')
summary=dict(version=version,directory=str(target),exe=str(target/'VisionStudio.exe'),archives_created=False,source_hash=bom['source_identity'],git_commit_created=False,github_release_created=False,clean_windows_verified=False,real_camera_verified=False)
(root/'artifacts/release3-delivery.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(summary,ensure_ascii=False,indent=2))
