"""Validate actual regression/desktop evidence, then package Phase 3B. Standard library only."""
from pathlib import Path
import hashlib
import json
import os
import shutil
import xml.etree.ElementTree as ET
import zipfile

root = Path(__file__).resolve().parent.parent
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
final = root / "test-results/phase3b/final"
rows, names = [], set()
for file in sorted(final.glob("*.trx")):
    tree = ET.parse(file); count = tree.find("t:ResultSummary/t:Counters", ns).attrib
    names.update(r.attrib["testName"] for r in tree.findall("t:Results/t:UnitTestResult", ns))
    test_class = tree.find("t:TestDefinitions/t:UnitTest/t:TestMethod", ns).attrib["className"]
    rows.append(dict(assembly=test_class.split(".Tests")[0] + ".Tests", file=file.relative_to(root).as_posix(),
                     total=int(count["total"]), passed=int(count["passed"]), failed=int(count["failed"]), skipped=int(count["notExecuted"])))
assert len(rows) == 5 and all(r["total"] == r["passed"] and r["failed"] == r["skipped"] == 0 for r in rows)
previous = {r.attrib["testName"] for file in (root / "test-results/phase3a/final").glob("*.trx")
            for r in ET.parse(file).findall("t:Results/t:UnitTestResult", ns)}
assert len(previous) == 243 and previous <= names, "An original test was removed, renamed, or not rerun"
desktop = json.loads((root / "artifacts/phase3b-acceptance/summary.json").read_text(encoding="utf-8"))
assert all(desktop[k] for k in ["Passed", "IfElseTrueAndFalse", "MergeCurrentOnly", "SavedReopened", "ConsoleMatchesWpf", "PhaseAProjectsOpened", "ConnectionErrorReported"])
assert desktop["BuffersAfter"] == desktop["BuffersBefore"] == 0 and not desktop["PhysicalSerialTested"]
assert len(desktop["Tcp"]) == 3 and all(row["Passed"] and row["RepeatedRun"] == "DuplicateSuppressed" for row in desktop["Tcp"])
assert desktop["Tcp"][1]["Received"] == "OK" and desktop["Tcp"][2]["Received"] == "NG"
published = (root / "test-results/phase3b/ui/published-state.txt").read_text(encoding="utf-8")
assert "IfElse" in published and "Skipped" in published and "report.Result" in published
for vendor in ["Nodify", "CvInspect"]:
    for item in json.loads((root / "third_party" / vendor / "upstream-sha256.json").read_text(encoding="utf-8-sig")):
        name = item.get("Path", item.get("path")); expected = item.get("SHA256", item.get("sha256"))
        if name.endswith(".csproj"): continue
        target = root / "third_party" / name if vendor == "Nodify" else root / "third_party/CvInspect" / Path(name).relative_to("src/CvInspect")
        assert hashlib.sha256(target.read_bytes()).hexdigest().lower() == expected.lower()
total = sum(r["total"] for r in rows)
summary = dict(date="2026-11-02 Asia/Shanghai", passed=total, failed=0, skipped=0, originalPhaseATestsRerun=len(previous),
               suites=rows, actualDesktop=desktop, publishedDesktopVerified=True, physicalSerialTested=False)
(final / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
for log in ["restore.log", "build.log", "test.log", "publish.log", "desktop.log"]:
    shutil.copy2(final.parent / log, final / log)
raw = final.parent / "acceptance"; raw.mkdir(exist_ok=True)
for file in (root / "artifacts/phase3b-acceptance").glob("*"):
    if file.is_file() and file.suffix in {".json", ".log"}: shutil.copy2(file, raw / file.name)
images = root / "docs/images"; images.mkdir(exist_ok=True)
for name in ["flow1-true.png", "flow1-false.png", "tcp2.png", "tcp3.png", "tcp4.png", "connection-error.png"]:
    shutil.copy2(root / "artifacts/phase3b-acceptance" / name, images / ("VisionStudio-Phase3B-" + name))
table = "\n".join(f"| {r['assembly']} | {r['total']} | {r['passed']} | {r['failed']} |" for r in rows)
report = f"""# VisionStudio 第三阶段 B 验收报告

2026-11-02，Windows x64，.NET8。沿用现有解决方案、Runtime、Contracts、Nodify与ROI，未进入C。

## 审核与兼容扩展

开发前实际读取RuntimeExecutor/GraphValidator：原执行器为纯数据流串行DAG，没有控制流。因此先由主Agent审核《第三阶段B控制流与接口设计》，冻结公共接口及四角色文件边界，再并行实施。主负责公共契约/界面/项目会话和集成；四角色分别实现逻辑、控制流、TCP、串口/结构化结果。

PortDefinition追加Data/Control和Numeric约束，原构造和数据端口不变。虚拟$exec/$done与IfElse分支输出独立于数据端口；工程仍用Schema1四字段连线。无控制线的旧工程保持原行为。执行器在调用算法前门控，未选分支Skipped/InactiveBranch且空输出，正常失活不使整体失败；上游真实错误为UpstreamUnavailable，取消为Cancelled。Merge只消费本次唯一活动分支，活动数据失败不读旧缓存。单节点统一生成一次所需上游快照，避免分别重算分支。

RuntimeExecutionSession独立于WPF及通信实现。Standalone执行结束关闭服务，WPF按打开工程持有作用域，New/Open/Close释放。原RunResult继续只管理图像等节点输出资源。现有ROI、图像算法与第三方控件源码没有改写；原模板匹配/图像处理/ROI保存加载回归保留。

## 实际交付

新增21节点：Compare，LogicAnd/Or/Not，IfElse/Merge，TcpConnect/Send/Receive/Disconnect，TcpServerStart/Accept/Receive/Send/Stop，SerialOpen/Send/Receive/Close，ResultDisplay/ResultFormatter。总计36内置节点，通过原注册机制发现与自动参数面板配置。

Compare支持6运算和绝对容差，使用精确有理数保留整数及混合浮点精度，不用字符串比较。布尔运算消费已准备的强类型输入，不对上游副作用做隐式短路。TCP/Serial层只处理消息，JSON/CSV由纯ResultFormatter生成。结构化结果面板绑定本次InspectionReport，显示OK/NG、数量、可用分数/姿态/耗时/通信状态，缺数据保持null。Console同时识别结构化检测质量并导出SkipReason。

## 最终构建与回归

dotnet restore、dotnet build -c Release、dotnet test -c Release均实际退出0，构建0警告0错误。**{total}项通过、0失败、0跳过**，新增{total-243}项。原A阶段243项按测试名称逐项核对全部仍执行通过，没有删除或改写失败测试制造通过。

| 测试程序集 | 总数 | 通过 | 失败 |
|---|---:|---:|---:|
{table}

TRX/构建/发布/桌面日志及自动汇总位于test-results/phase3b/final，原始TCP收包证据另存acceptance。测试覆盖精确数值、布尔表、True/False/嵌套分支、未选副作用、Merge故障/取消/资源、会话关闭并发、两帧协议/三编码分片粘包、大小限制、发送尝试去重、服务生命周期、模拟串口及结构化格式化。串口均使用可替换内存传输，**没有真实COM硬件测试**。

TCP早期连接失败测试最初只期望SocketException，Windows也可能有界返回TimeoutException；改为验证明确有界连接失败，保留首次记录。最终审查补齐Stop/Dispose与晚到连接竞态、串口发送超时和尝试历史上限，没有忽略此类失败。

兼容检查中补回了原NodeExecutionContext三参数及RuntimeExecutor两参数的二进制构造签名。最初的可选参数重载产生编译歧义，已改为明确的三参数服务注入构造，并加入构造签名回归；相关首次构建日志保留，修正后完整测试通过。

## 四类真实验收

1. Blob→Compare→IfElse：真实合成图测得BlobCount2；2>1进入NG，OK支路Skipped。修改比较值5后进入OK，NG支路Skipped。Merge、保存重开、独立Console进程对照均通过。
2. 视觉结果TCP：独立Python进程通过本机真实socket收到JSON，result=OK、count=2；未提供的score/pose/timing为null，没有填造示例数值。
3. 条件通信：对端分别只收到一条OK或NG。未选发送节点Skipped。相同工程会话用相同DeliveryKey立即重复运行，返回DuplicateSuppressed，独立进程确认仍只有一条消息。
4. 异常：真实Loopback自动测试覆盖连接失败、监听端口占用、接收/写入超时、远端断开、主动取消，以及关闭会话中断Read/Accept并释放端口。桌面连接失败节点显示Failed与原因。Editor项目加载/参数编辑不启动监听，New/Close后端口可重新绑定。

tools/Phase3BAcceptance运行生产MainWindow和独立TCP进程，实际记录临时端口与节点耗时。随包手工例统一127.0.0.1:9000；先启动接收脚本再运行。旧A三个工程逐一打开运行成功。关窗后FrameHandle.ActiveBytes由0回到0。发布目录VisionStudio.exe经computer-use启动并运行控制流，真实可访问状态保留ui/published-state.txt。

![条件分支与结构化结果](images/VisionStudio-Phase3B-flow1-true.png)

![条件通信与重复抑制](images/VisionStudio-Phase3B-tcp3.png)

![连接失败反馈](images/VisionStudio-Phase3B-connection-error.png)

## 可靠性和验证边界

SendEnabled默认false；启用须DeliveryKey，先记录尝试再发送，失败/超时不自动重试。ForceResend是明确重发。同作用域去重不是跨工程/进程的exactly-once，生产需业务标识与对端确认。未选分支不执行通信，但独立无控制的连接根节点仍按DAG运行，分支专属连接也必须接门控。

TCP Server默认Loopback，无自动公网监听/防火墙修改，服务持续存在于显式作用域，不是永不结束的算法节点。基础明文协议未加入TLS/身份认证。数据大小、超时、有限审计和尝试历史均有边界；日志不记录正文。当前为本机TCP与短时资源测试，未证明所有局域网故障、生产节拍或24小时长稳。

串口测试只验证可替换传输与Runtime门控，未扫描/操作真实COM、未运行虚拟串口对。设备Open/Dispose最终依赖Windows驱动，虽移出UI线程，真实驱动阻塞与关闭时延仍须设备验收。具体边界见串口说明。

Windows x64发布为self-contained，含运行时/许可证/示例；源码ZIP含解决方案、锁文件、脚本与测试记录。唯一新增生产库System.IO.Ports8.0.0，实际还原来源/通知已记录；OpenCV原生商业发行核验限制继承原清单。图像为合成样本。本阶段B完成后停止，C仅交接口准备建议，未实施。
"""
(root / "docs/第三阶段B验收报告.md").write_text(report, encoding="utf-8")
runtime = root / "artifacts/VisionStudio-Phase3B-win-x64"
assert (runtime / "coreclr.dll").exists() and (runtime / "System.IO.Ports.dll").exists()
assert not list(runtime.rglob("*ffmpeg*")) and not list(runtime.rglob("*.so")) and not list(runtime.rglob("*.dylib"))
shutil.copytree(root / "third_party/licenses", runtime / "licenses", dirs_exist_ok=True)
cache = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
frameworks = json.loads((runtime / "VisionStudio.runtimeconfig.json").read_text())["runtimeOptions"]["includedFrameworks"]
for framework in frameworks:
    for file in (cache / (framework["name"].lower() + ".runtime.win-x64") / framework["version"]).iterdir():
        if file.name.upper().startswith(("LICENSE", "THIRD-PARTY-NOTICES")): shutil.copy2(file, runtime / "licenses" / (framework["name"] + "-" + file.name))
for vendor in ["CvInspect", "Nodify"]:
    for file in ["LICENSE", "PROVENANCE.md"]: shutil.copy2(root / "third_party" / vendor / file, runtime / "licenses" / (vendor + "-" + file))
shutil.copy2(root / "third_party/CvInspect/THIRD-PARTY-NOTICES.md", runtime / "licenses/CvInspect-THIRD-PARTY-NOTICES.md")
shutil.copytree(root / "samples/phase3b", runtime / "Samples/phase3b", dirs_exist_ok=True)
shutil.copytree(images, runtime / "images", dirs_exist_ok=True)
for name in ["第三阶段B使用与控制流说明.md", "第三阶段BTCP与帧协议说明.md", "第三阶段B串口与结果说明.md", "第三阶段B验收报告.md", "第三方许可证清单.md", "native-build-information.txt", "sbom-nuget.json"]:
    shutil.copy2(root / "docs" / name, runtime / name)
for name in ["tcp_fixture.py", "tcp-loopback-peer.py"]: shutil.copy2(root / "tools" / name, runtime / name)
(runtime / "README.txt").write_text("VisionStudio 第三阶段 B\n运行VisionStudio.exe，打开Samples/phase3b/flow1.json并运行，查看真实IF/ELSE与结果。\nTCP示例先启动Python接收端：python tcp-loopback-peer.py --port 9000 --count 1 --reply ACK\n再打开flow2.json或flow3-ok/ng.json；发送默认按DeliveryKey抑制重复。\n已包含.NET Desktop x64运行时；串口物理硬件尚未验收。B完成后停止，未进入C。\n", encoding="utf-8")
manifest = []
for name, base, excludes, prefix in [
    ("VisionPlatform-Phase3B-Source.zip", root, {"bin", "obj", "artifacts", ".git", ".vs", "__pycache__"}, "VisionPlatform"),
    ("VisionStudio-Phase3B-win-x64.zip", runtime, set(), "VisionStudio")]:
    path = root / "artifacts" / name
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for file in sorted(base.rglob("*")):
            relative = file.relative_to(base)
            if file.is_file() and not any(part in excludes for part in relative.parts): archive.write(file, prefix + "/" + relative.as_posix())
    with zipfile.ZipFile(path) as archive: assert archive.testzip() is None
    manifest.append(dict(file=name, bytes=path.stat().st_size, sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
(root / "artifacts/phase3b-delivery-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
print(json.dumps(dict(testsPassed=total, originalTestsRerun=len(previous), desktopPassed=True, archives=manifest), ensure_ascii=False, indent=2))
