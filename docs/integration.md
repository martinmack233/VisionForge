# 开发者接入 | Developer integration

| 入口 | 运行方式 |
|---|---|
| Console | 子进程执行已保存的工程并输出节点摘要。适合批处理或离线验证。 |
| RuntimeHost | 单次执行返回 JSON，也可作为 TCP 服务管理工程会话。 |
| .NET | 从同版本发行目录引用所需程序集和配套依赖，使用 `NodeRegistry`、`ProjectStore`、`RuntimeExecutor` 集成。 |
| 自定义节点 | 实现 `IExecutableNode`，编译 .NET 10 x64 兼容 DLL，由 RuntimeHost 使用绝对路径显式加载。 |

The bundled Windows hosts and .NET assemblies form the integration surface. Keep the executable, assemblies, and native dependencies from the same release.

## 离线命令 | Offline commands

在 PowerShell 中进入完整解压目录，使用内置且无需硬件的示例。

```powershell
$project = (Resolve-Path '.\Examples\beginner-05-loop\project.json').Path
.\VisionPlatform.Console.exe --version
.\VisionPlatform.Console.exe run $project
.\VisionPlatform.RuntimeHost.exe --project $project --once
```

Console 摘要显示节点状态和业务结果。退出码 2 也可能代表执行成功但判定为 NG，请结合摘要判断。RuntimeHost 的 `--once` 将执行成功状态和业务结果分别放在 JSON 中。单次 Host 调用不会开启 TCP 监听。

For an existing packaged project, use `package-inspect` to check dependencies, then import it into a new directory with `package-import`.

```powershell
.\VisionPlatform.Console.exe package-inspect '.\ProjectPackages\beginner-05-loop.vspkg'
.\VisionPlatform.Console.exe package-import '.\ProjectPackages\beginner-05-loop.vspkg' '.\MyLoopProject'
```

## .NET 开发和自定义算子

[亮像素统计示例](../examples/dotnet-brightness/)包含 C# 源码、项目文件和可由 RuntimeHost 加载的演示工程。使用 .NET 10 SDK 编译后，从发行目录加载算子。示例只读取本地灰度图，返回亮像素数量、总像素数和占比。

RuntimeHost loads managed plugins through the `IExecutableNode` contract. Provide typed ports through `NodeDescriptor`, validate parameters, honor cancellation, and release output resources according to the runtime ownership contract. Use an explicit DLL path for trusted plugins. The loader executes plugin code in the host process, so review the code and its dependencies before loading it.

## Communication

**工程内通信**支持 TCP 客户端/服务端、串口以及 Modbus TCP/RTU。运行前在节点中配置端点、串口参数、单元地址、消息边界和超时。Modbus 可读写线圈和寄存器，解码时可指定数据类型、字节序和字序。建议先用自带的本机通信示例或 RTU 协议模拟核对配置。

**上位机工程控制**由独立 RuntimeHost 提供。每个连接管理独立会话。消息为 4 字节大端长度前缀加 UTF-8 JSON。常用操作依次为 `load`、`run`、`status`、`result`、`close`，运行中可请求 `cancel`。工程路径由 Host 本机访问，图像结果通过元数据描述。默认使用本机地址 `127.0.0.1`。部署到网络时应通过受控访问边界使用。

```powershell
.\VisionPlatform.RuntimeHost.exe --address 127.0.0.1 --port 8766
```

Workflow communication and host control are separate interfaces. Workflow nodes exchange messages with a device or service. RuntimeHost controls projects and returns structured execution results. Review each flow before running it against a production endpoint.

## YOLO

YOLO 模型需要模型文件和对应的部署契约。先在 **设置 → 首次配置与部署诊断** 核对本机 Python、AI 检测组件和模型路径，然后在流程里设置推理节点。正式版的流程内推理后端为 **ONNX CPU**。部署方需自行配置兼容环境和有权使用的模型。

训练工作区通过现有 YOLOStudio 环境执行数据集整理、标注、切片、训练及评估。训练及模型部署由用户发起。上位机如果只调用传统视觉流程，可直接通过 Console、RuntimeHost 或 .NET 执行。

YOLO inference requires a compatible ONNX model, its deployment metadata, and a configured Python component. The optional training workspace uses an existing YOLOStudio installation. Core vision runs independently of the training environment.
