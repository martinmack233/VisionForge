"""Summarize actual phase-2 evidence and package source/desktop outputs. Standard library only."""
from pathlib import Path
import hashlib, json, shutil, xml.etree.ElementTree as ET, zipfile

root = Path(__file__).resolve().parent.parent
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
rows = []
for file in sorted((root / "test-results/phase2/final").glob("*.trx")):
    tree = ET.parse(file)
    counters = tree.find("t:ResultSummary/t:Counters", ns).attrib
    assembly = tree.find("t:TestDefinitions/t:UnitTest/t:TestMethod", ns).attrib["className"].split(".Tests")[0] + ".Tests"
    rows.append(dict(assembly=assembly, file=file.relative_to(root).as_posix(),
        total=int(counters["total"]), passed=int(counters["passed"]), failed=int(counters["failed"]), skipped=int(counters["notExecuted"])))
assert len(rows) == 5 and sum(r["total"] for r in rows) == 163
assert all(r["passed"] == r["total"] and r["failed"] == r["skipped"] == 0 for r in rows)
baseline = json.loads((root / "test-results/phase2/phase1-source-baseline.json").read_text(encoding="utf-8-sig"))
assert all(hashlib.sha256((root / r["Path"]).read_bytes()).hexdigest().upper() == r["SHA256"] for r in baseline)
nodify_hashes = json.loads((root / "third_party/Nodify/upstream-sha256.json").read_text(encoding="utf-8-sig"))
assert all(hashlib.sha256((root / "third_party" / r["Path"]).read_bytes()).hexdigest().upper() == r["SHA256"]
    for r in nodify_hashes if r["Path"] != "Nodify/Nodify.csproj")
desktop = json.loads((root / "artifacts/phase2-acceptance/summary.json").read_text())
assert desktop["Passed"] and desktop["ConsoleMatchesWpf"] and desktop["ReopenedMeasurementsMatch"] and desktop["NormalClosingPassed"] and desktop["BuffersAfter"] == 0
interactive = json.loads((root / "test-results/phase2/ui/interactive.json").read_text())
assert interactive["SchemaVersion"] == 1 and len(interactive["Nodes"]) == 2 and len(interactive["Connections"]) == 1
assert interactive["Nodes"][1]["Parameters"]["Options"]["KernelSize"] == 7
assert abs(interactive["ROI"][0]["AngleDegrees"]) > 1
published_text = (root / "test-results/phase2/ui/published-state.txt").read_text(encoding="utf-8")
assert "OK BlobCount=2" in published_text and "decision.IsOk" in published_text
summary = dict(date="2026-11-02 Asia/Shanghai", passed=163, failed=0, skipped=0, suites=rows,
    phase1FilesChecked=len(baseline), phase1FilesChanged=0, actualDesktopAcceptance=desktop,
    mouseInteraction=dict(importImage=True, dragNode=True, dragWire=True, parameterKernel=7, run=True,
        roiCreateMoveResizeRotate=True, disconnectReconnect=True, saveCloseReopen=True, publishedApplicationOk=True))
(root / "test-results/phase2/final/summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
images = root / "docs/images"; images.mkdir(exist_ok=True)
shutil.copy2(root / "artifacts/phase2-acceptance/editor-running.png", images / "VisionStudio.png")
table = "\n".join(f"| {r['assembly']} | {r['total']} | {r['passed']} | {r['failed']} |" for r in rows)
large = desktop["LargeImage"]
def mib(value): return f"{value / 1048576:.2f}"
report = f"""# VisionStudio 第二阶段验收报告

完成日期2026-11-02（Asia/Shanghai）。保留原《第二阶段开发建议》的整体路线。已交付可启动的Windows x64 WPF桌面程序，不是只展示静态节点的Demo；全部算法调用原RuntimeExecutor。

## 架构与原代码保护

开工先核对NodeDescriptor、NodeSpecification、GraphValidator、RuntimeExecutor、ProjectStore和ImageFrame实际接口，再固定模块边界。Editor.Core只依赖现有Plugins/Runtime，不引用WPF或Nodify；Editor.Wpf负责界面；Nodify负责节点交互。原第一阶段43个源文件/工程/锁文件SHA256对照全部一致，**0处修改**。SchemaVersion仍为1，Console仍可加载编辑器保存的正式工程。

通用参数以独立元数据+反射生成，支持整数、浮点、布尔、字符串、枚举、范围、默认值、可空和嵌套对象。草稿单独持有JsonNode，应用前调用原节点校验。位置只写Sidecar；算法参数、ROI、参考姿态与模板写正式ProjectDocument。

主Agent统一接口、测试与集成；四专业角色分别负责Shell/参数、图像/ROI、Nodify、执行会话/工程管理。受三个子Agent并发槽位限制分批进行，文件归属见《第二阶段接口与分工》。10月30日已完成初步界面和控件；11月2日续接完成会话实现、全链测试、实际鼠标验收与发布。

Nodify固定此前审核源码提交8e7c93b4a11d3b06e257938ae0f06bd09e427c78、7.3.0、MIT；没有把不同提交的同版本NuGet冒充审核版本。控件源码未改，构建/签名差异和原始哈希已记录。原native版本与许可审核限制保持，详见第三方清单。

## 实际构建与自动化测试

`dotnet restore`、`dotnet build -c Release`、`dotnet test -c Release --logger trx --results-directory test-results/phase2/final`全部退出0。最终构建0警告、0错误，**163通过、0失败、0跳过**。原114项仍全部通过，新增编辑器相关49项。

| 程序集 | 总数 | 通过 | 失败 |
|---|---:|---:|---:|
{table}
| 合计 | 163 | 163 | 0 |

原始日志/TRX和自动汇总在test-results/phase2/final。测试覆盖参数草稿/范围/默认/可空、UI无关依赖边界、非法连接/拖线命令、图选择与退订、五格式预览、预算/缓存/快速切图回收、ROI坐标、后台执行/版本失效/排队取消、单节点fresh输入、正式保存/另存/Sidecar、模板教学和失配清旧姿态。

## 真实桌面验收

tools/EditorAcceptance使用生产MainWindow和EditorSession创建真实窗口，运行完整流程B，编辑旋转ROI并保存，另起Console进程运行同一文件，然后关闭窗口、新建会话重新加载并复测。

| 验收动作 | 结果 |
|---|---|
| 创建/打开工程、导入图像 | 桌面和自动化均完成 |
| 工具箱拖入高斯节点、画布拖线 | 实际鼠标完成；工程包含2节点/1连接 |
| 参数修改 | 自动面板把KernelSize从5改7并应用，正式JSON验证为7 |
| 运行/结果/耗时 | 实际UI新流程两节点Success；完整检测流程7节点Success、BlobCount2、OK |
| ROI创建/移动/尺寸/旋转 | 实际鼠标完成，正式JSON角度{interactive['ROI'][0]['AngleDegrees']:.6f}° |
| 断开/重新连线 | 实际UI连线同步消失/恢复，旧结果失效 |
| 保存/退出/重新打开/再运行 | 实际UI完成，节点、连线、Kernel7及ROI均恢复，运行成功 |
| 同一工程Console/WPF | 独立Console进程与真实桌面会话Score、CenterX及Blob数一致（容差1e-6）；重开后相同 |
| 失配/变更/取消/项目切换 | 回归通过；晚结果不发布、旧有效跟随ROI清除 |
| 正常关窗和资源 | API关闭及真实Closing事件均通过，平台图像缓冲由0回到0 |
| 发布目录启动 | VisionStudio.exe独立目录实际启动、生成示例并运行得到OK，未依赖开发目录中的程序集 |

完整检测实测：Score={desktop['Edited']['Match']['Score']:.9f}，中心({desktop['Edited']['Match']['CenterX']:.6f},{desktop['Edited']['Match']['CenterY']:.6f})，角度{desktop['Edited']['Match']['AngleDegrees']:.6f}°，尺度{desktop['Edited']['Match']['Scale']}，BlobCount2，OK。均来自真实算法。

真实鼠标验收工程与Sidecar在test-results/phase2/ui，发布版可访问树已记录published-state.txt。窗口截图是生产MainWindow真实渲染，不是设计稿。

![运行中的编辑器](images/VisionStudio.png)

## 8192×10000大图与内存/交互

82MP Mono8原始载荷81,920,000字节（78.125MiB）。实际创建BMP、加载、后台缩略预览，并执行1:1、缩放、平移和坐标逆变换检查。测试文件刚生成，因此是暖文件缓存，非冷盘或工业采集吞吐基准。

- 实际预览{large['PreviewDimensions'][0]}×{large['PreviewDimensions'][1]}，缓存像素载荷{large['PreviewBytes']:,}字节（{mib(large['PreviewBytes'])}MiB），低于64MiB限额。
- 加载/绑定/预览完成本轮{large['ElapsedMilliseconds']:.2f}ms；Dispatcher心跳{large['DispatcherTicks']}次，最大采样间隔{large['MaximumHeartbeatGapMilliseconds']:.2f}ms。这是一次共享开发环境测量，不是保证帧率或延迟上界。
- 进程Private Bytes：加载前{mib(large['ProcessBefore']['PrivateMemorySize64'])}MiB，持有大图预览时{mib(large['ProcessWithPreview']['PrivateMemorySize64'])}MiB，关闭后{mib(large['ProcessAfterClose']['PrivateMemorySize64'])}MiB。累计Peak Working Set约{mib(large['ProcessWithPreview']['PeakWorkingSet64'])}MiB，包含前面窗口、JIT、图像生成和native allocator，不是一次预览的纯分配。
- 关闭后FrameHandle.ActiveBytes/ActiveAllocations回到基线；快速切换和Bayer条带预览也已回归。CLR/native保留内存没有强制回到进程初值，不能从短跑宣称全native零泄漏或全天生产长稳完成。

预览缓存按冻结像素载荷64MiB预算，单预览最长边2048，独立结果图像预算512MiB。预算不是总进程、GPU或执行峰值上限。大图1:1目前按原图坐标比例显示缩略采样，保留IPreviewProvider/IImageTileProvider接口；完整金字塔/瓦片尚未实现。

## 发现与修复

- 首次整体构建缺EditorSession：续接补齐实际实现，而非用UI伪结果代替。
- 验收工具出现PixelFormat名称冲突：显式使用Contracts别名修复，生产算法不变。
- 实际截图发现参数文字在暗背景下不可读，以及默认节点位置重叠：修复字体继承/对比度、280宽节点与330×190默认间距，旧用户Sidecar不自动覆盖。
- 参数恢复默认对nullable非空默认值不正确：增加DefaultIsNull元数据及插件回归。
- ROI绘制使旧输出失效后无法绑定当前参考姿态：改为只供显式教学绑定的候选快照，输入/其他参数/连线/失配清除，不作为运行定位结果复用。
- 修改图像路径仍显示旧图：现在清旧预览，运行后采用本次Runtime输入。
- 单节点借用输出、排队运行遇项目切换、快速预览关闭及Closing重入均增加了生命周期保护与测试。
- 桌面仍在退出时一次编译遇短暂DLL锁重试；进程结束后重新最终构建0警告。早期记录保留，没有删失败测试或修改算法制造通过。

## 首轮明确边界

已实现矩形和旋转矩形，一个活动交互ROI；圆/椭圆/多边形/线段仅留类型接口。模板教学限轴对齐矩形，旋转检测ROI可用。正式保存仍要求图完整、参数有效；另存重定位引用但不自动复制外部资源。缩略图预览不是全分辨率Tile显示。取消不能抢占一次native调用；未做真实相机、线扫实时节拍或8/24小时长稳验收。第三方native内部发行授权仍按原清单后续核验。

完整源码、Windows桌面发布目录/ZIP、使用说明、许可证、截图和测试证据已一并交付。下一步应先用实际工业样本验证交互与精度，再扩展多ROI管理、Tile/金字塔和更多独立算法节点。
"""
(root / "docs/第二阶段验收报告.md").write_text(report, encoding="utf-8")
runtime = root / "artifacts/VisionStudio-Phase2-win-x64"
shutil.copytree(root / "third_party/licenses", runtime / "licenses", dirs_exist_ok=True)
for vendor in ["CvInspect", "Nodify"]:
    for name in ["LICENSE", "PROVENANCE.md"]:
        shutil.copy2(root / "third_party" / vendor / name, runtime / "licenses" / (vendor + "-" + name))
shutil.copy2(root / "third_party/CvInspect/THIRD-PARTY-NOTICES.md", runtime / "licenses/CvInspect-THIRD-PARTY-NOTICES.md")
shutil.copytree(root / "samples", runtime / "Samples", dirs_exist_ok=True)
shutil.copytree(images, runtime / "images", dirs_exist_ok=True)
for file in ["VisionStudio使用说明.md", "第二阶段验收报告.md", "第三方许可证清单.md", "native-build-information.txt"]:
    shutil.copy2(root / "docs" / file, runtime / file)
(runtime / "README.txt").write_text("VisionStudio 第二阶段\n\n启动 VisionStudio.exe。需要 .NET 8 Windows Desktop Runtime (x64)。\n点击“示例工程”再“运行流程”查看真实检测。也可打开 Samples/DemoInspection/flow-b.json。\n详见 VisionStudio使用说明.md 和 第二阶段验收报告.md。\n", encoding="utf-8")
assert not list(runtime.rglob("*ffmpeg*"))
archives = []
for name, base, excluded in [
    ("VisionPlatform-Phase2-Source.zip", root, {"bin", "obj", "artifacts", ".git", ".vs", "__pycache__"}),
    ("VisionStudio-Phase2-win-x64.zip", runtime, set())
]:
    file = root / "artifacts" / name
    with zipfile.ZipFile(file, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(base.rglob("*")):
            relative = path.relative_to(base)
            if path.is_file() and not any(part in excluded for part in relative.parts):
                archive.write(path, "VisionPlatform/" + relative.as_posix())
    with zipfile.ZipFile(file) as archive: assert archive.testzip() is None
    archives.append(dict(file=file.name, bytes=file.stat().st_size, sha256=hashlib.sha256(file.read_bytes()).hexdigest()))
(root / "artifacts/phase2-delivery-manifest.json").write_text(json.dumps(archives, indent=2), encoding="utf-8")
print(json.dumps(dict(testsPassed=163, phase1SourceChanges=0, desktopPassed=True, archives=archives), ensure_ascii=False, indent=2))
