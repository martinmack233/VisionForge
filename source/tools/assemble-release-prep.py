"""Version-driven local packaging. Never writes historical release directories or user data."""
from pathlib import Path
import hashlib, json, os, shutil, sys, xml.etree.ElementTree as ET, zipfile

root=Path(__file__).resolve().parent.parent
version=ET.parse(root/'build/Version.props').find('.//VisionProductVersion').text
assert version.startswith('0.6.0-'), 'Retired Phase1 packager: use tools/assemble-release-prep2.py (no unconfirmed SDK redistribution)'
name=f'VisionStudio-{version}-win-x64'; target=root/'artifacts'/name
assert target.is_dir() and (target/'VisionStudio.exe').is_file(), 'Publish current binaries first'
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
identity=json.loads((root/'build/source-manifest.json').read_text(encoding='utf-8-sig'))
for e in identity['files']:assert sha(root/e['path'])==e['sha256'], 'Source changed; regenerate identity and rebuild: '+e['path']
for e in json.loads((root/'test-results/release-prep/baseline/protected.json').read_text(encoding='utf-8-sig')):assert sha(Path(e['path']))==e['sha256'], 'Protected resource changed: '+e['path']
def copy_changed(source,destination):
    destination=Path(destination)
    if destination.is_file() and sha(Path(source))==sha(destination):return str(destination)
    return shutil.copy2(source,destination)
for source,destination in [('Examples','Examples'),('samples','Samples'),('AIExamples','AIExamples'),('docs','docs'),('third_party/licenses','licenses'),('licenses/phase4b','licenses/phase4b'),('resources/tessdata','tessdata')]:
    shutil.copytree(root/source,target/destination,dirs_exist_ok=True,copy_function=copy_changed)
for relative in ['BoqApi_CSharp.dll','readme.txt']:shutil.copy2(root/'third_party/boq/sdk'/relative,target/'CameraAdapters/Boq'/relative)
sdk=Path(json.loads((root/'test-results/release-prep/toolchain/sdk.json').read_text(encoding='utf-8-sig'))['Directory'])
licenses=target/'licenses/dotnet10';licenses.mkdir(parents=True,exist_ok=True)
for file in ['LICENSE.txt','ThirdPartyNotices.txt']:shutil.copy2(sdk/file,licenses/file)
shutil.copy2(root/'build/source-manifest.json',target/'source-manifest.json')
(target/'README.txt').write_text(f'''VisionStudio {version} / Windows x64 / .NET 10 LTS
启动本目录 VisionStudio.exe。自包含运行时10.0.15；不要求安装.NET SDK。
设置→关于：产品版本、运行时、dirty源码身份、SHA256、组件版本和当前程序目录。
方案示例→集合与子流程：多ROI英文OCR、Blob几何测量。打开时复制为可写工程。
集合结果显示：字段勾选/排序、颜色预览和选择、数值滑块/步进及中文枚举；应用参数后旧结果失效。
子流程：当前Item、0基Index、显式共享Image、真实收集Result。保存问题可点击定位。
AI仍需显式配置现有YOLOStudio和Python；不含环境或权重，不自动下载/训练。
Console/Host/BoqWorker支持 --version。工程仍Schema3、子图Version1，旧Schema1/2可读。
真实相机/串口硬件、系统DPI及全天稳定性未在本轮验证。详见docs/发布准备第一阶段说明.md。
本轮完成后停止，未实施常驻推理或工程打包等第二阶段功能。
''',encoding='utf-8')
runtime_versions={}
for config in list(target.glob('*.runtimeconfig.json'))+list((target/'CameraAdapters/Boq').glob('*.runtimeconfig.json')):
    data=json.loads(config.read_text())['runtimeOptions'];frameworks=data.get('includedFrameworks')
    assert frameworks and all(f['version']=='10.0.15' for f in frameworks),config
    runtime_versions[config.relative_to(target).as_posix()]=frameworks
assert len(runtime_versions)==4
if '--stage-only' in sys.argv:print(json.dumps(dict(staged=str(target),version=version,sourceHash=identity['sha256'],runtimeConfigs=runtime_versions),ensure_ascii=False,indent=2));sys.exit(0)
verification=target/'verification/release-prep';verification.mkdir(parents=True,exist_ok=True)
evidence=root/'test-results/release-prep'
for directory in ['regression-release','migration-comparison','ui-final','native-ui','toolchain']:
    source=evidence/directory
    if source.exists():
        for file in source.iterdir():
            if file.is_file() and file.suffix in {'.trx','.log','.json','.png','.jpg','.txt'}:
                destination=verification/directory/file.name;destination.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(file,destination)
for file in evidence.iterdir():
    if file.is_file() and file.suffix in {'.log','.json','.txt'}:shutil.copy2(file,verification/file.name)
manifest=target/'file-sha256.json';manifest.write_text(json.dumps([dict(path=p.relative_to(target).as_posix(),sha256=sha(p)) for p in sorted(target.rglob('*')) if p.is_file() and p!=manifest],ensure_ascii=False,indent=2),encoding='utf-8')
archives=[]
for filename,base,prefix,source in [(name+'.zip',target,'VisionStudio',False),(f'VisionPlatform-{version}-Source.zip',root,'VisionPlatform',True)]:
    destination=root/'artifacts'/filename;assert not destination.exists(),'Refuse to overwrite archive: '+filename
    with zipfile.ZipFile(destination,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        for directory,folders,files in os.walk(base):
            if source:folders[:]=[f for f in folders if f not in {'.git','.vs','artifacts','test-results','bin','obj','__pycache__','.pytest_cache','.venv','venv','service-data','transfer','wpf-render-transfer'} and not f.startswith('python-service-tmp')]
            for filename2 in sorted(files):
                p=Path(directory)/filename2
                if p.suffix.lower() in {'.pt','.onnx','.db','.sqlite','.sqlite3','.pyc'} or filename2.startswith('.env') or filename2.endswith(('-wal','-shm')):continue
                z.write(('\\\\?\\'+str(p.resolve())) if os.name=='nt' else p,prefix+'/'+p.relative_to(base).as_posix())
    with zipfile.ZipFile(destination) as z:assert z.testzip() is None
    archives.append(dict(path=str(destination),sha256=sha(destination),bytes=destination.stat().st_size))
delivery=dict(version=version,sourceCommit=identity['sourceCommit'],sourceState=identity['sourceState'],sourceHash=identity['sha256'],directory=str(target),runtimeConfigs=runtime_versions,archives=archives,gitCommitCreated=False,githubReleaseCreated=False)
(root/'artifacts/release-prep-delivery.json').write_text(json.dumps(delivery,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(delivery,ensure_ascii=False,indent=2))
