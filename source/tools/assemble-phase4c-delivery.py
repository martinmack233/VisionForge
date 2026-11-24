"""Verify measured Phase 4C evidence, preserve old assets, and package source + self-contained Windows delivery."""
from pathlib import Path
import hashlib
import json
import os
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile

root=Path(__file__).resolve().parent.parent
def read(path):return json.loads((root/path).read_text(encoding="utf-8-sig"))
def write(path,value):(root/path).write_text(value,encoding="utf-8")
def dump(path,value):write(path,json.dumps(value,ensure_ascii=False,indent=2))
ns={"t":"http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
suites=[];names=set()
for f in sorted((root/"test-results/phase4c/final-validation").glob("*.trx")):
    t=ET.parse(f);c=t.find("t:ResultSummary/t:Counters",ns).attrib;m=t.find("t:TestDefinitions/t:UnitTest/t:TestMethod",ns).attrib
    suites.append(dict(assembly=m["className"].split(".Tests")[0]+".Tests",total=int(c["total"]),passed=int(c["passed"]),failed=int(c["failed"]),skipped=int(c["notExecuted"])))
    names.update(r.attrib["testName"] for r in t.findall("t:Results/t:UnitTestResult",ns))
old={r.attrib["testName"] for f in (root/"test-results/phase4b/final-validation").glob("*.trx") for r in ET.parse(f).findall("t:Results/t:UnitTestResult",ns)}
assert len(old)==569 and old<=names
assert len(suites)==7 and all(s["total"]==s["passed"] and s["failed"]==s["skipped"]==0 for s in suites)
total=sum(s["passed"] for s in suites);assert total==632
d=read("artifacts/phase4c-acceptance/summary.json");large=read("artifacts/phase4c-acceptance/large-camera.json")
assert d["Passed"] and d["Examples"]==15 and d["RegisteredNodes"]==55 and len(d["OldProjects"])==6
assert all(d[k] for k in ["DesktopCamera","CameraNextFrameSemantics","FailureClearsRoi","SaveReload","ProjectSwitchStopsCapture","BuiltInExamplesUnchanged","ConsoleWpfHostSameRuntime"])
assert d["BuffersBefore"]==d["BuffersAfter"]==d["CacheAfterClose"]==0 and not d["Phase5Started"]
assert d["Host"]["Passed"] and d["Host"]["ActualPlugin"] and d["Host"]["RealExternalYolo"] and large["TrueOneToOne"]
p=read("test-results/phase4c/protected-source-check.json");assert p["checked"]==140 and p["unchanged"]==134 and not p["unexpected"]
external=read("test-results/phase4c/host-external/summary.json");assert external["Passed"] and external["IndependentOnce"] and external["RealTcpPythonClient"] and external["ActualTrustedDll"]
baseline="visionstudio-phase4b";old_assets=0
for path in subprocess.check_output(["git","ls-tree","-r","--name-only",baseline],cwd=root,text=True,encoding="utf-8").splitlines():
    if path.startswith(("Examples/","samples/")):
        assert (root/path).read_bytes()==subprocess.check_output(["git","show",baseline+":"+path],cwd=root),path
        old_assets+=1
assert old_assets==150
for vendor in ["CvInspect","Nodify"]:
    for item in read("third_party/"+vendor+"/upstream-sha256.json"):
        name=item.get("Path",item.get("path"));expected=item.get("SHA256",item.get("sha256"))
        if name.endswith(".csproj"):continue
        f=root/"third_party"/name if vendor=="Nodify" else root/"third_party/CvInspect"/Path(name).relative_to("src/CvInspect")
        assert hashlib.sha256(f.read_bytes()).hexdigest().lower()==expected.lower(),str(f)
evidence=root/"test-results/phase4c/acceptance";evidence.mkdir(parents=True,exist_ok=True)
for name in ["summary.json","large-camera.json","host-flows.json","host-camera-sequence.json","host-yolo-real.json"]:shutil.copy2(root/"artifacts/phase4c-acceptance"/name,evidence/name)
for folder in (root/"artifacts/phase4c-acceptance/console").iterdir():
    target=evidence/"console"/folder.name;target.mkdir(parents=True,exist_ok=True)
    for name in ["results.json","console.log","comparison.json"]:shutil.copy2(folder/name,target/name)
images=root/"docs/images";images.mkdir(exist_ok=True)
for f in (root/"artifacts/phase4c-acceptance").glob("*.png"):shutil.copy2(f,images/("VisionStudio-Phase4C-"+f.name))
ui=root/"test-results/phase4c/ui/published-state.txt"
published=ui.exists() and all(k in ui.read_text(encoding="utf-8") for k in ["VirtualCamera","Success","Sequence = 1","02-shifted.png"])
dump("test-results/phase4c/final-validation/summary.json",dict(total=total,passed=total,failed=0,skipped=0,previous569Preserved=True,suites=suites,protected=p,oldExampleAssetsUnchanged=old_assets,desktopAndHost=d,publishedDesktopVerified=published,physicalCameraTested=False,physicalSerialTested=False,allDayStabilityTested=False,phase5Started=False))
table="\n".join(f"| {s['assembly']} | {s['total']} | {s['passed']} | 0 |" for s in suites)
write("docs/第四阶段C自动化与兼容性报告.md",f"""# 第四阶段 C 自动化与兼容性报告

2026-11-12。实际执行dotnet restore、dotnet build -c Release、dotnet test -c Release，构建0警告0错误。

| 程序集 | 总数 | 通过 | 失败 |
|---|---:|---:|---:|
{table}
| 合计 | {total} | {total} | 0 |

0跳过；B的569项按测试名全部保留并再次通过，新增63项。旧测试源未删除或修改。TRX和汇总见[test-results/phase4c/final-validation](../test-results/phase4c/final-validation)；restore/build/test/publish日志与实际Host/客户端输出均在test-results/phase4c。

主集成程序通过15个示例完整性检查，14个可离线执行图及变体经过同一Runtime；YOLO依赖示例保留显式配置，开发机另以真实外部模型由Host执行。真实WPF设备页验证枚举不采图、打开、共享当前Runtime相机、预览第2帧、运行捕获第3失配帧、循环第4帧恢复、连续预览背压、停采、正式参数保存重开、工程切换清帧。原图缓冲和两视口显示缓存关闭后归零。

实际Host Loopback测试包含六种视觉工程、跨run相机序号1/2/3、同run请求重放、旧RunId拒绝、第三帧ROI/Blob Skipped、真实YOLO与可信DLL回显。另19项Hosting测试覆盖分片/粘包、UTF8/JSON/版本错误、超限、busy、取消/超时、断线、会话隔离、清理异常、实际Stop与资源释放。独立exe --once、Python真实TCP客户端及独立DLL加载也完成；外部监听测试只在客户端会话关闭后中断进程，不把进程中断冒充优雅Stop。

WPF重新打开运行的六个旧工程：{', '.join(d['OldProjects'])}。旧A/B示例、ROI/模板跟随、BlobFilter、RotatedCrop、IfElse/Merge、TCP异常与串口模拟继续回归。Schema保持1，不需要迁移。

基线visionstudio-phase4b：140个旧核心/算法/插件/ROI/EditorCore/测试C#文件中134个完全一致，6个EditorCore兼容增量见protected-source-check.json。RuntimeExecutor、ProjectStore、旧Contracts、五格式/缓冲和旧节点端口语义未改。150个旧示例/sample资源逐字节不变，CvInspect/Nodify上游库存哈希一致。修复的打开行为是：ImageInput.Image已有相机连接时跳过无用fallback文件预览；新增测试证明旧文件不存在仍能打开并通过原Runtime执行。

独立发布WPF：{'已在开发目录外启动并交互运行VirtualCamera流程，观察到真实Success与Sequence=1，设备预览取得第2帧，摘要保持单行，程序正常退出。' if published else '等待最终独立交互核验；源WPF窗口自动验收与自包含Host运行已通过。'}

没有真实相机/串口/生产局域网或全天稳定性验收。文件序列和短时循环结果不表示硬件吞吐、外触发或生产精度。
""")
write("docs/第四阶段C性能与资源报告.md",f"""# 第四阶段 C 性能与资源报告

本机实际文件相机测量，不能代替工业相机性能测试。

| 项目 | 实测 |
|---|---:|
| 8192×10000 Mono8目录打开 | {large['OpenMilliseconds']:.3f} ms |
| 82MP BMP采集/解码 | {large['CaptureMilliseconds']:.3f} ms |
| 真实L0/1:1视口等待 | {large['NativePreviewMilliseconds']:.3f} ms |
| 同次单个UI心跳排队 | {large['UiHeartbeatMilliseconds']:.3f} ms |
| 平台图像载荷 | {large['PlatformFrameBytes']:,} bytes |
| 当次视口像素缓存 | {large['CacheBytes']:,} bytes |
| 每视口缓存上限 | {large['CacheLimit']:,} bytes |
| 最终FrameHandle/显示缓存 | 0 / 0 |

UI心跳只是一次排队观测，不是最大卡顿测量；FrameHandle字节和显示缓存不是进程总内存或峰值。原图整帧驻留；文件解码有native临时缓冲。每视口64MiB，编辑器与设备页分别计，合计最多128MiB；算法结果另有原512MiB默认保留预算，虚拟帧另受100MP预算约束。

连续采集为单消费者pull，无待显示帧队列；显示慢时背压降低实际速度。FrameRate为连续采集上限，非硬实时保证；单帧触发立即取下一帧。24项相机测试含96次采集、12次打开关闭和五种像素格式。主WPF连续预览与工程切换验证了旧帧不再发布。旧高分辨率Tile和图像算法测试继续通过。

Host按连接持有执行服务，结果只保留有界投影；RunResult原图执行完成后释放，设备不持有消费者的图像引用。断线/close/Stop取消并等待运行，然后释放会话。OCR/ZXing/文件解码的同步native边界只能调用前后取消，未声称可硬抢占。

原始数据见[large-camera.json](../test-results/phase4c/acceptance/large-camera.json)、[Host流程](../test-results/phase4c/acceptance/host-flows.json)及[集成汇总](../test-results/phase4c/acceptance/summary.json)。未做全天、真实线扫SDK、持续存盘、实际多相机/GPU/生产网络压力测试。
""")
write("docs/第四阶段C相机接口说明.md","""# 第四阶段 C 相机接口说明

相机公共类型位于Contracts.Acquisition，ICameraProvider负责ProviderId、EnumerateAsync与Create(deviceId)；ICameraDevice负责Info/State/Capabilities/LastFrame/LastError、OpenAsync/CloseAsync/CaptureSingleAsync/StartAcquisitionAsync/ReadFramesAsync/StopAcquisitionAsync、ReadParametersAsync/SetParameterAsync及IAsyncDisposable。

CaptureSingleAsync和连续枚举每项转移一个ImageFrame引用，消费者负责Dispose；设备不释放其他持有者引用。ImageFrame五格式和Stride/CoordinateSpace保持原契约，显示使用自己的Share。连续枚举只允许一个消费者；停止取消等待，调用方仍需结束/释放自己的枚举和帧。

CameraParameterCapability包含名称、显示名、类型、可读/可写、范围、枚举集合、RequiresStopped。曝光/增益只有硬件Provider真实支持时才可暴露为可写；文件相机没有这些参数。CameraFrameInfo输出真实帧ID、虚拟序号、时间戳、尺寸、Stride、格式和源文件名；虚拟时间戳不是硬件同步时间。

Acquisition库仅依赖Contracts+Imaging；CameraProviderRegistry.Register/Discover可加入独立适配程序集，CameraSession.GetDeviceAsync以ProviderId/DeviceId和CameraId取得设备。自研SDK应在独立适配项目中实现接口、查询能力、管理SDK缓冲到ImageFrame的生命周期，不能把SDK对象、Mat或裸指针加入公共契约。现有Runtime通过IExecutionServices持有CameraSession，不修改执行器。实际代码与测试说明见[Phase4C-Acquisition.md](Phase4C-Acquisition.md)。

本批只有文件夹虚拟Provider和测试替身；真实厂商/自研SDK、曝光增益、硬件触发、线扫拼接尚未接入或验收。
""")
write("docs/第四阶段C虚拟相机与设备使用.md","""# 第四阶段 C 虚拟相机与设备使用

设备管理→选择文件夹→指定像素解释→打开→单帧预览或开始连续预览。能力参数修改作用于当前设备，需显式写入节点后才能随正式工程保存；否则Runtime仍以原工程参数配置设备，必要时重开并从首帧开始。目录按Ordinal文件名排序、非递归；坏文件/16bit/Alpha明确失败，不跳过后伪称成功。Bayer必须显式选对应排列，显示转换不覆盖原始数据。自动解释只用于普通灰度/彩色文件。

打开后连接设置锁定，可通过能力参数面板在停采状态修改支持项；无曝光/增益写能力。连续预览背压、只保留当前帧；停止清预览但设备保持打开，关闭释放设备。工程切换/窗口关闭取消旧循环、清帧并释放会话，旧dispatcher结果不能再次发布。

点击“写入/选择相机节点”将CameraId/FolderPath/FrameRate/Loop/PixelFormat写入正式VirtualCamera节点参数；存在ImageInput且其Image端口未连接时自动连接VirtualCamera.Image。也可手动接到模板匹配等节点。已有数据连接不会被覆盖。保存/另存路径使用原ProjectStore，重新打开从第一帧开始，不把运行索引存进配方。

“预览”和“运行”有明确区别：预览不执行算法；运行先停止预览，原Runtime的VirtualCamera节点触发下一新帧，不能把旧预览帧视为新检测。VirtualCamera无必需输入，输出Image和FrameInfo；图中后续节点共享只读输入。若切回文件输入，应显式断开相机到ImageInput.Image的连接；原文件fallback语义保留。

Examples/virtual-camera示例包含两个不同位置/角度目标和一张缺失图。新会话第1帧定位+10度并检出2个Blob，第2帧平移/-10度，第3帧匹配失败、ROI/Blob Skipped；循环第4帧恢复。实际结果由算法计算。全部资源为合成图，未代表物理工业精度。

详细页面与测试见[Phase4C-DevicesUI.md](Phase4C-DevicesUI.md)。矩形/旋转ROI仍在原算法编辑器中绘制、绑定、保存和跟随，不创建新的ROI管理器。
""")
write("docs/第四阶段CRuntimeHost使用说明.md",r"""# 第四阶段 C RuntimeHost 使用说明

发布目录包含VisionStudio.exe、VisionPlatform.Console.exe、VisionPlatform.RuntimeHost.exe，均使用同一Runtime/节点。Host为独立Console程序，无需启动WPF或安装.NET SDK。

一次运行并输出结构化结果：

```powershell
.\VisionPlatform.RuntimeHost.exe --project .\Examples\runtime-host\project.json --once
.\VisionPlatform.RuntimeHost.exe --project .\Examples\virtual-camera\project.json --once --target decision
```

显式启动本地监听，再运行标准库Python客户端（另一个终端）：

```powershell
.\VisionPlatform.RuntimeHost.exe --address 127.0.0.1 --port 8766
python -X utf8 tools/runtimehost_client.py --host 127.0.0.1 --port 8766 --project C:/absolute/path/Examples/host-tcp/project.json
```

端口0可由系统分配，首行JSON返回实际端口。客户端依次load/run/status/result/close，输出真实节点状态/耗时/OK-NG。启动监听和load均不执行图、不下载模型、不自动发检测消息。只有明确run执行算法及工程中已配置的副作用节点。相机服务在连接/工程会话内跨run保留序号；close/断线释放，重新load从首帧开始。

插件仅启动时显式`--plugin C:/absolute/trusted.dll`，通过原NodeRegistry发现并校验PluginVersion；不是安全沙箱或热加载。测试源码tools/HostingFixturePlugin展示一个明确的回显节点，不作为视觉算法结果。生产插件与相机SDK适配器授权须独立核验。

默认仅Loopback。非Loopback需显式`--allow-lan`和`--address`；本版没有认证/TLS，不适用于公网。默认服务读30秒、写5秒超时；客户端默认10秒IO，run默认60秒合作取消，长native调用不保证立即结束。Ctrl+C通知服务取消、等待运行并释放资源；强制结束进程不能代替优雅停止验证。

结果图像仅元数据、不传像素/指针；已知数据按类型有界投影，Blob不传PixelRuns，未知插件类型标Unsupported，超限明确RESULT_TOO_LARGE。完整协议、错误码、字段、限制见[Phase4C-HostProtocol.md](Phase4C-HostProtocol.md)，C#客户端见Hosting/RuntimeHostClient.cs，Python不依赖第三方包。C ABI/完整SDK尚未开发。
""")
write("docs/第四阶段C已知问题与范围.md","""# 第四阶段 C 已知问题与范围

- 已实现并实测：通用相机契约/能力参数/ProviderRegistry、文件相机单帧和连续采集、设备页、正式相机节点、原Runtime检测、独立Host、版本化TCP、可信DLL加载、取消/关闭释放与三个完整C示例。
- 只有虚拟文件相机，没有真实厂商或自研SDK适配、曝光/增益、硬件触发、相机断线重连、线扫条带拼接、多相机同步/标定。通用接口与适配注册已具备，不能称硬件验收完成。
- 连续为pull+显示背压，帧率为上限；文件解码仅前后取消，100MP预算在解码后检查，不是恶意文件内存沙箱。原图整帧驻留，未实现采集零拷贝或生产吞吐优化。
- Host是可信本机/显式LAN服务，无认证/TLS/公网加固；插件必须可信，加载不是沙箱。仅启动时加载托管节点插件，无热卸载。图像响应只元数据，尚无远程图像传输、C ABI或完整SDK。
- 请求重放窗口为最近128个成功变更请求，不提供跨重启/无限历史的Exactly Once保证。每次新操作应使用新RequestId，客户端不自动重试。合作取消无法硬抢占OCR/ZXing/第三方同步native节点；close会等待安全退出。
- 两个视口各自64MiB，合计128MiB；算法结果另有预算，不是总进程RAM硬上限。82MP与有限循环测试不是全天、实际相机带宽或工业稳定性认证。
- 原YOLO需显式外部环境/授权模型，原代码/权重/测试图片不打包；真实Host推理已测，AGPL/模型商业授权审查边界仍存在。原OpenCV native许可审查、VC x64运行库前提继承B，不声称整个闭源发行已无条件合规。
- 未做干净Windows VM、真实串口、真实相机、生产局域网或全天稳定性验证。本机自包含程序与真实Loopback已验证。相机配置可保存，运行索引/实时状态不保存。
- 第四阶段完成后停止；第五阶段只提供建议，未实施。
""")
write("docs/第四阶段C验收报告.md",f"""# 第四阶段 C 验收报告

2026-11-12。本批已完成软件实现和本机运行验收：新增Acquisition、Hosting和独立RuntimeHost；设备页可枚举、打开、单帧/连续预览、停止、关闭、按能力编辑参数并写入正式工程。新增VirtualCamera节点，经原Runtime串联模板匹配/ROI/Blob/OK-NG。共55节点、15示例，Schema1和旧ROI保持。

实际build 0警告0错误；{total}/{total}测试通过，B的569项全部保留。真实WPF/Console/Host一致性、工程保存重开、下一帧语义、第三帧失配清旧、82MP真1:1、六个旧工程和完整旧示例回归通过。真实Host/TCP/外部YOLO/可信DLL调用完成；关闭后FrameHandle/显示缓存均0。

独立发布桌面：{'已在开发目录外交互运行相机流程，观察到真实Success和首帧Sequence=1、设备预览第2帧，程序正常退出。' if published else '待最终开发目录外桌面交互；源WPF自动验收与自包含Host已通过。'}

交付含完整源码、Windows x64三程序、15个完整示例与自制图像、相机/虚拟设备/Host/协议/客户端文档、TRX/性能/兼容性/已知范围、许可证。数据与报告链接：[自动化兼容性](第四阶段C自动化与兼容性报告.md)、[性能资源](第四阶段C性能与资源报告.md)、[相机接口](第四阶段C相机接口说明.md)、[设备使用](第四阶段C虚拟相机与设备使用.md)、[Host使用](第四阶段CRuntimeHost使用说明.md)、[协议](Phase4C-HostProtocol.md)、[已知问题](第四阶段C已知问题与范围.md)。

软件文件采集已验收；真实工业硬件、物理精度、生产网络和全天稳定性未验收。真实SDK适配、C ABI、远程图像传输等未实现，不计为完成。C已完成后停止，不自动进入第五阶段。
""")
write("docs/第四阶段总验收报告.md",f"""# 第四阶段总验收报告

截至2026-11-12，A/B/C软件批次已经形成独立可运行交付，继续使用原Runtime、Schema1、ROI、Nodify和浅蓝主题。

| 批次 | 已实现 | 验证记录 |
|---|---|---|
| A | 导航七页、8个方案、Caliper/圆边缘/线圆拟合/距离/角度共6节点 | 479测试，原428项保留 |
| B | OCR、1D、QR、原YOLO外进程/Tile接入；增加4例 | 569测试、真实引擎/外部模型、独立桌面 |
| C | 相机抽象/虚拟文件相机/设备页、VirtualCamera节点、RuntimeHost/单TCP协议/可信插件、增加3例 | {total}测试，原569保留，真实WPF/Console/Host、82MP、TCP |

累计55个节点、15个完整工程示例。14个可离线运行图，YOLO例显式外部依赖；Host独立/通信模式需用户明确启动。OCR eng已离线随包。所有检测分数/位置/框、采集序号与耗时由实际执行得到；图像均为自制合成例，真实YOLO测试使用外部自有安装但不分发其资源。

相机/Host与UI分离；没有重建Runtime或替换旧算法。RuntimeExecutor/ProjectStore/旧Contracts/ROI源保持，只有新增契约/模块与EditorCore兼容增量。旧150个示例/sample资源逐字节保留，全部旧测试继续通过。最终build0警告0错误，0跳过；指标、原始TRX、截图和复现工具见各批次报告。

本次修复：相机已连接ImageInput时，WPF不再读取无用旧文件fallback；显示背压可取消；坏UTF8有结构化错误；采集元数据使用单行摘要，完整字段保留在结构化面板；客户端超时中断后关闭失同步连接；清理异常不阻止其余资源释放。没有删除失败测试或改正确答案。

已测试的软件能力不等于产线认证：真实相机SDK、曝光/触发/物理串口、全天稳定性、实际缺陷准确率未验收；YOLO及原OpenCV native商业授权审查边界仍须独立处理。C ABI、完整SDK、常驻GPU服务和远程图像传输未实现。详细边界以[第四阶段C已知问题与范围](第四阶段C已知问题与范围.md)及各B许可说明为准。

交付入口：解压VisionStudio-Phase4C-win-x64.zip，启动VisionStudio.exe；独立VisionPlatform.RuntimeHost.exe与VisionPlatform.Console.exe位于同目录。源码解决方案VisionPlatform.sln；命令/协议/示例均随包。第四阶段完成后停止，第五阶段仅有建议。
""")
write("docs/第五阶段开发建议.md","""# 第五阶段开发建议（尚未实施）

先用目标相机与真实工业样本定义验收预算，再扩展产品化能力：

1. 在独立适配项目接入一款确实有SDK/硬件的相机，按能力查询实现曝光/增益/触发/断线；验证SDK缓冲到ImageFrame所有权，保持Contracts无SDK类型。
2. 建立真实产线/线扫图像和标注集，区分像素拟合/标定物理精度，测量曝光变化、模糊、污损、遮挡与误检漏检，避免以合成例替代精度验收。
3. 在当前Host协议上做经过测试的版本演进、访问控制/TLS需求与发布/更新策略；先稳定一种外部接口，再评估C#SDK/C ABI，不复制Runtime。
4. 对采集、UI缓存、算法临时内存与外部YOLO进程分别进行长时/峰值/取消压力验证；若吞吐不达标，再评估零拷贝、Tile采集或独立常驻推理服务，保留当前可替换接口。
5. 完成YOLO/模型、OpenCV native组件与商业分发组合的授权审查，整理干净Windows VM安装验证、日志保留/诊断、插件版本和工程迁移政策。

以上仅为后续建议，需新任务授权后开始，当前未实施第五阶段。
""")
interfaces=read("docs/第四阶段C节点接口.json")
lines=["# 第四阶段完整节点清单","","共55个实际注册节点；不列未实现规划。详细参数与机器可读IO见第四阶段C节点接口.json。","","| TypeId | 中文名称 | 分类 | 输入 | 输出 |","|---|---|---|---|---|"]
for n in interfaces:lines.append(f"| {n['TypeId']} | {n['DisplayName']} | {n['Category']} | "+", ".join(p["Name"]+":"+p["Type"]+("?" if not p["Required"] else "") for p in n["Inputs"])+" | "+", ".join(p["Name"]+":"+p["Type"] for p in n["Outputs"])+" |")
write("docs/第四阶段完整节点清单.md","\n".join(lines)+"\n")
# No new third-party package versions are introduced in C.
cache=Path(os.environ.get("NUGET_PACKAGES",Path.home()/".nuget/packages"));pkgs=set()
for f in root.rglob("packages.lock.json"):
    if any(x in {"bin","obj","artifacts"} for x in f.relative_to(root).parts):continue
    for deps in json.loads(f.read_text(encoding="utf-8-sig"))["dependencies"].values():
        for k,v in deps.items():
            if v.get("type","").lower()!="project":pkgs.add((k,v["resolved"]))
before={(x["id"],x["version"]) for x in read("docs/sbom-phase4b-nuget.json")["packages"]};assert pkgs==before and len(pkgs)==30
write("docs/第四阶段C第三方依赖说明.md","# 第四阶段 C 第三方依赖说明\n\n本批新增相机/Host/协议/客户端均为本平台代码，未增加第三方包或厂商SDK。含测试工具共30个锁定NuGet包，集合和版本与B完全相同；详见sbom-phase4b-nuget.json及第四阶段B第三方依赖与许可.md。CvInspect/Nodify上游源哈希保持，OpenCV native与Tesseract/Leptonica/eng模型继续原配套。随包保留原许可/NOTICE及自包含.NET运行库声明。\n\n原YOLO及模型资源不打包；原授权边界、OpenCV native审查与VC x64运行库前提不因C新增模块消失，不宣称无条件闭源商用许可清关。未接入任何厂商SDK，没有厂商许可已审核的声明。\n")
runtime=root/"artifacts/VisionStudio-Phase4C-win-x64"
assert all((runtime/n).exists() for n in ["VisionStudio.exe","VisionPlatform.Console.exe","VisionPlatform.RuntimeHost.exe","coreclr.dll","tessdata/eng.traineddata","tools/yolo_bridge/yolo_bridge.py"])
assert not (runtime/"x86").exists() and not list(runtime.rglob("*ffmpeg*")) and not list(runtime.rglob("*.onnx")) and not list(runtime.rglob("*.pt"))
native=list(runtime.rglob("OpenCvSharpExtern.dll"));assert len(native)==1 and hashlib.sha256(native[0].read_bytes()).hexdigest()=="1fa122bdb8e94175e7719fb8aa8f2ab211268a756f5d0c7a13c710ed79ae30cd"
for folder,target in [("Examples","Examples"),("samples","Samples"),("third_party/licenses","licenses"),("licenses/phase4b","licenses/phase4b")]:shutil.copytree(root/folder,runtime/target,dirs_exist_ok=True)
for vendor in ["CvInspect","Nodify"]:
    for name in ["LICENSE","PROVENANCE.md"]:shutil.copy2(root/"third_party"/vendor/name,runtime/"licenses"/(vendor+"-"+name))
shutil.copy2(root/"third_party/CvInspect/THIRD-PARTY-NOTICES.md",runtime/"licenses/CvInspect-THIRD-PARTY-NOTICES.md")
for name in ["LICENSE.TXT","THIRD-PARTY-NOTICES.TXT"]:shutil.copy2(cache/"system.reflection.emit/4.7.0"/name,runtime/"licenses"/("System.Reflection.Emit-"+name))
for framework in read("artifacts/VisionStudio-Phase4C-win-x64/VisionStudio.runtimeconfig.json")["runtimeOptions"]["includedFrameworks"]:
    for f in (cache/(framework["name"].lower()+".runtime.win-x64")/framework["version"]).iterdir():
        if f.name.upper().startswith(("LICENSE","THIRD-PARTY-NOTICES")):shutil.copy2(f,runtime/"licenses"/(framework["name"]+"-"+f.name))
shutil.copy2(root/"resources/tessdata/provenance.json",runtime/"tessdata/provenance.json")
shutil.copytree(images,runtime/"docs/images",dirs_exist_ok=True)
for f in (root/"docs").iterdir():
    if f.is_file() and (f.name.startswith(("第四阶段","第五阶段","Phase4","第三方","sbom")) or f.name=="native-build-information.txt"):shutil.copy2(f,runtime/"docs"/f.name)
(runtime/"tools").mkdir(exist_ok=True)
for name in ["runtimehost_client.py","tcp-loopback-peer.py"]:shutil.copy2(root/"tools"/name,runtime/"tools"/name)
write("artifacts/VisionStudio-Phase4C-win-x64/README.txt","VisionStudio 第四阶段C / Windows x64\n启动VisionStudio.exe，首页/方案示例选择虚拟相机定位检测；设备页可打开、单帧/连续预览和写入正式相机节点。预览不执行算法，运行触发下一帧。\n55节点，15示例；632项测试全通过，旧569保留。新增独立VisionPlatform.RuntimeHost.exe与原Console位于同目录。\n独立运行：VisionPlatform.RuntimeHost.exe --project Examples/runtime-host/project.json --once\n本地服务：VisionPlatform.RuntimeHost.exe --port 8766\n客户端：python -X utf8 tools/runtimehost_client.py --port 8766 --project 工程绝对路径\n自包含.NET8 x64，无需SDK；native需要兼容Visual C++ x64运行库。OCR英文模型随包；YOLO需原外部环境/授权模型，不打包原代码/权重/数据。\n虚拟文件相机和软件接口已验收；真实相机SDK、硬件曝光触发、物理串口、全天稳定性和生产精度未验收。Host默认Loopback；LAN需显式配置，不是公网认证服务。\n使用/协议/许可证/测试/兼容性/边界见docs与licenses。第四阶段完成后停止，第五阶段未实施。\n")
if "--reports-only" in sys.argv:
    print(json.dumps(dict(testsPassed=total,publishedDesktopVerified=published,reportsGenerated=True)));raise SystemExit(0)
archives=[]
for name,base,excluded,prefix in [("VisionPlatform-Phase4C-Source.zip",root,{".git",".vs","artifacts","bin","obj","__pycache__"},"VisionPlatform"),("VisionStudio-Phase4C-win-x64.zip",runtime,set(),"VisionStudio")]:
    path=root/"artifacts"/name
    with zipfile.ZipFile(path,"w",zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        for f in sorted(base.rglob("*")):
            relative=f.relative_to(base)
            if f.is_file() and not any(x in excluded for x in relative.parts):z.write(f,prefix+"/"+relative.as_posix())
    with zipfile.ZipFile(path) as z:assert z.testzip() is None
    archives.append(dict(file=name,bytes=path.stat().st_size,sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
dump("artifacts/phase4c-delivery-manifest.json",dict(gitCommit=subprocess.check_output(["git","rev-parse","HEAD"],cwd=root,text=True).strip(),testsPassed=total,publishedDesktopVerified=published,realYoloVerified=True,phase5Started=False,archives=archives))
print(json.dumps(dict(testsPassed=total,publishedDesktopVerified=published,archives=archives),ensure_ascii=False,indent=2))
