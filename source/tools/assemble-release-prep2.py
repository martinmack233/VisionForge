"""Independent release preparation package. No SDK/engine/weights/environment redistribution."""
from pathlib import Path
import hashlib
import json
import os
import shutil
import sys
import xml.etree.ElementTree as ET
import zipfile

r=Path(__file__).resolve().parent.parent
version=ET.parse(r/'build/Version.props').find('.//VisionProductVersion').text
name=f'VisionStudio-{version}-win-x64';target=r/'artifacts'/name
assert (target/'VisionStudio.exe').is_file(),'Publish first'

def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()

identity=json.loads((r/'build/source-manifest.json').read_text(encoding='utf-8-sig'))
for item in identity['files']:assert sha(r/item['path'])==item['sha256'],'Source changed after build: '+item['path']
protected=json.loads((r/'test-results/release-prep2/baseline/protected.json').read_text(encoding='utf-8'))
for item in protected:assert sha(Path(item['path']))==item['sha256'],'Protected resource changed: '+item['path']

def copy_same_safe(source,destination):
    d=Path(destination)
    if d.is_file() and sha(Path(source))==sha(d):return str(d)
    return shutil.copy2(source,destination)

for source,dest in [('Examples','Examples'),('samples','Samples'),('AIExamples','AIExamples'),('docs','docs'),('components','components'),('third_party/licenses','licenses'),('licenses/phase4b','licenses/phase4b')]:
    shutil.copytree(r/source,target/dest,dirs_exist_ok=True,copy_function=copy_same_safe)
# Own business connector scripts are cheap/optional; neither Qt nor training engines/data are included.
sdk=Path(os.environ.get('DOTNET_ROOT',''))
assert (sdk/'LICENSE.txt').is_file(),'Set DOTNET_ROOT to the verified .NET10 SDK for notices'
(target/'licenses/dotnet10').mkdir(parents=True,exist_ok=True)
for file in ['LICENSE.txt','ThirdPartyNotices.txt']:shutil.copy2(sdk/file,target/'licenses/dotnet10'/file)
shutil.copy2(r/'build/source-manifest.json',target/'source-manifest.json')
for component in ['CvInspect','Nodify']:
    shutil.copy2(r/'third_party'/component/'LICENSE',target/'licenses'/(component+'-LICENSE.txt'))
assert not (target/'CameraAdapters/Boq/BoqApi_CSharp.dll').exists(),'Unconfirmed SDK must not enter this release'
assert not list(target.rglob('*.onnx')) and not list(target.rglob('*.pt')),'Weights must not enter default release'
assert not list(target.rglob('*.db')) and not list(target.rglob('*.sqlite'))
recipe=json.loads((r/'components/ai-runtime/recipe.json').read_text(encoding='utf-8'))
components=[
 dict(id='visionstudio.core',version=version,protocols={'runtime_host':1,'project_schema':[1,2,3],'package':1,'subflow':1},runtime='10.0.15',python_required=False,backends=['traditional-opencv','ocr-tesseract','virtual-camera'],included=True),
 dict(id='visionstudio.ai-detection',version=recipe['component_version'],protocol=1,python_minor=recipe['python_minor'],dependencies=recipe['dependencies'],backends=recipe['supported_backends'],included='recipe/probe/explicit local installer; engine and Python NOT included',source_files=recipe['source_files'],redistribution='unconfirmed; no third-party engine or models in default release'),
 dict(id='visionstudio.ai-training-workspace',version='connector-1.0.0',protocol=1,python_minor=recipe['python_minor'],enabled_by_default=False,included='WPF pages and own service connector only',business='explicitly configured existing YOLOStudio TrainingService/runner/train_worker; original .venv and data remain external',redistribution='source/engine/PySide6/model rights must be independently confirmed'),
 dict(id='visionstudio.boq-adapter',version=version,worker_runtime='10.0.15',included='adapter and isolation worker; NO vendor SDK/driver',hardware_verified=False,redistribution='vendor SDK/driver unconfirmed and excluded')]
dependencies={}
for lock in (r/'src').glob('*/packages.lock.json'):
    data=json.loads(lock.read_text(encoding='utf-8-sig'))
    for framework,items in data.get('dependencies',{}).items():
        for package,item in items.items():
            if item.get('type')=='Project' or 'resolved' not in item:continue
            ver=item['resolved'];key=(package,ver)
            if key in dependencies:continue
            nuspec=Path.home()/'.nuget/packages'/package.lower()/ver.lower()/(package.lower()+'.nuspec')
            license_value='unconfirmed';project_url=''
            if nuspec.exists():
                meta=ET.parse(nuspec).getroot().find('{*}metadata')
                lic=meta.find('{*}license');url=meta.find('{*}projectUrl')
                license_value=lic.text if lic is not None else 'unconfirmed'
                project_url=url.text if url is not None else ''
            dependencies[key]=dict(name=package,version=ver,source=project_url or 'NuGet locked package',license=license_value,redistribution='license metadata recorded; retain notices and review native/transitive terms, not a commercial authorization')
third_party=list(dependencies.values())+[
 dict(name='.NET runtime',version='10.0.15',source='https://github.com/dotnet/runtime',license='MIT and bundled third-party notices',redistribution='notices included'),
 dict(name='Nodify',version='7.3',source='https://github.com/miroiu/nodify',license='MIT',redistribution='source/notice retained'),
 dict(name='CvInspect',version='0.34.0+0eddfbaa5195753c2a589fef82555bdf6455da80',source='https://github.com/cmir79/CvInspect',license='MIT',redistribution='reviewed source/notice retained'),
 dict(name='Tesseract native / Leptonica / codecs / eng.traineddata',version='existing pinned x64 pair / eng',source='licenses/phase4b',license='Apache-2.0 / BSD / bundled codec terms',redistribution='individual notices retained; no other language data bundled'),
 dict(name='BoqApi_CSharp.dll / vendor driver',version='owner supplied SDK archive',source='local owner archive (not included)',license='unconfirmed proprietary',redistribution='EXCLUDED'),
 dict(name='YOLOStudio business source',version='hash-pinned recipe',source='owner existing repository; relative paths/hashes in recipe',license='unconfirmed ownership/re-distribution',redistribution='EXCLUDED; explicit local extraction only'),
 dict(name='Ultralytics',version=recipe['dependencies']['ultralytics'],source='https://www.ultralytics.com/license',license='upstream AGPL-3.0 / Enterprise options; actual owner authorization unconfirmed',redistribution='EXCLUDED; engine usage still needs applicable terms review, out-of-process is not a license exemption'),
 dict(name='PySide6 / Qt',version='existing training environment',source='owner existing environment',license='LGPL/GPL/commercial terms require independent review',redistribution='EXCLUDED'),
 dict(name='YOLO/PT/ONNX model weights',version='per-artifact hash/deployment schema1',source='per-model owner/source',license='unconfirmed per model',redistribution='EXCLUDED by default; export requires explicit carry-rights acknowledgement'),
 dict(name='torch / torchvision / numpy / Pillow / tifffile / ONNX Runtime',version=recipe['dependencies'],source='existing external Python environment',license='per-package and native/transitive licenses not collectively cleared',redistribution='EXCLUDED; no environment copy or pip installation')]
bom=dict(manifest_version=1,product_version=version,source_identity=identity['sha256'],source_state=identity['sourceState'],components=components,third_party=third_party,commercial_authorization_claimed=False,clean_windows_verified=False)
(target/'component-manifest.json').write_text(json.dumps(bom,ensure_ascii=False,indent=2),encoding='utf-8')
(target/'machine-configuration.example.json').write_text(json.dumps(dict(Version=1,SetupCompleted=False,DetectionPython='',DetectionComponentRoot='',DetectionBackend='onnx-cpu',TrainingEnabled=False,TrainingPython='',TrainingRoot='',BoqSdkPath='',BoqWorkerPath=''),indent=2),encoding='utf-8')
(target/'README.txt').write_text(f'''VisionStudio {version} / Windows x64 / .NET10.0.15 self-contained
Start VisionStudio.exe in this directory. The core needs no Python, training checkout or GPU.
First start: graphical setup. Reopen from Deployment menu or Home/Settings -> setup/diagnostics.
Traditional examples: Examples. Portable project packages: ProjectPackages/*.vspkg.
Package import always creates a NEW directory. Models are external by default and must be relocated with matching hashes.
AI detection: configure a verified existing Python 3.11 environment, then explicitly create a NEW local detection component from an authorized source using the recipe. Once installed, detection does NOT require the training repository path. No Python environment, engine or weights are redistributed here.
AI training: optional; explicitly configure the existing YOLOStudio and its original environment. Never automatic.
Machine config is user-local. All hosts accept --machine-config absolute.json / VISIONSTUDIO_MACHINE_CONFIG. Do not put environments/SDK paths into portable projects.
Vendor SDK/driver are NOT included. Core and virtual camera remain available.
See docs/发布准备第二阶段使用说明.md, component-manifest.json and verification for actual results and limitations.
Clean Windows, real camera/serial and commercial authorization are NOT certified by this local delivery.
''',encoding='utf-8')
if '--stage-only' in sys.argv:
    print(json.dumps(dict(staged=str(target),version=version,sourceHash=identity['sha256'],vendor_sdk_included=False,python_environment_included=False)));sys.exit(0)
e=r/'test-results/release-prep2';verify=target/'verification/release-prep2';verify.mkdir(parents=True,exist_ok=True)
for folder in ['regression-final','ui-final','native-ui']:
    source=e/folder
    if source.exists():
        for p in source.iterdir():
            if not p.is_file() or p.suffix not in {'.trx','.png','.jpg','.log','.txt'}:continue
            d=verify/folder/p.name;d.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(p,d)
for filename in ['final-summary.json','real-engine-comparison-final.json','component-probe.log','component-model-check.log','regression-final.log','build-final.log','published-cli.json','source-changes.json']:
    if (e/filename).exists():shutil.copy2(e/filename,verify/filename)
manifest=target/'file-sha256.json';manifest.write_text(json.dumps([dict(path=p.relative_to(target).as_posix(),sha256=sha(p)) for p in sorted(target.rglob('*')) if p.is_file() and p!=manifest],ensure_ascii=False,indent=2),encoding='utf-8')
archives=[]
for filename,base,prefix,source in [(name+'.zip',target,'VisionStudio',False),(f'VisionPlatform-{version}-Source.zip',r,'VisionPlatform',True)]:
    dest=r/'artifacts'/filename;assert not dest.exists(),'Refuse to overwrite '+str(dest)
    with zipfile.ZipFile(dest,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        for directory,folders,files in os.walk(base):
            if source:
                folders[:]=[f for f in folders if f not in {'.git','.vs','artifacts','test-results','bin','obj','__pycache__','.pytest_cache','.venv','venv','service-data','transfer','wpf-render-transfer'} and not f.startswith('python-service-tmp')]
                if Path(directory).relative_to(r).as_posix().startswith('third_party/boq/sdk'):folders[:]=[];continue
            for filename2 in sorted(files):
                p=Path(directory)/filename2
                if p.suffix.lower() in {'.pt','.onnx','.db','.sqlite','.sqlite3','.pyc'} or source and p.suffix.lower() in {'.dll','.exe','.zip'} or filename2.startswith('.env') or filename2.endswith(('-wal','-shm')):continue
                z.write(('\\\\?\\'+str(p.resolve())) if os.name=='nt' else p,prefix+'/'+p.relative_to(base).as_posix())
    with zipfile.ZipFile(dest) as z:assert z.testzip() is None
    archives.append(dict(path=str(dest),sha256=sha(dest),bytes=dest.stat().st_size))
delivery=dict(version=version,directory=str(target),sourceHash=identity['sha256'],sourceState=identity['sourceState'],archives=archives,vendor_sdk_included=False,python_environment_included=False,model_weights_included=False,clean_windows_verified=False,git_commit_created=False,github_release_created=False)
(r/'artifacts/release-prep2-delivery.json').write_text(json.dumps(delivery,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(delivery,ensure_ascii=False,indent=2))
