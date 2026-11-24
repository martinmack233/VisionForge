"""Stage/seal this release only. Existing archives and business data are read-only."""
from pathlib import Path
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile

root = Path(__file__).resolve().parent.parent
name = 'VisionStudio-Collections-20261116-win-x64'
target = root / 'artifacts' / name
assert target.is_dir() and (target / 'VisionStudio.exe').is_file()
assert target.resolve().parent == (root / 'artifacts').resolve()
def sha(p):
    with p.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()
protected = json.loads((root / 'test-results/collections/baseline/protected.json').read_text(encoding='utf-8-sig'))
for entry in protected:
    assert sha(Path(entry['Path'])).lower() == entry['Hash'].lower(), entry['Path']
for source, destination in [('Examples', 'Examples'), ('samples', 'Samples'), ('third_party/licenses', 'licenses'), ('licenses/phase4b', 'licenses/phase4b'), ('resources/tessdata', 'tessdata'), ('AIExamples', 'AIExamples'), ('tools/yolo_bridge', 'tools/yolo_bridge')]:
    shutil.copytree(root / source, target / destination, dirs_exist_ok=True, ignore=shutil.ignore_patterns('__pycache__', '.pytest_cache', '*.pyc'))
# Previously reviewed licenses only; never copy old application binaries over current publish output.
shutil.copytree(root / 'artifacts/VisionStudio-Phase5-win-x64/licenses', target / 'licenses', dirs_exist_ok=True)
shutil.copy2(root / 'third_party/boq/sdk/BoqApi_CSharp.dll', target / 'CameraAdapters/Boq/BoqApi_CSharp.dll')
shutil.copy2(root / 'third_party/boq/sdk/readme.txt', target / 'CameraAdapters/Boq/SDK-readme.txt')
for file in ['runtimehost_client.py', 'tcp-loopback-peer.py']:
    shutil.copy2(root / 'tools' / file, target / 'tools' / file)
shutil.copytree(root / 'docs', target / 'docs', dirs_exist_ok=True)
if '--stage-only' in sys.argv:
    print(json.dumps(dict(staged=str(target), protectedUnchanged=len(protected)), ensure_ascii=False))
    sys.exit(0)
verification = target / 'verification/collections'
verification.mkdir(parents=True, exist_ok=True)
for folder in ['regression-final-r5', 'ui-final', 'published-cli', 'native-ui']:
    source = root / 'test-results/collections' / folder
    for file in source.iterdir():
        if file.is_file() and file.suffix in {'.json', '.trx', '.log', '.txt', '.png', '.jpg'}:
            dest = verification / folder / file.name
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(file, dest)
for file in ['python.xml', 'python.log', 'build-release-final.log', 'restore-final.log']:
    shutil.copy2(root / 'test-results/collections' / file, verification / file)
for file in (root / 'test-results/collections').iterdir():
    if file.is_file() and file.suffix in {'.log', '.trx', '.txt'}:
        destination = verification / 'development-records' / file.name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(file, destination)
readme = '''VisionStudio 集合与逐项子流程版 / 2026-11-16 / Windows x64
启动本目录的 VisionStudio.exe。自包含.NET8；不含Python环境或AI模型。
方案示例 → 集合与逐项：多区域英文OCR、多Blob逐项测量；打开时复制到用户可写目录。
逐项节点参数面板 → 编辑子流程；当前项、索引、共享原图均为显式端口。
集合显示节点配置样式；集合/逐项面板查看完整表格、高亮、局部图像和有界迭代快照。
文本输出 → 文本详情：分页查看、复制完整文本。
Console: VisionPlatform.Console.exe run Examples/collection-multi-ocr/project.json
Host: VisionPlatform.RuntimeHost.exe --project Examples/collection-blob-measure/project.json --once
AI模型管理→用于测试；ONNX实际固定输入会校验并锁定。训练仍须明确选择本地PT；不自动下载或训练。
保留旧Schema1/2读取，保存Schema3；子流程版本1在正式节点参数内。旧程序不保证支持新节点。
英文Tesseract资源随包；其他语言需已有合法资源。可选YOLO示例不含权重。
真实博峤硬件、GPU训练及全天稳定性不属于本次验证。旧发布包原样保留。
详细功能、操作、测试结果与未验证边界见docs/集合与逐项子流程验收报告.md。
'''
(target / 'README.txt').write_text(readme, encoding='utf-8')
manifest = target / 'verification/collections/file-sha256.json'
manifest.write_text(json.dumps([dict(path=p.relative_to(target).as_posix(), sha256=sha(p)) for p in sorted(target.rglob('*')) if p.is_file() and p != manifest], ensure_ascii=False, indent=2), encoding='utf-8')
archive = root / 'artifacts' / (name + '.zip')
assert not archive.exists(), 'Do not overwrite an existing release ZIP'
with zipfile.ZipFile(archive, 'x', zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
    for p in sorted(target.rglob('*')):
        if p.is_file(): bundle.write(p, 'VisionStudio/' + p.relative_to(target).as_posix())
with zipfile.ZipFile(archive) as bundle:
    assert bundle.testzip() is None
assert not any(p.suffix.lower() in {'.pt', '.onnx', '.sqlite3', '.db'} for p in target.rglob('*') if p.is_file())
print(json.dumps(dict(directory=str(target), archive=str(archive), sha256=sha(archive), protectedUnchanged=len(protected)), ensure_ascii=False, indent=2))
source_archive = root / 'artifacts/VisionPlatform-Collections-20261116-Source.zip'
assert not source_archive.exists(), 'Do not overwrite an existing source ZIP'
excluded = {'.git', '.vs', 'artifacts', 'test-results', 'TestResults', 'bin', 'obj', '__pycache__', '.pytest_cache', '.venv', 'venv', 'service-data', 'transfer', 'wpf-render-transfer'}
with zipfile.ZipFile(source_archive, 'x', zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
    for directory, folders, files in os.walk(root):
        folders[:] = [n for n in folders if n not in excluded and not n.startswith('python-service-tmp')]
        for filename in sorted(files):
            p = Path(directory) / filename
            if p.suffix.lower() in {'.pt', '.onnx', '.sqlite', '.sqlite3', '.db', '.pyc'} or filename.startswith('.env') or filename.endswith(('-wal', '-shm')): continue
            if any(part in {'bin', 'obj'} for part in p.relative_to(root).parts): continue
            bundle.write(('\\\\?\\' + str(p.resolve())) if os.name == 'nt' else p, 'VisionPlatform/' + p.relative_to(root).as_posix())
with zipfile.ZipFile(source_archive) as bundle: assert bundle.testzip() is None
release_manifest = dict(release=name, directory=str(target), sourceZip=str(source_archive), runtimeZip=str(archive),
                        runtimeSha256=sha(archive), sourceSha256=sha(source_archive), gitCommit=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
                        uncommittedWorkPreserved=True, protectedUnchanged=len(protected), modelsIncluded=False, environmentIncluded=False)
(root / 'artifacts/collections-delivery-manifest.json').write_text(json.dumps(release_manifest, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(release_manifest, ensure_ascii=False, indent=2))
