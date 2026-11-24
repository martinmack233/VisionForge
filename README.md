# 视构 VisionForge 0.6.3

[Read in English](#english)

视构是一款面向 Windows 的工业视觉工作台。你可以用节点连接图像处理、定位测量、英文 OCR 和结果判断，并把流程保存为可移植的项目。

**[下载 Windows 11 x64 正式版](https://github.com/martinmack233/VisionForge/releases/download/v0.6.3/VisionForge-0.6.3-win-x64.zip)** · [发行说明、校验文件和验收报告](https://github.com/martinmack233/VisionForge/releases/tag/v0.6.3)

1. 下载完整 ZIP，解压到独立目录，再运行 `VisionForge.exe`。包内自带 .NET 10.0.15。
2. 第一次打开时，可在配置向导中选择“关闭，继续核心检测”。
3. 进入 **方案示例 → 从第1课开始 → 一键运行**。每次打开会创建可编辑的工程副本。

五个入门案例从图像、数字与布尔显示讲起，随后介绍矩形裁切、旋转裁切、集合筛选与选择，以及对三个 ROI 逐项执行同一个子流程。各案例提供操作步骤、预期结果和小练习。传统视觉、英文 OCR 和文件夹虚拟相机可离线运行。AI 模型、Python 环境、厂商 SDK 和驱动按需另行配置。

正式版通过 850 项自动化测试，并验证了 25 个 Console/Host 离线示例和 5 个 WPF 入门案例。程序为未签名的便携版。真实设备、全新 Windows 系统、外部 AI 模型及长期产线运行的验收范围见发行页。随包 `component-manifest.json` 和 `licenses` 列出了组件和第三方许可信息。

## English

VisionForge is an industrial machine vision workbench for Windows. Connect nodes for image processing, alignment and measurement, English OCR, and inspection decisions, then save the workflow as a portable project.

**[Download the Windows 11 x64 release](https://github.com/martinmack233/VisionForge/releases/download/v0.6.3/VisionForge-0.6.3-win-x64.zip)** · [Release notes, checksum, and acceptance report](https://github.com/martinmack233/VisionForge/releases/tag/v0.6.3)

1. Download and extract the full ZIP to its own folder. Launch `VisionForge.exe`. The package includes the .NET 10.0.15 runtime.
2. On first launch, use **Close and continue with core inspection** (关闭，继续核心检测) to proceed through the setup wizard.
3. Open **Examples → Start with Lesson 1 → Run** (方案示例 → 从第1课开始 → 一键运行). Opening a lesson creates an editable project copy.

The five lessons cover image, number, and Boolean displays, rectangular cropping, rotated cropping, collection filtering and selection, and running the same subflow over three ROIs. Each lesson includes instructions, expected results, and a short exercise. Classical vision, English OCR, and the folder-based virtual camera run offline. AI models, Python, vendor SDKs, and drivers can be configured separately.

The release passed 850 automated tests. Validation also covered 25 offline Console and RuntimeHost examples and five WPF lessons. The portable executable is unsigned. See the release acceptance report for the scope of testing on Windows, physical hardware, external AI models, and extended production runs. Component and third-party notices are included in `component-manifest.json` and `licenses`.
