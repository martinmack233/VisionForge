# AI 辅助写算法 | AI-assisted algorithm development

VisionForge 可以作为外部 AI 编程助手生成流程和算法代码后的执行环境。你可以让助手选择已有节点，描述具体连线和参数，或生成 C# 自定义算子供你检查和编译。

## 一次典型任务

1. 明确图片格式、坐标系、输入输出、正常与异常样例。先请 AI 给出流程和判定依据。
2. 优先让 AI 组合现有节点，例如“读图 → 定位或裁切 → Blob 检测 → 数量判定 → 结果显示”。按已知工程和节点名称配置流程。
3. 现有节点不足时，把[独立插件示例](../examples/dotnet-brightness/)交给 AI 作为契约参考，要求它生成新的节点 ID、类型化端口和参数检查。
4. 人工审核代码，再用 .NET 10 SDK 编译为 DLL。通过 RuntimeHost 指向本地合成样例执行，检查数值和异常输入。
5. 核对结果后再进入业务部署。生产通信、相机和训练应在对应环境中单独验收。

## 可复制的提示词

> 参考 VisionForge 的 BrightPixelRatioNode 示例，为 Windows x64 生成一个实现 IExecutableNode 的 C# 图像算子。输入为只读 Mono8 ImageFrame，参数 Threshold 为 0 到 255 的整数，输出亮像素数、占比和是否达到阈值。逐行读取像素并响应取消。请同时给出可编译项目、一个纯离线流程和已知输入下的预期输出。使用示例中确认的接口，并标明其他依赖。

## English

Use an external coding assistant to draft a node graph or a C# plugin based on the published example. Define the input format and expected outputs, ask for typed ports and validation, review the code, then compile and run it on an offline fixture using RuntimeHost. The desktop application provides the workflow editor and execution interfaces. Conversational code generation and automatic execution are not built into the app.

See the [integration guide](integration.md) for the Console, RuntimeHost, and .NET entry points.
