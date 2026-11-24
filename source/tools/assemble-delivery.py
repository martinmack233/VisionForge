"""Generate test evidence summary and local source/runtime delivery archives."""
from pathlib import Path
import collections, hashlib, json, shutil, xml.etree.ElementTree as ET, zipfile

root = Path(__file__).resolve().parent.parent
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
runs = []
for file in sorted((root / "test-results/release-validation").glob("*.trx")):
    document = ET.parse(file)
    counters = document.find("t:ResultSummary/t:Counters", ns).attrib
    results = document.findall("t:Results/t:UnitTestResult", ns)
    tests = document.findall("t:TestDefinitions/t:UnitTest/t:TestMethod", ns)
    suite = tests[0].attrib["className"].split(".Tests")[0] + ".Tests"
    runs.append(dict(suite=suite, file=file.relative_to(root).as_posix(), **{k: int(v) for k, v in counters.items()}))
assert len(runs) == 3 and sum(r["total"] for r in runs) == 114
assert all(r["failed"] == 0 and r["passed"] == r["total"] for r in runs)
console = json.loads((root / "test-results/console/summary.json").read_text(encoding="utf-8-sig"))
assert console["Passed"] and console["SeparateProcessMeasurementsEqual"]
observed_sources = json.loads((root / "tools/MatchingComparison/data/source-hashes.json").read_text())
assert all(hashlib.sha256(Path(p).read_bytes()).hexdigest() == value for p, value in observed_sources.items())
originals = json.loads((root / "third_party/CvInspect/upstream-sha256.json").read_text())
for item in originals:
    relative = Path(item["path"]).relative_to("src/CvInspect")
    if relative.name == "CvInspect.csproj":
        continue
    assert hashlib.sha256((root / "third_party/CvInspect" / relative).read_bytes()).hexdigest() == item["sha256"]
summary = dict(date="2026-10-30 Asia/Shanghai", passed=114, failed=0, skipped=0, suites=runs,
    separateProcessReloadEqual=True, consoleCommands=console["Commands"], originalSourceHashesUnchanged=True)
(root / "test-results/release-validation/summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
table = "\n".join(f"| {r['suite']} | {r['total']} | {r['passed']} | {r['failed']} |" for r in runs)
match = console["Measurement"]["Match"]
report = f"""# 第一阶段自动化测试报告

2026-10-30（Asia/Shanghai），Windows 11 x64，本机.NET8.0.18 / SDK9.0.302。最终使用OpenCvSharp managed与官方Slim native同为4.13.0.20260627。所有测试和Console命令均实际执行，数据为合成样本。

## 最终构建与测试

执行 `dotnet restore`、`dotnet build -c Release`、`dotnet test -c Release --logger trx --results-directory test-results/release-validation`，全部退出0。构建0警告、0错误。**114通过、0失败、0跳过**。

| 测试程序集 | 总数 | 通过 | 失败 |
|---|---:|---:|---:|
{table}
| 合计 | 114 | 114 | 0 |

日志和原始TRX在test-results/release-validation；summary.json由TRX计数生成。此前首次全量113项有1项失败，原日志保留test-results/final；修复过程见开发记录，没有删除失败测试。

## 覆盖

- 图像31项：五格式、Mono/RGB/BGR文件读写、BayerRG/GB物理通道及奇数偏移相位、RGB/BGR/灰度转换、stride/短缓冲、ROI越界、共享租约释放、8192×10000 Mono8 BMP实际创建/加载及零复制ROI操作。大图测试使用临时文件并清理，不把82MB数据固化进仓库。
- Runtime26项：正常/分支/多输入拓扑、类型和非法端口、环、重复接入、注册和发现、全图/指定节点/显式快照、取消与错误传播、可选输出缺失、输出所有权、参数快照、工程版本/缺插件/资源哈希、原子写入及失败保留、无关JSON保护。
- 算法及集成57项：匹配16项，Gaussian与ROI/Blob24项，真实流程集成9项，最终示例/大图处理/释放/非法匹配参数验收8项。覆盖正常/失败/旋转/缩放/限定ROI/低对比/噪声/重复匹配/模板保存、三格式高斯/非法kernel与sigma/border/独立ROI边界、真实旋转mask和坐标变换、连续滤波、同图分支共享、ROI真正影响Blob计数。82MP输入的32×32 ROI实际高斯及Blob测试验证只增加1024字节平台输出缓冲，而非复制整图。
- 20次完整流程每次Dispose后平台FrameHandle.ActiveBytes和ActiveAllocations都回到基线；缺模板失败流程同样回收。此断言只涵盖平台拥有缓冲，不代表native全分配零泄漏或长时间产线内存已经验收。
- 单独比较探针另有每算法13场景×7次测量+每场景2次预热，不混计到114项xUnit数量；全部输入和测量在tools/MatchingComparison。

## Console验收

tools/verify-console.ps1通过10个独立命令场景；日志在test-results/console。另用dotnet publish生成的Console独立目录运行保存工程成功，发布输出没有单独FFmpeg DLL。

| 检查 | 实测结果 |
|---|---|
| 流程A | 全节点成功，2 Blob，OK |
| 流程B | BGR→Mono→Gaussian→匹配→ROI→Blob→Decision，2 Blob，OK |
| 两次关闭进程后重载B | 分数、坐标、角度、尺度、ROI、全部Blob及判定逐值一致；耗时不作为一致性字段 |
| 换缩小旋转图片 | 分数0.965338、角-10.244°、尺度0.9，2 Blob，OK |
| 换3对象图片 | 真实计数3，NG，退出2 |
| 无目标图片 | Success=false，无Pose，下游Skipped，NG，退出2 |
| 单模板匹配/单高斯 | 同一Runtime ExecuteSingle执行成功 |
| 非法kernel=4 | 高斯节点Failed并给出KernelSize原因，进程退出2 |
| 运行到match | 只执行前置Image/Convert/Gaussian/Match |

重载流程B实际输出：Score={match['Score']:.9f}，中心({match['CenterX']:.6f},{match['CenterY']:.6f})，角度{match['Angle']:.6f}°，尺度{match['Scale']}。Blob质心(286,166)和(298,184)，滤波阈值后面积各61。节点耗时已记录；新进程包含JIT/初次native初始化，不能拿它直接替代预热基准或比较A/B算法性能。

## 可复现与限制

新目录解压源码后打开sln或按README运行restore/build/test/demo。依赖有packages.lock.json，联网首次恢复需要NuGet；已有缓存可离线使用。最终发行物native哈希见sbom。原Industrial matcher/models/pyproject与审核哈希一致，CvInspect纳入算法源码除已声明csproj外与原哈希一致。

未验证真实工业样本/实体相机/线扫连续采集/长期内存/客户干净机，也未实现正式UI、AI、TCP/C ABI或生产RuntimeHost。默认CvInspect存在离散尺度精度限制，Industrial源码授权缺口仍保留；native内部第三方发行审查未完全闭环。详见对应报告。
"""
(root / "docs/自动化测试报告.md").write_text(report, encoding="utf-8")

runtime = root / "artifacts/runtime"
shutil.copytree(root / "third_party/licenses", runtime / "licenses", dirs_exist_ok=True)
for name in ["LICENSE", "THIRD-PARTY-NOTICES.md", "PROVENANCE.md"]:
    shutil.copy2(root / "third_party/CvInspect" / name, runtime / "licenses" / ("CvInspect-" + name))
for name in ["第三方许可证清单.md", "sbom-nuget.json", "native-build-information.txt", "自动化测试报告.md"]:
    shutil.copy2(root / "docs" / name, runtime / name)
shutil.copy2(root / "README.md", runtime / "README.md")
shutil.copytree(root / "samples", runtime / "samples", dirs_exist_ok=True)
(runtime / "RunDemo.cmd").write_text('@echo off\ncd /d "%~dp0"\nVisionPlatform.Console.exe demo "samples\\DemoInspection"\npause\n', encoding="ascii")
assert not list(runtime.rglob("*ffmpeg*"))
archives = []
for name, base, exclude in [
    ("VisionPlatform-Phase1-Source.zip", root, {"bin", "obj", "artifacts", ".git", ".vs", "__pycache__"}),
    ("VisionPlatform-Phase1-Console-win-x64.zip", runtime, set()),
]:
    target = root / "artifacts" / name
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(base.rglob("*")):
            relative = path.relative_to(base)
            if path.is_file() and not any(part in exclude for part in relative.parts):
                archive.write(path, "VisionPlatform/" + relative.as_posix())
    with zipfile.ZipFile(target) as archive:
        assert archive.testzip() is None
    archives.append(dict(file=target.name, bytes=target.stat().st_size, sha256=hashlib.sha256(target.read_bytes()).hexdigest()))
(root / "artifacts/delivery-manifest.json").write_text(json.dumps(archives, indent=2), encoding="utf-8")
print(json.dumps(dict(validation=summary, archives=archives), ensure_ascii=False, indent=2))
