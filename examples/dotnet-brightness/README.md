# .NET SDK 示例 | Bright pixel ratio plugin

这个独立示例展示如何实现 VisionForge 的 `IExecutableNode`。输入为 `Mono8`，将像素与 `Threshold` 比较，输出亮像素数、总像素数及占比。运行使用本地合成图和 RuntimeHost 的单次模式。

Use the .NET 10 SDK and a complete VisionForge 0.6.3 extraction. Open PowerShell in this example's directory, set the release path, and compile.

```powershell
$release = 'D:\Apps\VisionForge-0.6.3-win-x64'
dotnet build -c Release "-p:VisionForgeDir=$release"
Copy-Item (Join-Path $release 'Examples\beginner-01-display\input.png') -Destination '.\input.png'
$dll = (Resolve-Path '.\bin\Release\net10.0\BrightPixelRatio.Plugin.dll').Path
$project = (Resolve-Path '.\project.json').Path
& (Join-Path $release 'VisionPlatform.RuntimeHost.exe') --project $project --once --plugin $dll
```

在返回 JSON 的节点 `bright` 中查看 `BrightPixels`、`TotalPixels` 和 `Ratio`。合成样例的预期值依次为 4400、76800 和约 0.05729。阈值为 0 时占比为 1。

The plugin consumes a borrowed image and disposes only its own buffer lease. It accepts thresholds from 0 through 255 and checks cancellation between rows. Run this example through RuntimeHost with the explicit plugin argument when developing your own node.
