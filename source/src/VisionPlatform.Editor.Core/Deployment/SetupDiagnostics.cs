using System.Runtime.InteropServices;
using System.Text.Json;
using VisionPlatform.Algorithms.AI;
using VisionPlatform.Contracts.Build;
using VisionPlatform.Contracts.Deployment;

namespace VisionPlatform.Editor.Core.Deployment;

public enum SetupState { Available, Missing, Incompatible, Unconfigured, Unverified }
public sealed record SetupCheck(string Id, string Name, SetupState State, string Detail, string Remedy)
{
    public string Status => State switch { SetupState.Available => "可用", SetupState.Missing => "缺失", SetupState.Incompatible => "不兼容", SetupState.Unconfigured => "未配置", _ => "未验证" };
}
public sealed record SetupReport(string ProductVersion, string SourceIdentity, string Runtime, DateTimeOffset CheckedAt, IReadOnlyList<SetupCheck> Checks);

public static class SetupDiagnostics
{
    public static async Task<SetupReport> CheckAsync(MachineConfiguration config, string? model = null, CancellationToken ct = default)
    {
        var result = new List<SetupCheck> { new("core", "核心检测平台 / 运行时", Environment.Version.Major == 10 && Environment.Is64BitProcess ? SetupState.Available : SetupState.Incompatible,
            ProductBuild.Version + " / " + RuntimeInformation.FrameworkDescription + " / " + RuntimeInformation.ProcessArchitecture, "使用随包x64自包含程序；核心功能不需要AI或训练组件。") };
        try
        {
            var native = await Task.Run(() => { ct.ThrowIfCancellationRequested(); var version = OpenCvSharp.Cv2.GetVersionString(); using var ocr = new Tesseract.TesseractEngine(Path.Combine(AppContext.BaseDirectory, "tessdata"), "eng", Tesseract.EngineMode.Default); return version; }, ct);
            result.Add(new("native", "OpenCV / Tesseract原生组件", SetupState.Available, "OpenCV " + native + "；Tesseract英文引擎及eng字库实际加载/释放成功，未识别业务图片。", "使用配套x64原生文件；如缺VC++依赖，安装厂商/微软明确要求的x64运行库。"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { result.Add(new("native", "OpenCV / Tesseract原生组件", SetupState.Incompatible, ex.GetBaseException().Message, "保留配套native文件与eng字库；按诊断安装所需x64 VC++运行库，不混用其他版本DLL。")); }
        if (string.IsNullOrWhiteSpace(config.DetectionPython) || string.IsNullOrWhiteSpace(config.DetectionComponentRoot)) result.Add(new("ai-runtime", "AI检测运行组件", SetupState.Unconfigured, "未配置；传统视觉和虚拟相机可继续使用。", "选择现有Python和已制作的检测组件目录；不需要YOLOStudio训练仓库路径。"));
        else if (!File.Exists(config.DetectionPython) || !File.Exists(Path.Combine(config.DetectionComponentRoot, "component.json"))) result.Add(new("ai-runtime", "AI检测运行组件", SetupState.Missing, "Python或component.json不存在。", "选择现有环境，或在新目录制作本机检测组件；不会运行pip。"));
        else
        {
            try
            {
                var probe = await DetectionComponentTool.RunAsync(config.DetectionPython, ["probe", "--root", config.DetectionComponentRoot], ct);
                result.Add(new("ai-runtime", "AI检测运行组件", SetupState.Available, $"组件{probe.GetProperty("component_version")} / 协议{probe.GetProperty("protocol_version")} / Python{probe.GetProperty("python")}；依赖版本和源码哈希匹配。", "按契约维护独立环境；不支持任意复制Python环境。"));
                var providers = probe.GetProperty("available_providers").EnumerateArray().Select(v => v.GetString()).ToArray();
                result.Add(new("device", "CPU / CUDA能力", providers.Contains("CPUExecutionProvider") && config.DetectionBackend == "onnx-cpu" ? SetupState.Available : SetupState.Incompatible,
                    "配置：ONNX CPU；可见Provider：" + string.Join(", ", providers) + "。不初始化GPU；CUDA训练能力须另在可选训练工作区检查。", "不会静默更换设备或后端。运行组件仅支持ONNX CPU。"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { result.Add(new("ai-runtime", "AI检测运行组件", SetupState.Incompatible, ex.Message, "按组件recipe核对Python小版本和逐项依赖版本；不覆盖已有.venv。")); }
        }
        if (!result.Any(c => c.Id == "device")) result.Add(new("device", "CPU / CUDA能力", SetupState.Unconfigured, "AI环境尚未通过检查；不能报告CUDA可用。", "先检查AI运行组件。核心算法CPU执行不受影响。"));
        if (string.IsNullOrWhiteSpace(model)) result.Add(new("model", "模型 / 部署契约 / 哈希", SetupState.Unconfigured, "未选择本轮检查的ONNX模型；不替换工程模型。", "选择模型及同目录deployment.json。仅检查，不执行推理。"));
        else
        {
            try
            {
                var verified = await YoloDeploymentVerifier.VerifyAsync(model, cancellationToken: ct); var actual = "静态契约通过；实际ONNX输入尚未检查。"; var state = SetupState.Unverified;
                if (result.Any(c => c.Id == "ai-runtime" && c.State == SetupState.Available))
                {
                    var check = await DetectionComponentTool.RunAsync(config.DetectionPython, ["model-check", "--root", config.DetectionComponentRoot, "--model", verified.ModelPath], ct);
                    actual = "实际ONNX输入 " + check.GetProperty("input_shape").GetRawText() + " / " + check.GetProperty("provider").GetString() + "；未执行推理。"; state = SetupState.Available;
                }
                result.Add(new("model", "模型 / 部署契约 / 哈希", state, $"部署schema1；输入{verified.InputSize}；类别{verified.Names.Count}；SHA256 {verified.ModelSha256}。" + actual, "文件改变须重新检查，不能沿用旧验证状态。"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { result.Add(new("model", "模型 / 部署契约 / 哈希", File.Exists(model) ? SetupState.Incompatible : SetupState.Missing, ex.Message, "重新选择存在的ONNX及匹配的deployment.json；不下载或重新导出替代模型。")); }
        }
        var trainingFiles = new[] { "core/training/service.py", "core/training/runner.py", "core/inference/backend.py" };
        result.Add(!config.TrainingEnabled ? new("training", "可选AI训练工作区", SetupState.Unconfigured, "未启用；不会启动业务服务、访问数据库或占用GPU。", "需要数据/标注/训练时，显式启用并选择已有工作区和Python。") :
            new("training", "可选AI训练工作区", File.Exists(config.TrainingPython) && trainingFiles.All(f => File.Exists(Path.Combine(config.TrainingRoot, f))) ? SetupState.Unverified : SetupState.Missing, "只检查代码入口与Python存在；训练依赖/GPU及业务锁由原AI工作区诊断，不在向导启动服务。", "在AI工作区连接并执行原环境诊断；不自动训练。"));
        result.Add(new("camera", "博峤适配器 / SDK", string.IsNullOrWhiteSpace(config.BoqSdkPath) ? SetupState.Unconfigured : File.Exists(config.BoqSdkPath) && File.Exists(config.BoqWorkerPath) ? SetupState.Unverified : SetupState.Missing,
            "厂商SDK不随默认包分发。仅有文件不能证明驱动或真实采集可用；不初始化、枚举或连接设备。", "在机器本地配置合法SDK和Worker，先显式运行--inspect-sdk；安装匹配厂商驱动后再验收硬件。"));
        result.Add(new("driver", "厂商驱动", SetupState.Unverified, string.IsNullOrWhiteSpace(config.VendorDriverDirectory) ? "未配置驱动目录；虚拟相机可用。" : Directory.Exists(config.VendorDriverDirectory) ? "目录存在；驱动安装状态/版本及硬件兼容未实测。" : "配置的驱动目录不存在。", "按厂商文档安装驱动；向导不安装、不修改永久配置。"));
        foreach (var (id, name, path) in new[] { ("data", "数据目录", config.DataDirectory), ("cache", "缓存目录", config.CacheDirectory), ("log", "日志目录", config.LogDirectory) })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!Path.IsPathFullyQualified(path) || MachineConfiguration.IsWithin(AppContext.BaseDirectory, path)) throw new ArgumentException("使用程序目录外的绝对路径。");
                Directory.CreateDirectory(path); var temporary = Path.Combine(path, ".vision-write-test-" + Guid.NewGuid().ToString("N"));
                try { await File.WriteAllTextAsync(temporary, "write probe", ct); } finally { if (File.Exists(temporary)) File.Delete(temporary); }
                result.Add(new(id, name, SetupState.Available, "创建/写入/释放测试文件成功；未读取现有用户内容。", "升级保留目录；业务数据不写入程序安装目录。"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { result.Add(new(id, name, SetupState.Incompatible, ex.Message, "选择有写权限且在程序目录外的目录。")); }
        }
        return new(ProductBuild.Version, ProductBuild.Identity, ProductBuild.Runtime, DateTimeOffset.UtcNow, result);
    }
    public static async Task ExportRedactedAsync(string path, SetupReport report, CancellationToken ct = default)
    {
        // Deliberately omit paths, model hashes/names, raw errors, settings and business data.
        var redacted = new { DiagnosticVersion = 1, report.ProductVersion, report.SourceIdentity, report.Runtime, report.CheckedAt,
            Redacted = true, Checks = report.Checks.Select(c => new { c.Id, c.Name, c.Status, c.Remedy }) };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(redacted, new JsonSerializerOptions { WriteIndented = true }), ct);
    }
}
