"""Package measured Phase5 evidence without changing Phase4C or user AI data."""
from pathlib import Path
import hashlib
import json
import os
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile

root = Path(__file__).resolve().parent.parent
runtime = root / 'artifacts/VisionStudio-Phase5-win-x64'
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def read(path): return json.loads((root / path).read_text(encoding='utf-8-sig'))
def dump(path, value):
    target = root / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
def write(path, value): (root / path).write_text(value, encoding='utf-8')
def sha(path):
    with Path(path).open('rb') as stream: return hashlib.file_digest(stream, 'sha256').hexdigest()
def test_names(folder):
    return {e.attrib['testName'] for path in (root / folder).glob('*.trx') for e in ET.parse(path).findall('t:Results/t:UnitTestResult', ns)}

suites = []
for path in sorted((root / 'test-results/phase5/final-validation-r2').glob('*.trx')):
    tree = ET.parse(path)
    counters = tree.find('t:ResultSummary/t:Counters', ns).attrib
    method = tree.find('t:TestDefinitions/t:UnitTest/t:TestMethod', ns).attrib
    suites.append(dict(assembly=method['className'].split('.Tests')[0] + '.Tests', total=int(counters['total']), passed=int(counters['passed']), failed=int(counters['failed']), skipped=int(counters['notExecuted'])))
assert len(suites) == 9 and all(s['total'] == s['passed'] and s['failed'] == s['skipped'] == 0 for s in suites)
old = test_names('test-results/phase5/baseline')
current = test_names('test-results/phase5/final-validation-r2')
assert len(old) == 632 and old <= current
total = sum(s['passed'] for s in suites)
classic = read('test-results/phase5/final-acceptance/attempt3/summary.json')
wpf = read('test-results/phase5-ai/wpf-final-fixed/summary.json')
deployment = read('test-results/phase5-ai/deployment-real/attempt2/summary.json')
shell = read('test-results/phase5-ai/shell-final/summary.json')
package_smoke = read('test-results/phase5/package-check/summary.json') if (root / 'test-results/phase5/package-check/summary.json').exists() else None
assert classic['Passed'] and classic['BuffersBefore'] == classic['BuffersAfter'] == 0 and wpf['Passed'] and deployment['Passed']
assert shell['Passed'] and shell['RuntimeConsoleHostPassed'] and shell['BuffersAfter'] == shell['BuffersBefore'] == 0
for file in ['service-real-onnx.json', 'training-real-resume.json', 'export-real.json', 'qt-independent.json']:
    assert read('test-results/phase5-ai/' + file)['verified']
assert read('test-results/phase5-ai/training-real-resume.json')['resume_verified']
assert len(wpf['Captures']) == 6 and len(classic['Compatibility']) == 7

protected = []
for item in read('test-results/phase5-ai/baseline/original-data-hashes.json'):
    observed = sha(item['Path'])
    assert observed.lower() == item['Hash'].lower(), item['Path']
    protected.append(dict(path=item['Path'], sha256=observed, unchanged=True))
old_packages = read('artifacts/phase4c-delivery-manifest.json')['archives']
for item in old_packages:
    assert sha(root / 'artifacts' / item['file']) == item['sha256'], 'Phase4C archive changed'
asset_count = 0
for relative in subprocess.check_output(['git', 'ls-tree', '-r', '--name-only', 'HEAD'], cwd=root, text=True, encoding='utf-8').splitlines():
    if relative.startswith(('Examples/', 'samples/')):
        assert (root / relative).read_bytes() == subprocess.check_output(['git', 'show', 'HEAD:' + relative], cwd=root), relative
        asset_count += 1
assert asset_count >= 176
native_hash = sha(runtime / 'OpenCvSharpExtern.dll')
assert native_hash == '1fa122bdb8e94175e7719fb8aa8f2ab211268a756f5d0c7a13c710ed79ae30cd'
assert not list(runtime.rglob('opencv_videoio_ffmpeg*.dll'))
assert all((runtime / name).is_file() for name in ['VisionStudio.exe', 'VisionPlatform.Console.exe', 'VisionPlatform.RuntimeHost.exe', 'coreclr.dll', 'VisionPlatform.AI.dll', 'VisionPlatform.Acquisition.Boq.dll', 'CameraAdapters/Boq/VisionPlatform.BoqWorker.exe', 'CameraAdapters/Boq/BoqApi_CSharp.dll', 'tessdata/eng.traineddata', 'tools/ai_workspace_service/service.py', 'tools/yolo_bridge/yolo_bridge.py'])
assert (root / 'test-results/phase5/published-ui-state/actual-published-accessibility.txt').is_file()

packages = set()
for parent in ['src', 'tests', 'tools', 'third_party']:
    for path in (root / parent).glob('*/packages.lock.json'):
        for target in json.loads(path.read_text(encoding='utf-8-sig'))['dependencies'].values():
            for name, entry in target.items():
                if 'resolved' in entry: packages.add((name, entry['resolved']))
dump('docs/sbom-phase5-nuget.json', [dict(name=n, version=v) for n, v in sorted(packages)])
summary = dict(total=total, passed=total, failed=0, skipped=0, original632Preserved=True, suites=suites,
               pythonOriginalTests=141, pythonServiceTests=8, originalDataHashChecks=protected,
               originalAssetFilesUnchanged=asset_count, phase4CArchivesPreserved=old_packages,
               classic=classic, aiWpf=wpf, deployment=deployment, aiShell=shell, nativeSha256=native_hash,
               packageSmokePassed=bool(package_smoke and package_smoke['Passed']),
               physicalBoqCaptureVerified=False, systemDpiSwitchVerified=False, cleanWindowsVmVerified=False, allDayStabilityVerified=False)
dump('test-results/phase5/final-validation-r2/summary.json', summary)
table = '\n'.join(f"| {s['assembly']} | {s['total']} | {s['passed']} | 0 |" for s in suites)
write('docs/第五阶段构建测试与交互验收记录.md', f'''# 第五阶段构建、测试与交互记录

2026-11-13，基线提交 fc487350ff2cbdacdf2ff1f0a28cb0a5bd80680a。实际执行 dotnet restore、dotnet build -c Release、dotnet test -c Release，构建0警告0错误，最终 {total}/{total} 通过，632项原测试逐个testName保留，没有删除或跳过。

| 程序集 | 总数 | 通过 | 失败 |
|---|---:|---:|---:|
{table}

原 YOLOStudio 在原项目工作目录运行 pytest，141/141通过；AI服务8/8通过。WPF中的真实服务测试覆盖导入/标注自动保存/撤销/重开、正常与未检查区别、70次真实原像素Tile及释放。日志、TRX、XML、每次失败与修复原件保存在 test-results/phase5 与 phase5-ai。测试计数是回归依据，实际操作证据另列如下。

## 当前构建与独立发布的实测

- Phase5Acceptance：实际shown WPF窗口，打开并运行第二阶段、第三阶段A/B/C、第四阶段C共7个旧工程，原文件哈希不变；多节点内部数据/控制线复制、禁用断线、启用不复线、撤销恢复、保存重开Schema3均通过。
- 使用新发布目录的 Console 与 RuntimeHost EXE，禁用节点均为Skipped/Disabled且无输出；独立有效流程成功；启用原下游后缺少Image输入均明确失败，原日志保留。
- AiShellAcceptance：真实MainWindow回调将本次训练导出的ONNX应用为正式YOLO节点，固定模型哈希和独立AI项目ID；应用时无执行结果。连接图像端口后保存重开，WPF原Runtime、发布Console/Host实际运行成功，关闭缓冲前后0。中间Schema2迁移及未知字段拒绝也有独立回归。
- 文件夹虚拟相机真实采集：冻结帧检测与其frameId一致，采集新帧的ID/序号改变；连续停止保留冻结帧，清空/关闭释放；FrameHandle前后0。统计来自实际采集，不是预填帧率。
- AiWorkspaceAcceptance：当前六页shown-window渲染，连接真实Python业务；显示本轮实际训练两条历史、2个Epoch指标、原引擎2目标推理及导出ONNX模型；未再启动训练。150%应用缩放首次将标注视口挤到零附近，修复页面滚动和最小有效区域后重新通过，旧截图保留。
- 原生桌面物理操作记录在 test-results/phase5/manual/screenshots：多选复制/撤销/重做、右键禁用、实际虚拟相机冻结/检测和连续停止。当前自包含VisionStudio.exe另由Windows启动并通过UIA观察到完整首页/AI导航，未自动运行检测或Python。当前AI截图采用shown WPF的RenderTargetBitmap；没有把其他窗口遮挡的桌面截图保存或冒充验收图。
- 1366×768、1093×614、911×512布局及150%应用布局缩放已检查；不是系统显示器DPI切换测试。ComboBox展开/可编辑/键盘/文本复制粘贴、菜单、Tab、滚动条和禁用状态有实际WPF控件回归。
- 打包后解压到源码目录外的独立Temp目录，在这个副本移走可选SDK，发布Console基本流程、Host基本流程和Host虚拟相机流程均exit0；没有安装Python或下载模型。源码ZIP检查不包含.venv、模型权重、SQLite或构建缓存。首轮源码打包遇到pytest临时数据的超长路径，已排除生成缓存并支持Windows扩展路径，使用校验后原子替换ZIP，失败日志保留。

## 真实引擎、失败与边界

同一已有ONNX与图像，原DetectorBackend和集成节点：整图2目标，12片17目标，ROI2目标，坐标/置信度误差均0；服务另一分块配置10目标也与原引擎相同。部署验证、保存重开、YoloDetectionRoi到Blob、Console和Host实际通过，原资源哈希不变。

独立4张64×64数据，在RTX4080运行原训练器，总目标2Epoch；真实第1轮后停止，从有效检查点恢复并完成第2轮；导出static64 FP32 ONNX，checker和CPU forward通过。实际mAP=0，证明任务链而非缺陷模型质量。原Qt入口在独立数据根打开关闭，项目/类别/图片/标注/训练/模型ID及记录保留。原用户SQLite和requirements哈希复核不变。

保留的失败包括：旧Schema固定断言、只读TextBox绑定、缩略图文件锁、Windows长路径、GPU尚未释放时过早恢复、Host查询未带runId、RID并发还原，以及最终Classic验收工具首次PixelFormat命名冲突和独立Compare缺输入。按原因修复，未降低业务校验。Classic最终attempt3通过，AI最终wpf-final-fixed通过。

博峤实际SDK .NET8加载与枚举通过；模拟设备单帧未返回像素，按超时失败。真实相机/物理触发/串口、干净Windows VM、全天稳定性、生产数据全量和显示器DPI切换未验收。详细原日志见博峤说明及已知边界。
''')
write('docs/第五阶段第三方依赖说明.md', f'''# 第五阶段第三方依赖与来源

实际锁文件解析得到 {len(packages)} 个NuGet名称/版本组合（含测试和跨平台传递包），清单见sbom-phase5-nuget.json。保留原CvInspect/Nodify来源与版权，未改其算法/编辑控件源码；OpenCvSharp Slim native哈希保持 {native_hash}，无独立FFmpeg插件。自包含运行时为实际publish得到的.NET/WindowsDesktop 8.0.18，原包LICENSE/NOTICE随licenses保留。

AI业务使用用户现有YOLOStudio及其Python环境，未安装、升级或打包其环境/权重/数据。原requirements.lock.txt哈希不变；原Ultralytics/模型等商业授权需要单独核验，外部进程并不自动消除发行义务。原OpenCV静态组成待核验范围继承此前说明。

博峤来自用户提供BoqApi_CSharp_20160916.zip，ZIP/DLL原件、README与哈希记录保留。发布包中的SDK用于本机用户自带依赖集成；没有找到明确商业再分发授权，不能宣称对外闭源销售已清关。具体.NET8验证、VC依赖及硬件未验证范围见博峤接入说明。
''')
write('docs/第五阶段验收报告.md', f'''# 第五阶段交付与验收

本轮交付在原工程上实现编辑交互修复、禁用规则、统一浅蓝控件、设备操作闭环、可选博峤隔离适配，以及复用现有Python业务的六页AI工作区。原Runtime、ROI与算法保持，正式工程可从Schema1/2读取并保存为Schema3；旧版本程序不保证读取新格式，原工程与Phase4C发布包保留。

实际完成restore/build/test：{total}/{total} .NET测试、141/141原YOLO测试、8/8服务测试通过，原632项保留。真实训练停止/恢复、ONNX导出、同图同模型对照、模型应用和WPF/Console/Host链路通过。细节见《第五阶段构建测试与交互验收记录》《第五阶段AI服务实际验证记录》及原日志。

入口：artifacts/VisionStudio-Phase5-win-x64/VisionStudio.exe。同目录包含独立Console、RuntimeHost；CameraAdapters/Boq包含独立worker及用户SDK。首次AI连接需配置已有YOLOStudio与Python环境，不能把包描述为内置完整Python训练环境。先阅读《第五阶段AI工作区使用说明》《第五阶段AI模型部署说明》《第五阶段博峤相机接入说明》。

已经完成的软件功能、部分完成和未验证项目逐项列在《第五阶段变更与已知边界》。真实博峤硬件采集尚未通过：SDK模拟单帧超时保留；没有用虚拟帧伪造硬件结果。小样本训练/有限循环不代表产线精度或全天稳定性认证。当前截图为实际构建窗口/控件渲染，原生桌面物理交互证据另行标明。

原用户SQLite、requirements、{asset_count}个已跟踪示例/样本文件及原Phase4C归档哈希复核不变。检测工程与AI数据通过独立ID关联，删除检测工程不调用AI数据删除。完整源码、配套YOLO入口补丁、发布包、SHA256清单、截图和失败/修复记录一并交付。本轮到此停止，不自动扩展后续阶段。
''')

# Only copy into the new Phase5 directory. Old release, source examples and user data are read-only.
for source, target in [('Examples', 'Examples'), ('samples', 'Samples'), ('third_party/licenses', 'licenses'), ('licenses/phase4b', 'licenses/phase4b')]:
    shutil.copytree(root / source, runtime / target, dirs_exist_ok=True)
shutil.copytree(root / 'artifacts/VisionStudio-Phase4C-win-x64/licenses', runtime / 'licenses', dirs_exist_ok=True)
shutil.copy2(root / 'resources/tessdata/provenance.json', runtime / 'tessdata/provenance.json')
(runtime / 'docs').mkdir(parents=True, exist_ok=True)
for file in (root / 'docs').iterdir():
    if file.is_file(): shutil.copy2(file, runtime / 'docs' / file.name)
images = root / 'docs/images/Phase5'
images.mkdir(parents=True, exist_ok=True)
for folder, prefix in [('test-results/phase5-ai/wpf-final-fixed', 'AI-'), ('test-results/phase5/final-acceptance/attempt3', 'Current-'), ('test-results/phase5/manual/screenshots', 'Desktop-')]:
    for file in (root / folder).glob('*.png'): shutil.copy2(file, images / (prefix + file.name))
shutil.copytree(images, runtime / 'docs/images/Phase5', dirs_exist_ok=True)
for name in ['runtimehost_client.py', 'tcp-loopback-peer.py']:
    shutil.copy2(root / 'tools' / name, runtime / 'tools' / name)
file_manifest = runtime / 'verification/file-sha256.json'
file_manifest.parent.mkdir(parents=True, exist_ok=True)
for path in ['test-results/phase5/final-validation-r2', 'test-results/phase5/final-acceptance/attempt3', 'test-results/phase5-ai/wpf-final-fixed', 'test-results/phase5-ai/shell-final']:
    destination = runtime / 'verification' / Path(path).name
    destination.mkdir(parents=True, exist_ok=True)
    for file in (root / path).iterdir():
        if file.is_file() and file.suffix in {'.json', '.log', '.trx'}: shutil.copy2(file, destination / file.name)
write('artifacts/VisionStudio-Phase5-win-x64/README.txt', f'''VisionStudio 第五阶段 / Windows x64
启动 VisionStudio.exe；自包含.NET8，无需.NET SDK。保留原Phase4C目录和ZIP。
新增AI工作区：首次连接配置已有YOLOStudio代码、已有.venv及独立数据根；不包含环境/权重，不运行pip，不自动下载或训练。
Runtime YOLO仍为ONNX CPU；工作区原PT/CUDA训练与测试需显式选择。应用模型不会自动运行。
禁用算子会断开全部线；启用不复线；撤销可恢复。正式保存Schema3，读取旧Schema1/2；旧程序不保证读取新工程。
Console: VisionPlatform.Console.exe run Examples/basic/project.json
Host: VisionPlatform.RuntimeHost.exe --project Examples/runtime-host/project.json --once
博峤SDK为用户自带依赖，worker位于CameraAdapters/Boq；真实硬件尚未验收，SDK模拟单帧曾超时。
实际{total}项.NET、141项原YOLO、8项服务测试通过；真实小样本训练/停止恢复/导出及同模型对照通过，非工业精度认证。
使用步骤、兼容性、全部截图、测试证据和未验证范围见docs中第五阶段文档。VC x64运行库/厂商驱动需按实际环境核对。
''')
file_manifest.write_text(json.dumps([dict(path=path.relative_to(runtime).as_posix(), sha256=sha(path)) for path in sorted(runtime.rglob('*')) if path.is_file() and path != file_manifest], ensure_ascii=False, indent=2), encoding='utf-8')

if '--stage-only' in sys.argv:
    print(json.dumps(dict(passed=total, originalTestsPreserved=len(old), staged=str(runtime), protectedAssets=asset_count), ensure_ascii=False)); sys.exit(0)

archives = []
excluded = {'.git', '.vs', 'artifacts', 'bin', 'obj', '__pycache__', '.pytest_cache', '.venv', 'service-data', 'transfer', 'wpf-render-transfer', 'data'}
for name, base, prefix, source_archive in [('VisionStudio-Phase5-win-x64.zip', runtime, 'VisionStudio', False), ('VisionPlatform-Phase5-Source.zip', root, 'VisionPlatform', True)]:
    archive = root / 'artifacts' / name
    temporary_archive = archive.with_name(archive.name + '.building')
    with zipfile.ZipFile(temporary_archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as target:
        for directory, folders, files in os.walk(base):
            if source_archive: folders[:] = [x for x in folders if x not in excluded and not x.startswith('python-service-tmp')]
            for filename in sorted(files):
                path = Path(directory) / filename
                if source_archive and (path.suffix.lower() in {'.pt', '.onnx', '.sqlite3', '.sqlite', '.db', '.pyc'} or filename.endswith(('-wal', '-shm'))): continue
                filesystem_path = ('\\\\?\\' + str(path.resolve())) if os.name == 'nt' else path
                target.write(filesystem_path, prefix + '/' + path.relative_to(base).as_posix())
        if source_archive:
            for relative in ['main.py', 'core/workspace_lock.py']:
                target.write(root.parent / 'YOLOStudio' / relative, 'YOLOStudio-integration/' + relative)
    with zipfile.ZipFile(temporary_archive) as target: assert target.testzip() is None
    os.replace(temporary_archive, archive)
    archives.append(dict(file=name, bytes=archive.stat().st_size, sha256=sha(archive)))
dump('artifacts/phase5-delivery-manifest.json', dict(baseCommit='fc487350ff2cbdacdf2ff1f0a28cb0a5bd80680a', currentSourceUncommitted=True, testsPassed=total, original632Preserved=True, sourceIncludesYoloEntryPatch=True, runtimeYoloBackend='onnx-cpu', realBoqHardwareVerified=False, originalAssetsUnchanged=asset_count, archives=archives))
print(json.dumps(dict(passed=total, archives=archives), ensure_ascii=False, indent=2))
