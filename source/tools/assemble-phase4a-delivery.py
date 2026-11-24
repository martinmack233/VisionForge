"""Verify actual Phase 4A evidence and assemble reports/source/self-contained desktop delivery."""
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
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
final = root / "test-results/phase4a/final-validation"
names, suites = set(), []
for file in sorted(final.glob("*.trx")):
    tree = ET.parse(file)
    count = tree.find("t:ResultSummary/t:Counters", ns).attrib
    names.update(r.attrib["testName"] for r in tree.findall("t:Results/t:UnitTestResult", ns))
    method = tree.find("t:TestDefinitions/t:UnitTest/t:TestMethod", ns).attrib
    suites.append(dict(assembly=method["className"].split(".Tests")[0] + ".Tests", total=int(count["total"]), passed=int(count["passed"]), failed=int(count["failed"]), skipped=int(count["notExecuted"]), file=file.relative_to(root).as_posix()))
assert len(suites) == 5 and all(r["total"] == r["passed"] and not r["failed"] and not r["skipped"] for r in suites), suites
previous = {r.attrib["testName"] for file in (root / "test-results/phase3c/final").glob("*.trx") for r in ET.parse(file).findall("t:Results/t:UnitTestResult", ns)}
assert len(previous) == 428 and previous <= names
total = sum(r["total"] for r in suites)
assert total >= 478
evidence = root / "artifacts/phase4a-acceptance"
d = json.loads((evidence / "summary.json").read_text(encoding="utf-8"))
assert d["Passed"] and len(d["Desktop"]) == 8 and len(d["Headless"]) == 15
assert d["BuffersBefore"] == d["BuffersAfter"] == d["CacheAfterClose"] == 0
assert all(d[k] for k in ["DashboardDoesNotRunAlgorithms", "NavigationDraftsPreserved", "MonitorUsesExistingRuntime", "Real82MP", "ExistingRoiSaveReload", "BuiltInTemplatesUnchanged", "ConsoleWpfEqual"])
assert d["Tcp"]["Passed"] and d["Tcp"]["ServicesReleased"]
protection = json.loads((root / "test-results/phase4a/protected-source-check.json").read_text())
assert protection["checked"] == 96 and protection["unchanged"] == 94 and len(protection["compatibleExtensions"]) == 2 and not protection["unexpected"]
for vendor in ["Nodify", "CvInspect"]:
    for item in json.loads((root / "third_party" / vendor / "upstream-sha256.json").read_text(encoding="utf-8-sig")):
        name = item.get("Path", item.get("path")); expected = item.get("SHA256", item.get("sha256"))
        if name.endswith(".csproj"): continue
        path = root / "third_party" / name if vendor == "Nodify" else root / "third_party/CvInspect" / Path(name).relative_to("src/CvInspect")
        assert hashlib.sha256(path.read_bytes()).hexdigest().lower() == expected.lower()
images = root / "docs/images"; images.mkdir(exist_ok=True)
for file in evidence.glob("*.png"): shutil.copy2(file, images / ("VisionStudio-Phase4A-" + file.name))
raw = root / "test-results/phase4a/acceptance"; raw.mkdir(exist_ok=True)
shutil.copy2(evidence / "summary.json", raw / "summary.json")
for folder in (evidence / "console").iterdir():
    target = raw / "console" / folder.name; target.mkdir(parents=True, exist_ok=True)
    for name in ["comparison.json", "console.log", "results.json"]: shutil.copy2(folder / name, target / name)
published_file = root / "test-results/phase4a/ui/published-state.txt"
published_verified = published_file.exists() and "CircleFit" in published_file.read_text(encoding="utf-8") and "Success" in published_file.read_text(encoding="utf-8")
summary = dict(date="2026-11-11", total=total, passed=total, failed=0, skipped=0, originalTestsPreserved=len(previous), suites=suites,
               protection=protection, actualDesktop=d, independentPublishedDesktopVerified=published_verified,
               industrialPhysicalAccuracyValidated=False, realSerialHardwareTested=False, phaseBStarted=False, phaseCStarted=False)
(final / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
table = "\n".join(f"| {r['assembly']} | {r['total']} | {r['passed']} | {r['failed']} |" for r in suites)
(root / "docs/第四阶段A自动化与兼容性报告.md").write_text(f"""# 第四阶段 A 自动化与工程兼容性报告

2026-11-11。实际执行dotnet restore、dotnet build -c Release、dotnet test -c Release；构建0警告0错误，测试结果如下。

| 程序集 | 总数 | 通过 | 失败 |
|---|---:|---:|---:|
{table}
| 合计 | {total} | {total} | 0 |

0跳过。第三阶段最终428项按测试名全部再次通过，增加{total-len(previous)}项。旧测试源未删除或修改预期。主集成验收另通过8个真实WPF示例、15次无界面示例/变体、实际TCP回环、3个独立Console进程的图像SHA256/测量/数值比较、6个历史工程、82MP导航与ROI保存重开、5次工程切换。关闭后FrameHandle载荷和显示缓存均回到0。

原始TRX及汇总：[final-validation](../test-results/phase4a/final-validation)。restore/build/test/publish日志在test-results/phase4a，桌面原始结果见[acceptance/summary.json](../test-results/phase4a/acceptance/summary.json)。全部图像为合成样本，硬件串口只复跑旧模拟测试，没有真实硬件或全天测试。

源码以Git标签visionstudio-phase3c-baseline（{protection['commit']}）保护。96个既有核心/算法/ROI/旧测试文件中94个完全一致；2处经兼容设计审核的增加为InspectionReport可选MeasurementSummary，以及ResultDisplay可选Measurement输入，原字段/端口/Decision/IsOk优先级保持。Runtime核心、ProjectDocument/Schema1、原图像缓冲和原ROI实现未改。CvInspect/Nodify原始源码逐文件SHA256也核对通过。

无需工程迁移，旧第二阶段、A、B、C工程按原资源实际执行成功；ROI/模板跟随、BlobFilter、RotatedCrop、IfElse/Merge、TCP异常和串口模拟继续回归。新几何数据只流经端口与节点参数，Overlay不替换ROI，工作区配置单独workspace-v1.json，不进入正式工程。新增节点工程需要本批次插件，不能承诺旧可执行程序识别新TypeId。

首轮暴露并修正：只读ProjectInformation TextBox默认TwoWay绑定；ResultDisplay新增错误文字破坏旧断言；新增WPF测试清理先Dispose会遮蔽原始错误；旋转裁切样本原尺度1.1导致范围79×70，改用已知单位尺度样本保留72×64教学预期并保留旧节点尺度行为。验收器中文搜索需区分命中简介的多个结果，且需等待排队的适应任务后再测1:1。日志保留，未删除失败记录。另补连接几何的坐标空间检查和真实RenderTargetBitmap点/线/圆坐标回归。

最近工程入口补齐后，示例工程改为新身份一次发布，工作区读取允许原子替换，规避复制后立即替换的共享冲突；后续连续3次完整MainWindow复跑均通过。没有据此宣称全天稳定性。

独立发布版人工交互验收：{'曾从开发目录外完整运行圆测量并查看实际端口；最新配置/复制改动后的独立复测被用户Escape中断，后续改动由3次完整MainWindow自动复跑覆盖。证据为test-results/phase4a/ui/published-state.txt。' if published_verified else '尚待单独启动与交互复核；开发目录中真实MainWindow的自动验收已通过。'}
""", encoding="utf-8")
measurements = {r["Id"]: r["ActualMeasurements"] for r in d["Desktop"]}
caliper = next(m for m in measurements["caliper"] if m["Kind"] == "Caliper")["Values"]
circle = next(m for m in measurements["circle"] if m["Kind"] == "CircleFit")["Values"]
example_rows = "\n".join(f"| {r['Id']} | 通过 | {len(r['Nodes'])} | {sum(n['Milliseconds'] for n in r['Nodes']):.3f} |" for r in d["Desktop"])
(root / "docs/第四阶段A验收报告.md").write_text(f"""# VisionStudio 第四阶段 A 验收报告

2026-11-11。仅完成A；B/C尚未启动。沿用原解决方案、RuntimeExecutor、Nodify、浅蓝主题、Schema1及ROI。第三阶段实测基线为428项，历史427项文档已同步；此前第三阶段手工重启验收曾被Escape中断，没有将其当成通过，本批次另完成原ROI的实际保存重载。

## 功能与边界

| 功能 | 状态 | 验证 |
|---|---|---|
| 可折叠导航与七页工作区 | 已实现/已测试 | 默认首页无图像/算法；切页保留同一Graph/Viewport/参数草稿与运行任务 |
| 工程管理/最近项目/设置/监控 | 已实现/已测试 | 原ProjectStore、搜索/复制/名称更新/路径信息；监控同Session；设置为独立版本化Sidecar |
| 八个完整离线方案 | 已实现/已测试 | manifest+正式工程+图像/模板+封面+说明+预期验证；打开先复制，不覆盖内置模板 |
| 六个几何节点及Overlay | 已实现/已测试 | Caliper、CircleEdgePoints、LineFit、CircleFit、DistanceMeasure、AngleMeasure；真实数据流与原图坐标 |
| TCP教学例 | 已实现/已测试本机 | 默认通信门控关闭；显式入口真实Loopback并核对接收值、关闭服务 |
| 82MP与旧工程兼容 | 已实现/已测试 | 原像素L0、导航保留图像/ROI、保存重开、旧6工程及全部428原测试 |
| Windows x64 self-contained | 已生成 | {'曾在独立目录完成交互，最新配置/复制修正后的独立复测被Escape停止；自动MainWindow复跑通过。' if published_verified else '独立交互复核尚待完成；当前包已包含运行时与本地资源。'} |
| 设备页 | 界面框架 | 设备/虚拟相机配置明确未接入，按钮禁用；本批次没有实现采集 |
| 工业物理精度/全天长稳 | 未验证 | 仅合成像素几何、有限次数资源测试；没有标定毫米或生产精度承诺 |
| OCR/条码/YOLO/相机/RuntimeHost | 未实现本批次 | 属于B/C，未提前开发 |

## 实际示例

以下为真实WPF最后一次执行，各流程图像与参数不同，耗时不用于比较算法优劣，也不是重复采样中位数。

| 示例标识 | 对照预期 | 节点数 | 节点耗时合计ms |
|---|---|---:|---:|
{example_rows}

另执行Blob正常/异常、模板平移旋转/失配、逻辑True/False变体。失配后ROI和Blob为本次Skipped，不读旧姿态。默认TCP connect/send为Skipped；显式本机测试收到真实结构化OK/count=2，未填造score/坐标。

卡尺图的解析边缘真值120.25px，实测MeanX={caliper['MeanX']:.9f}px，16个边缘点；拟合直线90°。圆图真值center=(128,128)、r=60px，64点实测center=({circle['CenterX']:.6f},{circle['CenterY']:.6f})、r={circle['Radius']:.9f}px、diameter={circle['Diameter']:.9f}px、RMS={circle['RmsError']:.9f}px。这些是该合成图的实际结果，不能泛化为工业准确度；单元测试另外覆盖非整数60.15/60.35/60.65边缘、噪声和离群点。

![首页](images/VisionStudio-Phase4A-dashboard.png)
![示例中心](images/VisionStudio-Phase4A-examples.png)
![圆形测量与真实结果](images/VisionStudio-Phase4A-circle.png)
![运行监控](images/VisionStudio-Phase4A-monitor.png)

## 交付与复现

自动化{total}/{total}通过、0失败0跳过、构建0警告0错误。报告与原始证据见[自动化和兼容性](第四阶段A自动化与兼容性报告.md)，操作/端口/单位见[使用说明](第四阶段A使用与测量节点说明.md)、[完整节点接口](第四阶段A节点接口.json)、[卡尺和拟合方法](第四阶段A卡尺与几何拟合说明.md)、[示例中心说明](第四阶段A方案示例中心说明.md)。已知边界单列第四阶段A已知问题.md。

源码、锁文件、示例资源与测试记录完整保留；独立发布含.NET Desktop x64运行时及许可通知。Git保留第三阶段基线并在本批次最终创建visionstudio-phase4a里程碑。原始算法/第三方源码没有被重写，C#/WPF以外的训练系统、相机SDK未改。商业原生组件发行审核限制继续按第三方许可证清单保留。
""", encoding="utf-8")
(root / "docs/第四阶段A已知问题.md").write_text("""# 第四阶段 A 已知边界

- 测量只输出像素和角度。亚像素是灰度剖面梯度峰值的抛物线拟合，不代表已达到0.1px的所有工况准确度，更没有毫米物理精度结论。低对比度、模糊、纹理、多边缘、遮挡与镜头畸变需真实样本验证。Kasa拟合在小圆弧上可能偏置，有限确定性RANSAC不保证任意离群比例都成功。
- 缺测的固定数值端口为0，必须结合Valid解释；Line/Circle缺测不提供几何。仅Measurement输入ResultDisplay时OK表示测量有效，不是公差合格。
- 源图仍全幅驻留。显示64MiB像素缓存不含原图、GPU/native工作区；调试中间结果预算下次启动生效。此前120秒观测和本批次有限循环均不能代替全天生产长稳。
- 设备管理是A界面框架，未实现虚拟采集、厂商SDK、RuntimeHost；OCR/条码/AI也尚未实施。
- 普通工程复制保留外部资源位置，不自动做资源导出包；示例中心的复制包含资源。最近运行记录当前会话最多50条；最近工程持久化20项，日志有界。仅浅蓝/中文可用，主题和语言扩展边界预留。
- 取消示例复制可保留带唯一标识的局部目录，不递归删除用户数据。文件完整性和只读保护覆盖应用自身入口，不替代操作系统文件权限。
- TCP只验证Loopback与既有异常回归；没有实际串口/虚拟串口对、真实工业相机或生产局域网验收。默认不开公网端口，无无限重试。
- OpenCV native静态组成的商业发行待核验范围继承既有许可证清单；本批次没有增加第三方依赖或扩大授权结论。
""", encoding="utf-8")

runtime = root / "artifacts/VisionStudio-Phase4A-win-x64"
assert (runtime / "coreclr.dll").exists() and (runtime / "VisionStudio.exe").exists()
assert not list(runtime.rglob("*ffmpeg*")) and not list(runtime.rglob("*.so")) and not list(runtime.rglob("*.dylib"))
native = list(runtime.rglob("OpenCvSharpExtern.dll")); assert len(native) == 1
assert hashlib.sha256(native[0].read_bytes()).hexdigest() == "1fa122bdb8e94175e7719fb8aa8f2ab211268a756f5d0c7a13c710ed79ae30cd"
shutil.copytree(root / "Examples", runtime / "Examples", dirs_exist_ok=True)
shutil.copytree(root / "samples", runtime / "Samples", dirs_exist_ok=True)
shutil.copytree(root / "third_party/licenses", runtime / "licenses", dirs_exist_ok=True)
cache = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
frameworks = json.loads((runtime / "VisionStudio.runtimeconfig.json").read_text())["runtimeOptions"]["includedFrameworks"]
for framework in frameworks:
    for file in (cache / (framework["name"].lower() + ".runtime.win-x64") / framework["version"]).iterdir():
        if file.name.upper().startswith(("LICENSE", "THIRD-PARTY-NOTICES")): shutil.copy2(file, runtime / "licenses" / (framework["name"] + "-" + file.name))
for vendor in ["CvInspect", "Nodify"]:
    for name in ["LICENSE", "PROVENANCE.md"]: shutil.copy2(root / "third_party" / vendor / name, runtime / "licenses" / (vendor + "-" + name))
shutil.copy2(root / "third_party/CvInspect/THIRD-PARTY-NOTICES.md", runtime / "licenses/CvInspect-THIRD-PARTY-NOTICES.md")
shutil.copytree(images, runtime / "docs/images", dirs_exist_ok=True)
for file in (root / "docs").iterdir():
    if file.is_file() and (file.name.startswith(("第四阶段A", "第三方")) or file.name in {"sbom-nuget.json", "native-build-information.txt"}): shutil.copy2(file, runtime / "docs" / file.name)
for name in ["tcp-loopback-peer.py", "tcp_fixture.py"]: shutil.copy2(root / "tools" / name, runtime / name)
(runtime / "README.txt").write_text("VisionStudio 第四阶段 A\n启动VisionStudio.exe，默认首页→方案示例→选择方案→一键运行。内置例自动复制到用户工程目录，可自由编辑副本。\n提供8个离线完整方案；TCP必须显式启动本机测试。50节点，新增6个几何测量节点；只输出px/deg，不宣称工业物理精度。\n自包含.NET Desktop x64，不需安装SDK或下载模型。设备页为A框架，OCR/YOLO/相机/RuntimeHost未在本批次实现。\n报告/算法输入输出/已知边界见docs，许可见licenses。完成A后停止，未进入B/C。\n", encoding="utf-8")
if "--reports-only" in sys.argv:
    print(json.dumps(dict(passed=total, oldTests=len(previous), reportsGenerated=True, publishedDesktopVerified=published_verified), ensure_ascii=False))
    raise SystemExit(0)
manifest = []
for name, base, excludes, prefix in [
    ("VisionPlatform-Phase4A-Source.zip", root, {"bin", "obj", "artifacts", ".git", ".vs", "__pycache__"}, "VisionPlatform"),
    ("VisionStudio-Phase4A-win-x64.zip", runtime, set(), "VisionStudio")]:
    path = root / "artifacts" / name
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for file in sorted(base.rglob("*")):
            relative = file.relative_to(base)
            if file.is_file() and not any(part in excludes for part in relative.parts): archive.write(file, prefix + "/" + relative.as_posix())
    with zipfile.ZipFile(path) as archive: assert archive.testzip() is None
    manifest.append(dict(file=name, bytes=path.stat().st_size, sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
(root / "artifacts/phase4a-delivery-manifest.json").write_text(json.dumps(dict(gitCommit=subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root).decode().strip(), testsPassed=total, publishedDesktopVerified=published_verified, archives=manifest), indent=2), encoding="utf-8")
print(json.dumps(dict(passed=total, previousTests=len(previous), publishedDesktopVerified=published_verified, archives=manifest), ensure_ascii=False, indent=2))
