"""Assemble Phase 4B only from measured evidence. No original YOLO implementation/data/weights are copied."""
from pathlib import Path
import hashlib
import json
import os
import shutil
import statistics
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile

root = Path(__file__).resolve().parent.parent
def read(path): return json.loads((root / path).read_text(encoding="utf-8-sig"))
def write(path, value): (root / path).write_text(value, encoding="utf-8")
def dump(path, value): write(path, json.dumps(value, ensure_ascii=False, indent=2))
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
names, suites = set(), []
for file in sorted((root / "test-results/phase4b/final-validation").glob("*.trx")):
    tree = ET.parse(file); count = tree.find("t:ResultSummary/t:Counters", ns).attrib
    method = tree.find("t:TestDefinitions/t:UnitTest/t:TestMethod", ns).attrib
    names.update(r.attrib["testName"] for r in tree.findall("t:Results/t:UnitTestResult", ns))
    suites.append(dict(assembly=method["className"].split(".Tests")[0] + ".Tests", total=int(count["total"]), passed=int(count["passed"]), failed=int(count["failed"]), skipped=int(count["notExecuted"])))
old = {r.attrib["testName"] for f in (root / "test-results/phase4a/final-validation").glob("*.trx") for r in ET.parse(f).findall("t:Results/t:UnitTestResult", ns)}
assert len(old) == 479 and old <= names
assert len(suites) == 5 and all(s["total"] == s["passed"] and not s["failed"] and not s["skipped"] for s in suites)
total = sum(s["passed"] for s in suites); assert total == 569
d = read("artifacts/phase4b-acceptance/summary.json")
assert d["Passed"] and d["RealYoloVerified"] and d["ConsoleWpfEqual"] and d["BuiltInExamplesUnchanged"]
assert len(d["OldProjects"]) == 6 and len(d["Desktop"]) == 3 and d["Examples"] == 12 and d["RegisteredNodes"] == 54
assert d["BuffersBefore"] == d["BuffersAfter"] == d["CacheAfterClose"] == 0 and d["LargeRoi"]["Passed"] and not d["PhaseCStarted"]
protection = read("test-results/phase4b/protected-source-check.json"); assert not protection["unexpected"]
yolo = read("artifacts/phase4b-acceptance/yolo-real.json")
external = read("test-results/phase4b-yolo-external.json"); process = read("test-results/phase4b-yolo-process.json")
assert external["all_passed"] and process["all_passed"] and process["passed"] == 7
large = read("artifacts/phase4b-acceptance/large-roi.json")
evidence = root / "test-results/phase4b/acceptance"; evidence.mkdir(parents=True, exist_ok=True)
for name in ["summary.json", "large-roi.json", "yolo-real.json"]: shutil.copy2(root / "artifacts/phase4b-acceptance" / name, evidence / name)
for folder in (root / "artifacts/phase4b-acceptance/console").iterdir():
    target = evidence / "console" / folder.name; target.mkdir(parents=True, exist_ok=True)
    for name in ["results.json", "console.log", "comparison.json"]: shutil.copy2(folder / name, target / name)
images = root / "docs/images"; images.mkdir(exist_ok=True)
for file in (root / "artifacts/phase4b-acceptance").glob("*.png"):
    # The real external test image is intentionally not redistributed, including in screenshots.
    if file.name == "yolo-real.png": continue
    shutil.copy2(file, images / ("VisionStudio-Phase4B-" + file.name))
published_file = root / "test-results/phase4b/ui/published-state.txt"
published = published_file.exists() and all(s in published_file.read_text(encoding="utf-8") for s in ["VISION 12345", "Success"])
dump("test-results/phase4b/final-validation/summary.json", dict(total=total, passed=total, failed=0, skipped=0, previous479Preserved=True, suites=suites,
    protected=protection, actualWpf=d, actualYolo=yolo, actualProcesses=process, publishedDesktopVerified=published,
    physicalIndustrialAccuracyValidated=False, realSerialHardwareTested=False, allDayStabilityTested=False, phaseCStarted=False))
table = "\n".join(f"| {s['assembly']} | {s['total']} | {s['passed']} | 0 |" for s in suites)
write("docs/第四阶段B自动化与兼容性报告.md", f"""# 第四阶段 B 自动化与兼容性报告

2026-11-11。实际执行 `dotnet restore`、`dotnet build -c Release`、`dotnet test -c Release`。构建 0 警告、0 错误。

| 程序集 | 总数 | 通过 | 失败 |
|---|---:|---:|---:|
{table}
| 合计 | {total} | {total} | 0 |

无跳过；A 的479项按测试名全部重新通过，本轮新增90项。旧测试源未修改或删除。原始TRX与汇总在 [final-validation](../test-results/phase4b/final-validation)，restore/build/test/publish日志在 test-results/phase4b。

独立WPF验收程序实际运行11个离线方案及其变体，另对YOLO默认缺依赖方案核验明确失败；OCR/QR/条码三项经真实窗口运行、单节点、保存重开、空白清旧和独立Console进程比较。真实外部YOLO由C#节点、WPF、Console分别调用，重复框一致并复用6片Tile。实际TCP回环仍收到当前结构化结果；真实OS进程7/7验证取消、超时、输出超限、父子PID退出。关闭后 FrameHandle 与显示缓存均为0。

本轮重新在WPF打开运行的历史工程：{', '.join(d['OldProjects'])}。A的8个旧示例也继续使用相同Schema1运行。旧ROI/模板跟随、BlobFilter、RotatedCrop、IfElse/Merge、TCP异常和串口模拟测试均继续通过。

以 Git `visionstudio-phase4a` 为基线，{protection['checked']}个旧Runtime/Contracts/Imaging/算法/插件/ROI/测试源中{protection['unchanged']}个逐文件一致；唯一允许变更为示例manifest增加可选 ExternalDependencies 元数据。RuntimeExecutor、ProjectDocument/Schema1、旧算法端口与ROI模型未改，不需迁移。WPF/Core仅增加新类型展示、识别Overlay和ROI参数绑定，参数与工程变化仍由原Revision机制清旧。新增类型要求新版插件，不能声称旧可执行程序可加载OCR/YOLO节点。

独立发布桌面人工交互验证：{'2026-11-12已观察到脱离开发目录启动、运行OCR并显示真实文本/Success，程序正常退出。' if published else '待最终人工交互核验；当前已完成实际WPF自动验收及自包含Console运行。'}

没有真实串口、相机或工业产线验收；有限重复和短时预览不代表全天稳定性验证。
""")
median_ms = statistics.median(r["bridge_total_ms"] for r in external["runs"])
write("docs/第四阶段B性能与资源报告.md", f"""# 第四阶段 B 性能与资源报告

以下为当前Windows开发机实际测量，非硬实时或生产吞吐承诺。

| 项目 | 实测 |
|---|---|
| 8192×10000 Mono8构造与合成ROI图案 | {large['AllocationMilliseconds']:.3f} ms |
| 82MP图上640×160 OCR ROI | {large['OcrMilliseconds']:.3f} ms |
| 82MP图上640×200条码ROI | {large['BarcodeMilliseconds']:.3f} ms |
| 平台原图缓冲载荷 | {large['PlatformImageBytes']:,} bytes |
| 同次进程PrivateBytes变化 | {large['PrivateBytesDelta']:,} bytes |
| 结束后平台缓冲变化 | 0 bytes |
| 真实YOLO新进程3次中位（桥接总耗时） | {median_ms:.3f} ms |
| C#适配器单次本轮耗时 | {yolo['Real']['ExecutionTime']} |
| 原系统Tile | {yolo['Tiled']['TileCount']}片，{len(yolo['Tiled']['Detections'])}框 |

82MP测试只处理指定ROI，词框与码定位点检查为原图坐标；平台没有额外整图ImageFrame副本。PrivateBytes包括托管/引擎缓存且只是一个测量点，不能当作峰值总内存。OCR/条码会复制ROI到引擎临时缓冲；YOLO会保存本次ROI的临时PNG，单进程推理后删除，不复制输入到每个节点。

YOLO真实模型输出umbrella 0.8889719248、person 0.8655822277。C#总耗时包含临时PNG/新Python进程/推理；原forward毫秒值是内部指标，不能作为完整节点延时。新进程方案优先生命周期隔离，尚无常驻模型服务或GPU吞吐优化。Tile复用原系统，分裂框说明此配置不代表工业缺陷准确率。

原始结果见 [large-roi](../test-results/phase4b/acceptance/large-roi.json)、[C# YOLO](../test-results/phase4b/acceptance/yolo-real.json)、[外部真实YOLO](../test-results/phase4b-yolo-external.json)、[进程释放](../test-results/phase4b-yolo-process.json)。OCR/ZXing单次native/同步调用不支持立即抢占，只在前后/复制循环响应取消；预算限制ROI。没有全天测试、真实工业字符/污损码/线扫缺陷标注准确率测试。
""")
interfaces = read("docs/第四阶段B节点接口.json")
newids = {"OCR", "BarcodeReader", "QRCodeReader", "YoloInference"}
lines = ["# 第四阶段 B 节点与使用说明", "", "共54节点，新增4项。全部由原NodeRegistry发现、RuntimeExecutor执行；WPF只编排和呈现。", ""]
for n in interfaces:
    if n["TypeId"] not in newids: continue
    lines += ["## " + n["DisplayName"] + " / " + n["TypeId"], "", "| 方向 | 端口 | 类型 | 必需 |", "|---|---|---|---|"]
    for direction in ["Inputs", "Outputs"]:
        lines += [f"| {'输入' if direction == 'Inputs' else '输出'} | {p['Name']} | {p['Type']} | {p['Required']} |" for p in n[direction]]
    lines += ["", "默认参数：", "", "```json", json.dumps(n["Parameters"], ensure_ascii=False, indent=2), "```", ""]
lines += ["识别ROI优先级：现有ROI输入端口 → UseRoi参数 → 整张输入。ROI为原坐标像素，浮点边界向外光栅化；直接识别轴对齐ROI，旋转ROI先用原RotatedCrop，保留其CoordinateTransform映回原图。OCR/条码只收Mono8；其他四种格式先使用现有PixelFormatConvert显式转换。YOLO收Mono8/RGB8/BGR8，Bayer须先转换。", "", "绘制ROI后选择识别节点，点击绑定ROI写入正式参数；已有ROI连接需先断开。点击Result端口在执行结果页看结构化字段，图像自动查找上游原图并叠加当前结果。参数变化/换图/换工程清除旧结果。Text可接TextDisplay.String或已有通信字符串端口；Success可接IfElse.Condition，Confidence/Count可接Compare数值端口。", "", "细节：[OCR](Phase4B-OCR.md)、[条码](Phase4B-Barcodes.md)、[YOLO](Phase4B-YOLO.md)。机器可读完整54节点IO见第四阶段B节点接口.json。"]
write("docs/第四阶段B节点与使用说明.md", "\n".join(lines) + "\n")
write("docs/第四阶段B已知问题与范围.md", """# 第四阶段 B 已知问题与范围

- 已实现并实测：OCR/一维码/QR节点、原ROI适配、原坐标结果、真实引擎置信度（OCR）、结果面板/Overlay、四个示例、新旧工程运行、真实外部YOLO/Tile、取消和资源清理。
- OCR内置eng，优先数字/英文；中文模型、手写、复杂工业字体未验证。低对比度归一化可关闭，不保证所有噪声或遮挡识别。恒定图判空防止已复现的引擎幻觉。
- 条码首版每次0或1个符号，支持常见8种1D及QR；没有多码扫描。定位点为解码器实际格式相关点，不宣称完整边框或不存在的置信度。
- OCR与ZXing同步识别期间不能立即抢占取消；不宣称硬超时。明确像素预算/取消前后检查，YOLO进程支持可中断超时及Windows JobObject释放。Job用于生命周期管理，不是恶意代码安全沙箱。
- YOLO仅接入既有ONNX CPU入口，显式Python/原工程/模型/deployment.json。无自动下载、训练系统、GPU/PT替代或常驻服务。新进程约秒级开销，未做产线吞吐优化。
- YOLO发行示例标注外部依赖并默认明确失败，配置资源后才运行；本机模型/数据存在且已真测，但不复制原实现、环境、权重、图像到发行包。真实YOLO截图只留本机artifacts，不打包。
- 外部YOLOStudio未见项目根许可，Ultralytics为AGPL-3.0，测试COCO8附GPLv3许可；独立进程不构成闭源商业授权结论。使用/分发前需独立完成商业授权审核。原OpenCV native静态组成审查边界继承旧报告。
- 发布自包含.NET8 x64，不需.NET SDK；Tesseract需要已有Visual C++ 2019兼容x64运行库。本机具备并已启动，未做全新Windows虚拟机安装验证。
- 无真实工业字符/污损条码/缺陷标注准确率、全天稳定性、真实串口/工业相机测试。第四阶段C的相机接口、虚拟采集、RuntimeHost均未实现，本轮完成B后停止。
""")
write("docs/第四阶段C接口准备建议.md", """# 第四阶段 C 接口准备建议（尚未实施）

保持现有RuntimeExecutor、Schema1、ImageFrame共享所有权和ROI。下一批先设计厂商无关采集接口及能力查询，再用文件夹虚拟相机验证触发/连续采集/取消；SDK在独立适配层，避免泄露类型。RuntimeHost直接调用原Runtime和会话服务，先支持Loopback版本化TCP一种协议，加载、运行、状态、取消与结构化错误；不复制执行引擎。

本批新增识别契约可以直接用于Host输出；图像仍走拥有者/lease，禁止传裸Mat跨会话。OCR同步取消边界和YOLO进程生命周期需要进入Host任务政策。外部模型与许可配置应显式验证，不在加载工程时创建连接/下载/推理。先冻结公共接口及文件归属，再并行开发。本文仅为下一阶段准备，尚未开发C，需用户确认后开始。
""")
write("docs/第四阶段B验收报告.md", f"""# 第四阶段 B 验收报告

已完成本批代码与运行验收：OCR、BarcodeReader、QRCodeReader、YoloInference四节点；54个已实现节点、12个示例（11个离线、1个明确外部依赖YOLO）。保持原Runtime、Schema1、ROI、Nodify和浅蓝导航，不开发第四阶段C。

实际构建0警告0错误；{total}/{total}测试通过，A的479项全部保留。真实WPF/Console执行、保存重开、单节点、空白清旧、六个历史工程、82MP ROI与实际TCP继续通过；结束图像/缓存为0。真实外部YOLO检测两框，重复一致、6 Tile、类别筛选、异常与7项进程释放测试完成。默认发布YOLO示例缺依赖时明确失败，没有静态框或伪造结果。自包含发布Console已脱离SDK运行OCR，输出VISION 12345及真实0.9200000166893005置信度。

独立发布桌面：{'2026-11-12已在开发目录以外启动并交互执行OCR，观察到真实文本、0.9200000166893005置信度与Success；程序正常退出。' if published else '当前待最后独立启动交互检查；源WPF实际窗口自动验收已经通过。'}

交付：[节点输入输出与使用](第四阶段B节点与使用说明.md)、[自动化和兼容性](第四阶段B自动化与兼容性报告.md)、[性能与资源](第四阶段B性能与资源报告.md)、[依赖许可](第四阶段B第三方依赖与许可.md)、[已知范围](第四阶段B已知问题与范围.md)、[C准备建议](第四阶段C接口准备建议.md)。源码zip和Windows x64 zip在artifacts，启动VisionStudio.exe，首页→方案示例选择OCR/二维码/条码→一键运行。内置例自动创建副本，原模板只读。

完成并测试的是软件功能与既有系统推理接入；尚未完成真实生产识别精度、全天稳定性、YOLO商业授权或干净Windows虚拟机验收。中文OCR、多码读取、GPU常驻服务尚未实现；原串口仅模拟、无真实工业相机。所有边界详见已知问题。B完成后停止，不自动进入C。
""")
# Include exact package provenance, while retaining the previous inventory untouched.
cache = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages")); packages = {}
for f in root.rglob("packages.lock.json"):
    if any(p in {"bin", "obj", "artifacts"} for p in f.relative_to(root).parts): continue
    scope = "test" if f.relative_to(root).parts[0] == "tests" else "tool" if f.relative_to(root).parts[0] == "tools" else "runtime"
    for deps in json.loads(f.read_text(encoding="utf-8-sig"))["dependencies"].values():
        for name, item in deps.items():
            if item.get("type", "").lower() == "project": continue
            version = item["resolved"]; folder = cache / name.lower() / version
            entry = packages.setdefault((name, version), dict(id=name, version=version, scope=set()))
            entry["scope"].add(scope)
            nuspec = folder / (name.lower() + ".nuspec"); nupkg = folder / (name.lower() + "." + version + ".nupkg")
            if nuspec.exists():
                doc = ET.parse(nuspec); lic = next((e for e in doc.iter() if e.tag.split("}")[-1] == "license"), None)
                entry["license"] = lic.text if lic is not None else "See original package/phase A inventory"
            if nupkg.exists(): entry["nupkgSha256"] = hashlib.sha256(nupkg.read_bytes()).hexdigest()
for entry in packages.values(): entry["scope"] = sorted(entry["scope"])
assert len(packages) == 30
dump("docs/sbom-phase4b-nuget.json", dict(packages=sorted(packages.values(), key=lambda x: x["id"].lower()), runtimeNative=read("resources/tessdata/provenance.json")))
write("docs/第四阶段B第三方依赖与许可.md", """# 第四阶段 B 第三方依赖与许可

在A原27个锁定包基础上仅增加Tesseract 5.2.0、ZXing.Net 0.16.11及其System.Reflection.Emit 4.7.0，共30个含测试工具包。精确哈希/版本/范围见sbom-phase4b-nuget.json；原清单保留。

Tesseract包装器5.2.0为Apache-2.0，实际native Tesseract5.0.0 Apache-2.0、Leptonica1.82.0 BSD-2-Clause、内嵌InteropDotNet MIT。模型固定tessdata_fast提交87416418657359cb625c412a48b6e1d6d41c29bd，eng.traineddata 4,113,088字节，SHA256 7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2，Apache-2.0。锁定模型离线部署，运行时不下载。

实际Leptonica静态codec版本：giflib5.2.1、libjpeg6b/libjpeg-turbo2.1.4、libpng1.6.37、libtiff4.4.0、zlib1.2.13。11项许可原文/来源/哈希及JPEG声明已保留于licenses/phase4b和resources/tessdata/provenance.json，不能只列Tesseract顶层许可证。包装器与native配对x64发布；不复制x86，不替换原OpenCV native。

ZXing.Net0.16.11，Apache-2.0，源码tag v0.16.11.0 / fd58f3c4a5d80ccf45ef1237899a65b0ba5b83a0，无额外native或System.Drawing绑定。System.Reflection.Emit为MIT，原文与第三方声明随发布保留。自包含.NET8.0.18的许可证和第三方声明也保留。

既有CvInspect、Nodify、OpenCvSharp/OpenCV、Ports等沿用原锁定版本/许可；OpenCvSharpExtern.dll SHA256仍为1fa122bdb8e94175e7719fb8aa8f2ab211268a756f5d0c7a13c710ed79ae30cd。OpenCV现有Nonfree/FFMPEG静态组合待审边界未因此消除，不能宣称整个闭源商用发行已无条件清关。

外部YOLOStudio无根LICENSE；环境Ultralytics为AGPL-3.0，COCO8附GPLv3。适配器仅调用已有外部安装，不打包其代码/环境/权重/图片；进程隔离不等同于商业授权豁免。真实推理结果数值报告可复现，本地测试截图含外部图片不随包分发。商业交付前需审核原创实现、模型和完整发布组合。

官方来源：[Tesseract包装器](https://www.nuget.org/packages/Tesseract/5.2.0)、[ZXing.Net](https://www.nuget.org/packages/ZXing.Net/0.16.11)、[eng模型许可](https://github.com/tesseract-ocr/tessdata_fast/blob/87416418657359cb625c412a48b6e1d6d41c29bd/LICENSE)。详细各codec上游链接见provenance.json。
""")
# Deployment contents, including the shared self-contained Console entry point.
runtime = root / "artifacts/VisionStudio-Phase4B-win-x64"; assert (runtime / "VisionStudio.exe").exists() and (runtime / "VisionPlatform.Console.exe").exists() and (runtime / "coreclr.dll").exists()
assert not (runtime / "x86").exists() and not list(runtime.rglob("*ffmpeg*"))
assert not list(runtime.rglob("*.onnx")) and not list(runtime.rglob("*.pt"))
for folder, target in [("Examples", "Examples"), ("samples", "Samples"), ("third_party/licenses", "licenses"), ("licenses/phase4b", "licenses/phase4b")]: shutil.copytree(root / folder, runtime / target, dirs_exist_ok=True)
for vendor in ["CvInspect", "Nodify"]:
    for name in ["LICENSE", "PROVENANCE.md"]: shutil.copy2(root / "third_party" / vendor / name, runtime / "licenses" / (vendor + "-" + name))
shutil.copy2(root / "third_party/CvInspect/THIRD-PARTY-NOTICES.md", runtime / "licenses/CvInspect-THIRD-PARTY-NOTICES.md")
for name in ["LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"]: shutil.copy2(cache / "system.reflection.emit/4.7.0" / name, runtime / "licenses" / ("System.Reflection.Emit-" + name))
for framework in read("artifacts/VisionStudio-Phase4B-win-x64/VisionStudio.runtimeconfig.json")["runtimeOptions"]["includedFrameworks"]:
    for f in (cache / (framework["name"].lower() + ".runtime.win-x64") / framework["version"]).iterdir():
        if f.name.upper().startswith(("LICENSE", "THIRD-PARTY-NOTICES")): shutil.copy2(f, runtime / "licenses" / (framework["name"] + "-" + f.name))
shutil.copy2(root / "resources/tessdata/provenance.json", runtime / "tessdata/provenance.json")
shutil.copytree(images, runtime / "docs/images", dirs_exist_ok=True)
for f in (root / "docs").iterdir():
    if f.is_file() and (f.name.startswith(("第四阶段", "Phase4B", "第三方", "sbom")) or f.name == "native-build-information.txt"):
        shutil.copy2(f, runtime / "docs" / f.name)
shutil.copy2(root / "tools/tcp-loopback-peer.py", runtime / "tcp-loopback-peer.py")
write("artifacts/VisionStudio-Phase4B-win-x64/README.txt", "VisionStudio 第四阶段B / Windows x64\n启动VisionStudio.exe → 首页/方案示例 → OCR、二维码、一维条码 → 一键运行。内置例自动复制为用户工程。\n54节点，12示例：11离线+1需显式外部YOLO环境/模型。英文OCR模型已随包；不需要.NET SDK；native依赖兼容Visual C++ x64运行库。\nConsole入口同目录VisionPlatform.Console.exe run Examples/ocr/project.json；需要查看结果可加 --outputs 输出目录。\nYOLO默认不推理/下载，按docs/Phase4B-YOLO.md配置自有授权资源。原系统、权重和数据不随包分发。\n569测试通过；工业准确率、全天稳定性、真实相机/串口及YOLO商业授权尚未验收。完成B停止，未实施C。\n详细使用、许可证、测试、兼容性、性能、边界见docs和licenses。\n")
if "--reports-only" in sys.argv:
    print(json.dumps(dict(tests=total, publishedDesktopVerified=published, reportsGenerated=True))); raise SystemExit(0)
archives = []
for name, base, exclusions, prefix in [
    ("VisionPlatform-Phase4B-Source.zip", root, {".git", ".vs", "artifacts", "bin", "obj", "__pycache__"}, "VisionPlatform"),
    ("VisionStudio-Phase4B-win-x64.zip", runtime, set(), "VisionStudio")]:
    path = root / "artifacts" / name
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for f in sorted(base.rglob("*")):
            relative = f.relative_to(base)
            if f.is_file() and not any(p in exclusions for p in relative.parts): archive.write(f, prefix + "/" + relative.as_posix())
    with zipfile.ZipFile(path) as archive: assert archive.testzip() is None
    archives.append(dict(file=name, bytes=path.stat().st_size, sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
dump("artifacts/phase4b-delivery-manifest.json", dict(gitCommit=subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root).decode().strip(), testsPassed=total,
    publishedDesktopVerified=published, realYoloVerified=True, phaseCStarted=False, archives=archives))
print(json.dumps(dict(testsPassed=total, publishedDesktopVerified=published, archives=archives), ensure_ascii=False, indent=2))
