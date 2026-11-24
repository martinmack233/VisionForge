"""Assemble Phase 3A from actual TRX, desktop, and published-app evidence."""
from pathlib import Path
import hashlib
import json
import os
import shutil
import xml.etree.ElementTree as ET
import zipfile

root = Path(__file__).resolve().parent.parent
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
final = root / "test-results/phase3a/final"
rows, names = [], set()
for file in sorted(final.glob("*.trx")):
    tree = ET.parse(file)
    counters = tree.find("t:ResultSummary/t:Counters", ns).attrib
    test_class = tree.find("t:TestDefinitions/t:UnitTest/t:TestMethod", ns).attrib["className"]
    results = tree.findall("t:Results/t:UnitTestResult", ns)
    names.update(item.attrib["testName"] for item in results)
    rows.append(dict(assembly=test_class.split(".Tests")[0] + ".Tests", file=file.relative_to(root).as_posix(), total=int(counters["total"]), passed=int(counters["passed"]),
                     failed=int(counters["failed"]), skipped=int(counters["notExecuted"])))
assert len(rows) == 5 and all(r["total"] == r["passed"] and r["failed"] == r["skipped"] == 0 for r in rows)
old_names = {r.attrib["testName"] for p in (root / "test-results/phase2/final").glob("*.trx")
             for r in ET.parse(p).findall("t:Results/t:UnitTestResult", ns)}
assert len(old_names) == 163 and old_names <= names, "A Phase 2 test was removed, renamed, or not rerun"
desktop = json.loads((root / "artifacts/phase3a-acceptance/summary.json").read_text(encoding="utf-8"))
assert desktop["Passed"] and desktop["OldSchema1Opened"] and desktop["BuffersAfter"] == desktop["BuffersBefore"] == 0
assert len(desktop["Flows"]) == 3 and all(f["Passed"] and f["ConsoleMatchesWpf"] and f["ReopenedMatches"] for f in desktop["Flows"])
published = (root / "test-results/phase3a/ui/published-state.txt").read_text(encoding="utf-8")
assert "FilteredBlobs" in published and "Success" in published, "Published UI evidence is missing"
for vendor in ["Nodify", "CvInspect"]:
    hashes = json.loads((root / "third_party" / vendor / "upstream-sha256.json").read_text(encoding="utf-8-sig"))
    for item in hashes:
        item_path = item.get("Path", item.get("path"))
        expected = item.get("SHA256", item.get("sha256")).upper()
        if item_path.endswith(".csproj"):
            continue
        target = root / "third_party" / item_path if vendor == "Nodify" else root / "third_party/CvInspect" / Path(item_path).relative_to("src/CvInspect")
        assert hashlib.sha256(target.read_bytes()).hexdigest().upper() == expected

total = sum(row["total"] for row in rows)
summary = dict(date="2026-11-02 Asia/Shanghai", passed=total, failed=0, skipped=0, suites=rows,
               originalPhase2TestsRerun=len(old_names), desktop=desktop, publishedApplicationVerified=True)
(final / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
for log in ["restore.log", "build.log", "test.log", "publish.log", "desktop.log"]:
    shutil.copy2(final.parent / log, final / log)
images = root / "docs/images"; images.mkdir(exist_ok=True)
for i in range(1, 4):
    shutil.copy2(root / f"artifacts/phase3a-acceptance/flow{i}.png", images / f"VisionStudio-Phase3A-flow{i}.png")
table = "\n".join(f"| {row['assembly']} | {row['total']} | {row['passed']} | {row['failed']} |" for row in rows)
large = desktop["LargeImage"]
flows = "\n".join(f"- {row['Project']}：{len(row['Nodes'])} 个节点成功；输出校验值 `{row['Output']}`，Console/WPF 与重开一致。" for row in desktop["Flows"])
report = f"""# VisionStudio 第三阶段 A 验收报告

2026-11-02，Windows x64，.NET 8。本次在原解决方案扩展，未重新创建平台、重写 Runtime 或进入第三阶段 B。

## 交付范围与兼容性

新增8个独立节点：MedianBlur、Threshold、Morphology、BlobFilter、CropImage、RotatedCrop、ImageDisplay、TextDisplay。浅蓝主题采用集中 ResourceDictionary；工具箱统一72 DIP卡片、宽度伸展、中文主标题/英文ID、长名称提示和中文/英文搜索，分类从实际编辑元数据生成。

主Agent先审核兼容设计与文件边界，再分批协调四专业角色，分别负责主题、处理/筛选、裁切、显示/会话。Runtime原调度与图执行代码未改，Contracts只增加独立仿射记录和Blob可选元数据，旧构造参数/端口语义保留。SchemaVersion仍为1；算法参数保存在原Nodes.Parameters；现有ROI模型、编辑方式及sidecar布局继续使用。没有新增相机SDK、多ROI或B/C功能。

所有算法通过原注册发现机制进入RuntimeExecutor；WPF/ViewModel不含视觉算法。显示节点发布数据与独立共享图像租约，Runtime不引用WPF。滤波保持输入格式，Threshold仅接受Mono8并明确提示转换，Bayer处理/裁切要求显式转换。

Blob筛选掩膜来自真实标签像素行程，保留孔洞及原检测输入栅格，空结果归零。圆度采用包含孔洞边的4邻域数字周长，区别于平滑轮廓指标。裁切输出显式局部→原始坐标变换，正角顺时针，像素中心约定见《第三阶段A裁切坐标约定》。原图与裁切图显示按坐标空间分离，筛选显示不再混入未筛选目标。参数/连线/工程变化使旧结果失效。

## 实际构建与测试

最终 dotnet restore、dotnet build -c Release、dotnet test -c Release 全部退出0，构建0警告0错误。**{total} 项通过，0失败，0跳过**。原第二阶段163项逐测试名称核对均在本轮重新执行通过，没有删失败测试。新增{total - 163}项覆盖新处理模式/格式/参数、真实掩膜/孔洞/筛选/排序、裁切/旋转/越界/坐标/共享释放、主题卡片实际布局、显示端口和会话失效。

| 测试记录 | 总数 | 通过 | 失败 |
|---|---:|---:|---:|
{table}

原始TRX、构建日志与summary.json在test-results/phase3a/final。新功能验证不是硬编码返回值；测试图案具有已知几何，算法由实际OpenCV/Runtime执行。

首次整体测试发现空显示参数与原参数元数据约束不兼容，补充了实际生效的ShowOverlays与Prefix配置并新增回归；原失败测试没有修改，首次失败TRX保留在integration-attempt1。截图复核修正了结果列过窄和长文本横向溢出，并按真实测量布局适应流程。修复后重新完整执行243项全部通过。

## 三条真实桌面流程

tools/Phase3Acceptance启动生产MainWindow，用正式工程文件运行三条要求流程。每条均保存后新建会话状态、重新打开再运行，另外启动独立Console进程核对文本或最终图像逐行像素SHA256。窗口截图来自WPF实际渲染。

{flows}

流程一的真实筛选掩膜非零像素数等于保留目标面积之和，并确认面积条件去除第三个小目标。流程二校验裁切中心经CoordinateTransform映射后等于实际匹配Pose中心。流程三明确连接Threshold.ForegroundPixelCount:int→TextDisplay.Integer，文本为本次实测数值。全流程节点耗时由Runtime记录在桌面summary。

旧工程test-results/phase2/ui/interactive.json实际打开并运行成功，保持Schema1、原节点参数和连线。发布目录VisionStudio.exe经computer-use实际启动并打开/运行第三阶段流程，记录保留在test-results/phase3a/ui。UI修改不会覆盖旧工程坐标或连接。

![Blob筛选与显示](images/VisionStudio-Phase3A-flow1.png)

![模板姿态旋转裁切](images/VisionStudio-Phase3A-flow2.png)

![中值阈值文本输出](images/VisionStudio-Phase3A-flow3.png)

## 大图与资源

本轮再次创建并导入8192×10000 Mono8图像，在真实窗口检查1:1、缩放、平移及坐标逆变换。预览为{large['PreviewWidth']}×{large['PreviewHeight']}，缓存像素载荷{large['PreviewBytes']:,}字节，预算{large['CacheBudget']:,}字节；加载/预览本轮{large['ElapsedMilliseconds']:.2f}ms，Dispatcher心跳{large['DispatcherTicks']}次，最大采样间隔{large['MaximumHeartbeatGapMilliseconds']:.2f}ms。该文件刚生成，属于暖缓存短时观测，不是冷盘节拍保证。

加载前Private Bytes {large['ProcessBefore']['PrivateMemorySize64']:,}，持有预览时{large['ProcessWithPreview']['PrivateMemorySize64']:,}，关窗后{desktop['ProcessAfterClose']['PrivateMemorySize64']:,}。所有流程及大图窗口关闭后FrameHandle.ActiveBytes由0回到0。进程/原生分配器可能保留内存，本报告不据此声称全天长稳或全部原生分配零泄漏。

显示缓存64MiB、结果图像512MiB预算延续原设计，它们不是全进程或算法执行峰值预算。Blob像素行程还占托管内存，密集碎片目标需后续专项压力评估。大图仍为缩略预览，尚未实现全分辨率金字塔/瓦片。

## 发布与边界

artifacts/VisionStudio-Phase3A-win-x64为self-contained发布目录，ZIP随附运行时、许可证及Samples/phase3a三个工程；解压运行VisionStudio.exe。源代码ZIP包含解决方案、锁文件、测试、报告和复现工具。发布manifest记录文件大小与SHA256。

样本均为合成图，本次未声称完成真实工业样本、相机、连续生产节拍或商业发行授权闭环。OpenCV原生内部组件的待核验范围继承原《第三方许可证清单》，没有因新增功能而更换来源或混版DLL。第三阶段B只提供接口建议，**未实施，A完成后停止**。
"""
(root / "docs/第三阶段A验收报告.md").write_text(report, encoding="utf-8")
runtime = root / "artifacts/VisionStudio-Phase3A-win-x64"
assert (runtime / "coreclr.dll").exists() and (runtime / "PresentationFramework.dll").exists()
assert not list(runtime.rglob("*ffmpeg*"))
shutil.copytree(root / "third_party/licenses", runtime / "licenses", dirs_exist_ok=True)
frameworks = json.loads((runtime / "VisionStudio.runtimeconfig.json").read_text())["runtimeOptions"]["includedFrameworks"]
cache = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
for framework in frameworks:
    package = cache / (framework["name"].lower() + ".runtime.win-x64") / framework["version"]
    for item in package.iterdir():
        if item.name.upper().startswith(("LICENSE", "THIRD-PARTY-NOTICES")):
            shutil.copy2(item, runtime / "licenses" / (framework["name"] + "-" + item.name))
for vendor in ["CvInspect", "Nodify"]:
    for file in ["LICENSE", "PROVENANCE.md"]:
        shutil.copy2(root / "third_party" / vendor / file, runtime / "licenses" / (vendor + "-" + file))
shutil.copy2(root / "third_party/CvInspect/THIRD-PARTY-NOTICES.md", runtime / "licenses/CvInspect-THIRD-PARTY-NOTICES.md")
shutil.copytree(root / "samples/phase3a", runtime / "Samples/phase3a", dirs_exist_ok=True)
shutil.copytree(images, runtime / "images", dirs_exist_ok=True)
for file in ["第三阶段A节点与使用说明.md", "第三阶段A验收报告.md", "第三阶段A裁切坐标约定.md", "第三方许可证清单.md", "native-build-information.txt"]:
    shutil.copy2(root / "docs" / file, runtime / file)
(runtime / "README.txt").write_text("VisionStudio 第三阶段 A\n解压后运行 VisionStudio.exe。已包含 .NET 8 Windows Desktop x64运行时。\n打开 Samples/phase3a/flow1.json、flow2.json、flow3.json 并运行。\n详见 第三阶段A节点与使用说明.md。未进入第三阶段B。\n", encoding="utf-8")
manifest = []
for name, base, excludes, prefix in [
    ("VisionPlatform-Phase3A-Source.zip", root, {"bin", "obj", "artifacts", ".git", ".vs", "__pycache__"}, "VisionPlatform"),
    ("VisionStudio-Phase3A-win-x64.zip", runtime, set(), "VisionStudio")]:
    path = root / "artifacts" / name
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for item in sorted(base.rglob("*")):
            relative = item.relative_to(base)
            if item.is_file() and not any(part in excludes for part in relative.parts): archive.write(item, prefix + "/" + relative.as_posix())
    with zipfile.ZipFile(path) as archive: assert archive.testzip() is None
    manifest.append(dict(file=path.name, bytes=path.stat().st_size, sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
(root / "artifacts/phase3a-delivery-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
print(json.dumps(dict(testsPassed=total, originalTestsRerun=len(old_names), desktopPassed=True, archives=manifest), ensure_ascii=False, indent=2))
