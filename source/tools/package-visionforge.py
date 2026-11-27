"""Stage/ZIP VisionForge from current source. No dependency on previous install directories."""
from pathlib import Path
import argparse,hashlib,json,os,shutil,subprocess,xml.etree.ElementTree as ET,zipfile
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--stage-only',action='store_true');parser.add_argument('--zip-only',action='store_true');parser.add_argument('--dotnet-root',default=os.environ.get('DOTNET_ROOT',''));parser.add_argument('--evidence-root',help='Opt in to staging local acceptance evidence. Omit for customer packages.');args=parser.parse_args()
root=Path(__file__).resolve().parent.parent;props=ET.parse(root/'build/Version.props');version=props.find('.//VisionProductVersion').text;name=props.find('.//VisionProductName').text
folder=f'VisionForge-{version}-win-x64';target=root/'artifacts'/folder
assert (target/'VisionStudio.exe').is_file(),'Publish to a new VisionForge directory first'
def sha(p):
 with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
identity=json.loads((root/'build/source-manifest.json').read_text(encoding='utf-8-sig'))
for row in identity['files']:assert sha(root/row['path'])==row['sha256'],'Source changed after build: '+row['path']
runtime_config=json.loads((target/'VisionStudio.runtimeconfig.json').read_text(encoding='utf-8'))['runtimeOptions']
included_frameworks={framework['name']:framework['version'] for framework in runtime_config['includedFrameworks']}
runtime_version=included_frameworks['Microsoft.NETCore.App']
assert included_frameworks['Microsoft.WindowsDesktop.App']==runtime_version,'Mismatched desktop and core runtime versions'
if not args.zip_only:
 shutil.copy2(target/'VisionStudio.exe',target/'VisionForge.exe')
 for source,dest in [('Examples','Examples'),('samples','Samples'),('AIExamples','AIExamples'),('docs','docs'),('components','components'),('third_party/licenses','licenses'),('licenses/phase4b','licenses/phase4b')]:shutil.copytree(root/source,target/dest,dirs_exist_ok=True)
 for component in ['CvInspect','Nodify']:shutil.copy2(root/'third_party'/component/'LICENSE',target/'licenses'/(component+'-LICENSE.txt'))
 shutil.copy2(root/'third_party/CvInspect/THIRD-PARTY-NOTICES.md',target/'licenses/CvInspect-THIRD-PARTY-NOTICES.md')
 nuget_cache=Path(os.environ.get('NUGET_PACKAGES') or Path.home()/'.nuget/packages')
 for package,package_version in [('Microsoft.Extensions.Logging.Abstractions','8.0.3'),('Microsoft.Extensions.DependencyInjection.Abstractions','8.0.2')]:
  notices=target/'licenses'/f'{package}-{package_version}'
  notices.mkdir(parents=True,exist_ok=True)
  for filename in ['LICENSE.TXT','THIRD-PARTY-NOTICES.TXT']:
   shutil.copy2(nuget_cache/package.lower()/package_version/filename,notices/filename)
 sdk=Path(args.dotnet_root);assert (sdk/'LICENSE.txt').is_file(),'Specify --dotnet-root for .NET notices; no SDK is installed automatically'
 (target/'licenses/dotnet10').mkdir(parents=True,exist_ok=True)
 for f in ['LICENSE.txt','ThirdPartyNotices.txt']:shutil.copy2(sdk/f,target/'licenses/dotnet10'/f)
 shutil.copy2(root/'build/source-manifest.json',target/'source-manifest.json')
 bom=json.loads((root/'build/component-baseline.json').read_text(encoding='utf-8-sig'));bom.update(product_name=name,product_version=version,source_identity=identity['sha256'],source_state=identity['sourceState'])
 for c in bom['components']:
  if c['id'] in ['visionstudio.core','visionstudio.boq-adapter']:c['version']=version
  if c['id']=='visionstudio.core':c['runtime']=runtime_version
  if c['id']=='visionstudio.boq-adapter':
   worker_config=json.loads((target/'CameraAdapters/Boq/VisionPlatform.BoqWorker.runtimeconfig.json').read_text(encoding='utf-8'))['runtimeOptions']
   c['worker_runtime']=next(f['version'] for f in worker_config['includedFrameworks'] if f['name']=='Microsoft.NETCore.App')
 for package in bom['third_party']:
  if package['name']=='.NET runtime':package['version']=runtime_version
 bom['brand_compatibility']=dict(primary_launcher='VisionForge.exe',legacy_launcher='VisionStudio.exe',internal_assemblies_renamed=False,configuration_root_unchanged='%LOCALAPPDATA%/VisionStudio',project_schema_changed=False)
 (target/'component-manifest.json').write_text(json.dumps(bom,ensure_ascii=False,indent=2),encoding='utf-8')
 packages=target/'ProjectPackages';packages.mkdir(exist_ok=True)
 for example in ['operators-contrast-edges','operators-matrix-warp','operators-random-logic','operators-file-read','operators-tcp-request','operators-modbus-tcp','collection-multi-ocr','collection-blob-measure','template-follow','image-filter-methods','contour-envelope-geometry']+[p.name for p in sorted((root/'Examples').glob('beginner-*')) if p.is_dir()]:
  output=packages/(example+'.vspkg')
  if output.exists():continue
  p=subprocess.run([str(target/'VisionPlatform.Console.exe'),'package-export',str(target/'Examples'/example/'project.json'),str(output)],cwd=target,capture_output=True,text=True,encoding='utf-8',timeout=30)
  assert p.returncode==0,f'{example}: {p.stderr}'
 (target/'README.txt').write_text(f'''{name} {version} / Windows x64 / .NET {runtime_version} self-contained
启动本目录 VisionForge.exe。VisionStudio.exe为兼容启动名，内部DLL与配置目录保持原标识。
先读 docs/使用与接入/00-文档入口与能力边界.md。运行不需要旁边放源码目录。
核心传统视觉与文件夹虚拟相机不需要Python/GPU/厂商SDK。AI需显式配置现有合法环境与匹配部署模型。
不会自动pip、下载模型、训练、连接硬件或发送生产通信。
不含PT/ONNX模型、Python环境、YOLO引擎、博峤SDK/驱动或业务数据库，授权义务分别确认。
方案示例的通信需显式本机测试；静态通信包端口为1，默认Modbus示例只读。
新旧启动名共用原有 %LOCALAPPDATA%\\VisionStudio 设置，不搬移或清空用户数据。
通过 GitHub 发行页查看本版本的验收范围与 SHA256。组件及许可证见 component-manifest.json 和 licenses。
''',encoding='utf-8')
if args.evidence_root:
 verify=target/'verification/brand-release';verify.mkdir(parents=True,exist_ok=True);evidence=root/args.evidence_root
 for child in ['regression-final','examples-final','communications-final','ui-final','native-ui','published']:
  source=evidence/child
  if source.exists():
   for f in source.iterdir():
    if f.is_file() and f.suffix.lower() in {'.json','.trx','.png','.jpg','.txt','.log'} and f.name!='machine.json':shutil.copy2(f,verify/(child+'-'+f.name))
 for filename in ['validation-summary.json','cleanup-result.json','build-final.log','restore-final.log','regression-final.log','release-note.md']:
  if (evidence/filename).is_file():shutil.copy2(evidence/filename,verify/filename)
excluded_extensions={'.pt','.pth','.onnx','.db','.sqlite','.sqlite3','.pfx','.p12','.key'}
excluded_names={'boqapi_csharp.dll','machine-v1.json','workspace-state.json','.venv','__pycache__','.git'}
for entry in target.rglob('*'):
 assert not entry.is_symlink() and not entry.is_junction(),f'Linked package entry: {entry}'
 assert entry.suffix.lower() not in excluded_extensions and entry.name.lower() not in excluded_names and not entry.name.lower().startswith('.env'),f'Excluded package entry: {entry}'
manifest=target/'file-sha256.json';manifest.write_text(json.dumps([dict(path=f.relative_to(target).as_posix(),sha256=sha(f),bytes=f.stat().st_size) for f in sorted(target.rglob('*')) if f.is_file() and f!=manifest],ensure_ascii=False,indent=2),encoding='utf-8')
archive=root/'artifacts'/(folder+'.zip')
if not args.stage_only:
 assert not archive.exists(),'Refuse to overwrite archive; validate any previous unpublished output explicitly'
 with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
  for f in sorted(target.rglob('*')):
   if f.is_file():z.write(f,folder+'/'+f.relative_to(target).as_posix())
 with zipfile.ZipFile(archive) as z:assert z.testzip() is None,'Archive CRC failure'
 (archive.parent/(archive.name+'.sha256')).write_text(sha(archive)+'  '+archive.name+'\n',encoding='ascii')
summary=dict(product_name=name,version=version,directory=str(target),exe=str(target/'VisionForge.exe'),legacy_exe=str(target/'VisionStudio.exe'),source_hash=identity['sha256'],source_commit=identity['sourceCommit'],source_state=identity['sourceState'],archive=str(archive) if archive.exists() else None,archive_sha256=sha(archive) if archive.exists() else None,clean_windows_verified=False,real_hardware_verified=False)
(root/'artifacts'/(folder+'-delivery.json')).write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(summary,ensure_ascii=False,indent=2))
