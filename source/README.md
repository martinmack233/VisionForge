# VisionForge 0.6.3 源码快照 | Source snapshot

此目录包含可构建的 0.6.3 源码、运行时、桌面编辑器、算法、测试、合成示例以及构建工具。用 PowerShell 7 和 `global.json` 指定的 .NET 10 SDK，在本目录运行 `.\build.ps1`。SDK 路径可以通过 `-DotNet` 指定。

This directory contains the buildable VisionForge 0.6.3 source snapshot, its tests and synthetic examples. Run `.\build.ps1` here with PowerShell 7 and the .NET 10 SDK selected by `global.json`.

- [解决方案 / Solution](VisionPlatform.sln)
- [许可证及通知 / License notices](licenses/) · [第三方许可 / Third-party license notices](third_party/licenses/)
- [CvInspect LICENSE](third_party/CvInspect/LICENSE) · [Nodify LICENSE](third_party/Nodify/LICENSE)
- [开发者接入 / Integration](../docs/integration.md) · [发行资料 / Release information](../release-info/v0.6.3/)

公开快照保留当前程序和合成测试资源。旧项目回读测试使用独立的合成 fixture。历史日志、开发机比对脚本、原 YOLOStudio 项目及博峤 SDK 归档由私有开发环境管理。外部模型、厂商驱动和依赖包按各自许可配置。随附许可证分别适用于对应组件。

The public snapshot includes a synthetic fixture for reading legacy projects. Historical logs, machine-specific comparisons, the external YOLOStudio project, and the vendor SDK archive remain in the private development environment. The included notices apply to their respective components.

## 快照验证 | Snapshot verification

锁定依赖还原与 Release 构建通过，零警告、零错误。本目录运行的 850 项回归中，847 项通过。其余 3 项实际 YOLOStudio 集成测试要求上级目录有 `YOLOStudio/.venv/Scripts/python.exe` 及对应工作区代码。独立快照缺少该外部环境，测试保留原有断言。

Locked restore and the Release build passed with zero warnings and errors. Of 850 tests, 847 passed. Three live YOLOStudio integration tests require a sibling YOLOStudio checkout and its existing Python environment. Those tests retain their original assertions.
