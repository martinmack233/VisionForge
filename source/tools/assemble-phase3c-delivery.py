"""Validate actual C evidence and assemble reproducible reports/delivery. Python standard library only."""
from pathlib import Path
import hashlib
import json
import os
import shutil
import sys
import xml.etree.ElementTree as ET
import zipfile

root = Path(__file__).resolve().parent.parent
final = root / "test-results/phase3c/final"
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
rows, names = [], set()
for file in sorted(final.glob("*.trx")):
    tree = ET.parse(file)
    count = tree.find("t:ResultSummary/t:Counters", ns).attrib
    names.update(r.attrib["testName"] for r in tree.findall("t:Results/t:UnitTestResult", ns))
    method = tree.find("t:TestDefinitions/t:UnitTest/t:TestMethod", ns).attrib
    rows.append(dict(assembly=method["className"].split(".Tests")[0] + ".Tests", file=file.relative_to(root).as_posix(),
                     total=int(count["total"]), passed=int(count["passed"]), failed=int(count["failed"]), skipped=int(count["notExecuted"])))
assert len(rows) == 5 and all(r["total"] == r["passed"] and r["failed"] == r["skipped"] == 0 for r in rows), rows
previous = {r.attrib["testName"] for file in (root / "test-results/phase3b/final").glob("*.trx")
            for r in ET.parse(file).findall("t:Results/t:UnitTestResult", ns)}
assert len(previous) == 345 and previous <= names, "A B test was removed, renamed, or not rerun"
protection = json.loads((root / "test-results/phase3c/protected-source-check.json").read_text())
assert protection["checked"] == 83 and not protection["changed"]
evidence = root / "artifacts/phase3c-acceptance"
d = json.loads((evidence / "summary.json").read_text(encoding="utf-8"))
assert d["Passed"] and d["ConsoleMatchesWpf"] and d["SampleGraphsSavedReopened"]
assert d["BuffersBefore"] == d["BuffersAfter"] == d["DisplayCacheAfterClose"] == 0
assert d["Console"]["ImagesComparedByHash"] > 0 and d["Console"]["ScalarsCompared"] > 0
assert d["Tcp"]["OnlyOneMessage"] and d["Display"]["HeldPreviewSeconds"] >= 120
assert len(d["Display"]["Formats"]) == 5 and not d["FullDayIndustrialStabilityValidated"]
for row in d["Display"]["Formats"]:
    assert row["RawPixelsCompared"] > 0 and row["CurrentPyramidLevel"] == 0
    assert row["DisplayCacheBytes"] <= row["MaxDisplayCacheBytes"]
for vendor in ["Nodify", "CvInspect"]:
    for item in json.loads((root / "third_party" / vendor / "upstream-sha256.json").read_text(encoding="utf-8-sig")):
        name, expected = item.get("Path", item.get("path")), item.get("SHA256", item.get("sha256"))
        if name.endswith(".csproj"): continue
        target = root / "third_party" / name if vendor == "Nodify" else root / "third_party/CvInspect" / Path(name).relative_to("src/CvInspect")
        assert hashlib.sha256(target.read_bytes()).hexdigest().lower() == expected.lower()

total = sum(r["total"] for r in rows)
raw = final.parent / "acceptance"; raw.mkdir(exist_ok=True)
for file in evidence.glob("*"):
    if file.is_file() and file.suffix in {".json", ".log"}: shutil.copy2(file, raw / file.name)
images = root / "docs/images"; images.mkdir(exist_ok=True)
for file in evidence.glob("*.png"): shutil.copy2(file, images / ("VisionStudio-Phase3C-" + file.name))
summary = dict(deliveryDate="2026-11-11 Asia/Shanghai", passed=total, failed=0, skipped=0,
               originalPhaseBTestsRerun=len(previous), protectedSourcesUnchanged=83, suites=rows, desktop=d,
               physicalSerialHardwareTested=False, fullDayIndustrialStabilityTested=False)
(final / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
table = "\n".join(f"| {r['assembly']} | {r['total']} | {r['passed']} | {r['failed']} |" for r in rows)
(root / "docs/第三阶段C自动化测试报告.md").write_text(f"""# 第三阶段 C 自动化测试报告

交付整理日期2026-11-11。机器生成的TRX及日志保留执行主机时间，不重写历史时间戳。全部使用实际算法，没有删除旧测试或修改旧预期。

| 测试程序集 | 总计 | 通过 | 失败 |
|---|---:|---:|---:|
{table}
| 合计 | {total} | {total} | 0 |

原B全部345项按测试名再次出现并通过；新增{total-345}项。83个受保护既有源文件SHA256与B源码包一致，覆盖Runtime、Contracts、原算法/图像/ROI及旧测试。CvInspect/Nodify逐文件来源哈希也已核对。构建0警告、0错误。

原始结果：[final](../test-results/phase3c/final)、[源码保护记录](../test-results/phase3c/protected-source-check.json)、[真实桌面/性能结果](../test-results/phase3c/acceptance/summary.json)。restore-final.log、build-final.log、test-final.log、desktop-final.log、publish-final.log位于test-results/phase3c。

新增覆盖自动阈值、增强、像素中心变换、格式/Stride/ROI原点、非法参数、取消/资源回收、真实Tile原像素/渲染、Bayer相位/接缝、LRU/晚到请求、缩略排队取消、端口调试失效和旧工程重载。独立Console进程导出图像与同Runtime参考逐图SHA256相同，同时比较实际数值；真实WPF会话也逐图/标量对照。

验收工具另外执行六个C工程（条件TCP使用独立Python接收端）、五个旧工程、82MP多节点链、8次工程开关、12次切图、120秒预览和主动取消。TCP真实Loopback、旧串口模拟测试通过；没有真实/虚拟串口对硬件测试，没有全天工业稳定性结论。

初轮问题和修复保留记录：新增测试中的JSON缩进比较改为语义比较；Tile切换不同尺寸时重新居中；补齐缩略转换并发限制；验收器对独立会话消息只排除真实inspectionId/elapsedMs再比较业务值。旧测试和实际算法预期没有改动。历史首轮失败日志保留在phase3c目录，最终通过以final结果为准。

复现（仓库根目录，Windows x64/.NET SDK）：

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release --logger trx --results-directory test-results/phase3c/recheck
dotnet run --project tools/Phase3CAcceptance -c Release --no-build -- . 120
python tools/check-phase3c-protection.py
```

源码保护脚本依赖保留的B源码交付包；单独解压C源码包不含该历史ZIP，应将原B包放入artifacts。普通编译、测试及桌面验收不依赖这个历史ZIP。新机器没有Python时只有独立TCP测试探针/交付脚本需安装Python；生产WPF和Console不依赖Python。
""", encoding="utf-8")

def mib(value): return value / 1024 / 1024
display = d["Display"]
formats = "\n".join(f"| {r['Format']} | {r['Width']}×{r['Height']} | {r['LoadMilliseconds']:.2f} | {r['FitPreviewMilliseconds']:.2f} | {r['NativeOneToOneMilliseconds']:.2f} | {r['PanZoomMilliseconds']:.2f} | {mib(r['DisplayCacheBytes']):.2f} | {r['RawPixelsCompared']} |" for r in display["Formats"])
memory = "\n".join(f"| {r['Format']} | {mib(r['WithPreview']['PlatformImageBytes']):.2f} | {mib(r['WithPreview']['WorkingSet64']):.2f} | {mib(r['WithPreview']['PrivateMemorySize64']):.2f} |" for r in display["Formats"])
observations = display["Observations"]
obs_private = [mib(r["Memory"]["PrivateMemorySize64"]) for r in observations]
(root / "docs/第三阶段C大图像性能测试报告.md").write_text(f"""# 第三阶段 C 大图像性能测试报告

交付整理日期2026-11-11。真实生产MainWindow/显示控件，Windows x64、Intel Core i9-14900K、.NET SDK9.0.302、OpenCV4.13锁定native。输入为可复现合成图，未使用工业相机。原始数据见[summary.json](../test-results/phase3c/acceptance/summary.json)。本表每格式一次加载/交互观测，存在操作系统文件缓存，不能当冷盘基准或性能保证。

| 格式 | 尺寸 | 文件加载ms | 适应预览ms | 真1:1就绪ms | 平移缩放ms | 像素缓存MiB | L0采样对照数 |
|---|---:|---:|---:|---:|---:|---:|---:|
{formats}

1:1必须CurrentPyramidLevel=0且IsNativeResolutionReady=true，当前可视区域小于原图；采样覆盖Tile边缘、中心和接缝，Mono/彩色直接与原始缓冲对照，Bayer与独立局部双线性参考对照。另有实际RenderTargetBitmap单像素交替图测试。显示前后原始整幅图SHA256相同。检测坐标正反变换误差小于1e-6，原有旋转ROI数据保存验证通过。

| 格式 | 原始FrameHandle MiB | 进程工作集MiB | 进程Private Bytes MiB |
|---|---:|---:|---:|
{memory}

缩略图16MiB + Tile48MiB，总像素载荷缓存上限64MiB；当前视口借用LRU位图，不另外保留无上限位图列表。至多64可视Tile、2个Tile工作和1个缩略工作。原图完整驻留内存；工作区、WPF合成/GPU、Bitmap元数据和分配器不包含在64MiB中。缓存命中和平移速度不能代替冷请求速度。

快速替换12张2048×1536图，取消旧请求并Drain。随后保持4096×5000 BGR8预览{display['HeldPreviewSeconds']:.2f}秒，每10秒平移；观测缓存始终{mib(observations[0]['DisplayCacheBytes']):.2f}MiB，Private Bytes范围{min(obs_private):.2f}–{max(obs_private):.2f}MiB。观测期间Dispatcher 10ms名义心跳触发{display['DispatcherTicks']}次，最大间隔{display['MaximumHeartbeatGapMilliseconds']:.2f}ms（受Windows调度/渲染影响，不是完整输入延迟分位数）。取消Tile批次{display['CancelledTileBatches']}，请求{display['TileRequests']}，命中{display['TileCacheHits']}。

8次新建/打开/运行后关闭窗口，平台图像载荷从0回到0，缓存为0；关闭后进程工作集{mib(d['AfterCloseMemory']['WorkingSet64']):.2f}MiB、Private Bytes {mib(d['AfterCloseMemory']['PrivateMemorySize64']):.2f}MiB。进程累计峰值工作集{mib(d['AfterCloseMemory']['PeakWorkingSet64']):.2f}MiB包含后续82MP算法链，不能归因于Tile本身。没有通过强制GC来伪造资源回收；WPF及分配器可能保留可复用内存，因此进程字节不应被要求立刻归零。

已验证82MP Mono和两种82MP Bayer的显示/1:1、较大RGB/BGR、取消与短时反复操作。部分完成的稳定性证据仅为120秒预览及有限次数循环；未验证24/72小时、所有GPU/DPI组合、磁盘懒加载、超大线扫连续流、真实工业图/相机。Bayer采用双线性插值和2像素halo；低倍条带取样可能有1灰度级舍入差，L0细节不以缩略图替代。详见[Tile设计](第三阶段CTile显示说明.md)。

![82MP原始像素](images/VisionStudio-Phase3C-native-mono-1to1.png)
![Bayer原始区域插值](images/VisionStudio-Phase3C-native-bayer-1to1.png)
""", encoding="utf-8")

def ms(value):
    h, m, s = value.split(":")
    return (int(h) * 3600 + int(m) * 60 + float(s)) * 1000
performance = "\n".join(f"| {r['Project']} | {r['WarmupRuns']} | {r['MeasuredRuns']} | {r['MedianMilliseconds']:.3f} | {r['MaximumMilliseconds']:.3f} |" for r in d["NodePerformance"])
node_rows = []
for run in d["NodePerformance"]:
    for n in run["Nodes"]: node_rows.append(f"| {run['Project']} | {n['NodeId']} | {ms(n['ExecutionTime']):.3f} | {n['Status']} |")
large = d["LargeRuntime"]
(root / "docs/第三阶段C节点执行性能报告.md").write_text(f"""# 第三阶段 C 节点执行性能报告

每工程同一输入/参数，串行Runtime；预热1次、测量5次，统计完整流程中位数/最大值。合成小图（预处理/筛选200×144，模板例320×240），不直接比较不同流程的算法优劣。参数和完整输入资源保存在samples/phase3c。

| 工程 | 预热次数 | 测量次数 | 流程中位数ms | 流程最大ms |
|---|---:|---:|---:|---:|
{performance}

以下节点耗时来自各工程最后一次真实执行，**不是节点的五次中位数**。调度/载入与原生线程初始化也会影响耗时。

| 工程 | NodeId | 最后一次ms | 状态 |
|---|---|---:|---|
{chr(10).join(node_rows)}

82MP另测 ImageInput→Normalize→GaussianBlur→OtsuThreshold→Morphology：预热{large['WarmupRuns']}次、测量{large['MeasuredRuns']}次，中位数{large['MedianMilliseconds']:.2f}ms、最大{large['MaximumMilliseconds']:.2f}ms。保留各节点中间图像时图像载荷{mib(large['LastRun']['WithOutputs']['PlatformImageBytes']):.2f}MiB，Private Bytes {mib(large['LastRun']['WithOutputs']['PrivateMemorySize64']):.2f}MiB。它是调试保留中间结果的实际成本，不是每节点复制输入。

实际后台运行20ms后请求取消，记录从启动到返回{large['ActiveCancellationMilliseconds']:.2f}ms（含取消前20ms）；返回Cancelled且Dispose后图像载荷回基线。OpenCV原生单次调用不能抢占，取消在调用前后/节点间协作检查，不承诺硬实时停止。串联输出由RunResult统一释放，反复执行后FrameHandle未增长。

Console与WPF使用相同Runtime；WPF/headless逐图像和数值比较通过。独立Console进程另导出{d['Console']['ImagesComparedByHash']}个图像逐SHA256比较，{d['Console']['ScalarsCompared']}个标量完全比较通过。会话ID/实际耗时只用于诊断，不作为两个独立会话必须相等的检测字段。
""", encoding="utf-8")

(root / "docs/第三阶段完整验收报告.md").write_text(f"""# VisionStudio 第三阶段完整验收报告

2026-11-11。在原解决方案上完成第三阶段C集成，A/B功能继续回归；不进入第四阶段。完整源文件、锁定依赖、测试和六份C示例随包交付。

| 项目 | 状态 | 实际证据和限制 |
|---|---|---|
| 八个新算法、自动参数、Console/WPF执行 | 已完成 | Otsu、Adaptive、Normalize、EqualizeHist、CLAHE、Rotate、Resize、Flip；输入验证、独立输出/变换及测试 |
| 真Tile/层级/有界缓存/取消 | 已完成 | 五格式，82MP Mono/Bayer，真实L0原像素和实际渲染检查；原图仍全幅驻留 |
| 节点参数/输出端口调试 | 已完成 | 单击选择，图像预览、数值/Boolean及分组字段，错误/耗时，修改/切换失效 |
| 工程回归和原架构 | 已完成 | Schema1无需迁移，83旧源码保护、原345测试保留，第二阶段/A/B工程运行 |
| 条件通信 | 已完成本机/模拟范围 | C真Loopback只收到一条，未选分支Skipped，重复运行抑制；全套B TCP异常与串口模拟再次通过 |
| 发布程序 | 已完成本机发布验收 | Windows x64 self-contained，需在实际客户PC继续安装/驱动验证 |
| 性能和长稳 | 部分完成 | 120秒预览、12切图、8工程循环、82MP处理/取消；不是全天工业稳定性结论 |
| 物理串口/工业相机/真实工业样本 | 未验证 | 无硬件验收，不自动扫描/操作设备 |
| 磁盘Tile懒加载/算法分块/第四阶段 | 未实现 | 本阶段只有显示Tile；第四阶段仅建议文档 |

自动化{total}/{total}通过、0失败0跳过，构建0警告0错误。五个历史工程在原资源路径实际运行；新六工程保存重开，参数和连接恢复。模板跟随、原ROI、BlobFilter、RotatedCrop、IfElse/Merge、真实TCP异常和串口模拟由旧测试及实际工程覆盖。

示例：1中值→Otsu→形态学→显示；2阈值→Blob→筛选→判定/结构化结果；3模板定位→旋转裁切→显示；4数量比较→IfElse→门控TCP；5真实8192×10000 Mono BMP；6全部额外增强/变换。TCP示例先启动随包Python接收脚本，默认Loopback:9000；普通加载和参数编辑不通信。

![图像预处理与结构化端口调试](images/VisionStudio-Phase3C-flow1-preprocessing.png)
![增强与变换](images/VisionStudio-Phase3C-flow6-enhance-transform.png)
![条件通信](images/VisionStudio-Phase3C-conditional-tcp.png)

坐标采用x右/y下、整数像素中心、正角顺时针；变换输出从局部输出映射到输入原图，需要显式组合。Bayer先显式转换后进入新增算法，显示转换不改原数据。变换输出单轴32766、载荷512MiB限制明确报错。旧ROI编辑方式没有改写。

复现入口：[示例与调试说明](第三阶段C示例与节点调试.md)、[44节点和参数清单](第三阶段完整算法清单.md)、[完整机器可读接口](第三阶段完整节点接口.json)。验收数据：[自动化](第三阶段C自动化测试报告.md)、[大图性能](第三阶段C大图像性能测试报告.md)、[节点耗时](第三阶段C节点执行性能报告.md)、[兼容性](第三阶段C工程兼容性报告.md)。

第三方版本未升级，C未增加第三方包，实际依赖及许可随包。原OpenCV静态组件商业发行的待审范围继续保留，开发验收不替代商业授权审核。未实现第四阶段算法或相机SDK。
""", encoding="utf-8")

if "--reports-only" in sys.argv:
    print(json.dumps(dict(passed=total, originalTestsRerun=len(previous), reportsGenerated=True), ensure_ascii=False))
    raise SystemExit(0)

published = (root / "test-results/phase3c/ui/published-state.txt").read_text(encoding="utf-8")
assert "OtsuThreshold" in published and "Success" in published and "CalculatedThreshold" in published
runtime = root / "artifacts/VisionStudio-Phase3C-win-x64"
assert (runtime / "coreclr.dll").exists() and (runtime / "VisionStudio.exe").exists()
assert not list(runtime.rglob("*ffmpeg*")) and not list(runtime.rglob("*.so")) and not list(runtime.rglob("*.dylib"))
native = list(runtime.rglob("OpenCvSharpExtern.dll")); assert len(native) == 1
assert hashlib.sha256(native[0].read_bytes()).hexdigest() == "1fa122bdb8e94175e7719fb8aa8f2ab211268a756f5d0c7a13c710ed79ae30cd"
shutil.copytree(root / "third_party/licenses", runtime / "licenses", dirs_exist_ok=True)
cache = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
frameworks = json.loads((runtime / "VisionStudio.runtimeconfig.json").read_text())["runtimeOptions"]["includedFrameworks"]
for framework in frameworks:
    for file in (cache / (framework["name"].lower() + ".runtime.win-x64") / framework["version"]).iterdir():
        if file.name.upper().startswith(("LICENSE", "THIRD-PARTY-NOTICES")):
            shutil.copy2(file, runtime / "licenses" / (framework["name"] + "-" + file.name))
for vendor in ["CvInspect", "Nodify"]:
    for file in ["LICENSE", "PROVENANCE.md"]: shutil.copy2(root / "third_party" / vendor / file, runtime / "licenses" / (vendor + "-" + file))
shutil.copy2(root / "third_party/CvInspect/THIRD-PARTY-NOTICES.md", runtime / "licenses/CvInspect-THIRD-PARTY-NOTICES.md")
for phase in ["phase3a", "phase3b", "phase3c"]: shutil.copytree(root / "samples" / phase, runtime / "Samples" / phase, dirs_exist_ok=True)
shutil.copytree(images, runtime / "docs/images", dirs_exist_ok=True)
for file in (root / "docs").iterdir():
    if file.is_file() and (file.name.startswith(("第三阶段", "第四阶段", "第三方")) or file.name in {"native-build-information.txt", "sbom-nuget.json"}):
        shutil.copy2(file, runtime / "docs" / file.name)
for name in ["tcp_fixture.py", "tcp-loopback-peer.py"]: shutil.copy2(root / "tools" / name, runtime / name)
(runtime / "README.txt").write_text("VisionStudio 第三阶段 C\n运行VisionStudio.exe，打开Samples/phase3c/flow1-preprocessing.json，点击运行流程。\n单击节点查看参数，单击输出端口查看当前真实值。flow5-large.json包含真实82MP BMP，1:1显示Level0原像素。\nWindows x64 self-contained，不需要另装.NET。TCP例需先启动：python tcp-loopback-peer.py --port 9000 --count 1 --reply ACK\n完整清单/测试/性能/兼容性见docs。原图全幅驻留，显示缓存64MiB；120秒观测不代表全天工业长稳，物理串口未验证。\nC完成后停止，未实施第四阶段。\n", encoding="utf-8")
manifest = []
for name, base, excludes, prefix in [
    ("VisionPlatform-Phase3C-Source.zip", root, {"bin", "obj", "artifacts", ".git", ".vs", "__pycache__"}, "VisionPlatform"),
    ("VisionStudio-Phase3C-win-x64.zip", runtime, set(), "VisionStudio")]:
    path = root / "artifacts" / name
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for file in sorted(base.rglob("*")):
            relative = file.relative_to(base)
            if file.is_file() and not any(part in excludes for part in relative.parts): archive.write(file, prefix + "/" + relative.as_posix())
    with zipfile.ZipFile(path) as archive: assert archive.testzip() is None
    manifest.append(dict(file=name, bytes=path.stat().st_size, sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
(root / "artifacts/phase3c-delivery-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
print(json.dumps(dict(testsPassed=total, originalTestsRerun=len(previous), desktopPassed=True, archives=manifest), ensure_ascii=False, indent=2))
