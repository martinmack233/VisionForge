using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Projects;
using ZXing;
using ZXing.Common;

namespace VisionPlatform.Plugins.Samples;

/// <summary>Owned synthetic recognition fixtures. External YOLO weights and user data are never redistributed.</summary>
public static class Phase4BExampleFactory
{
    public static async Task SaveAllAsync(string directory, CancellationToken ct = default)
    {
        var store = new ProjectStore(BuiltinRegistration.CreateRegistry());
        foreach (var id in new[] { "ocr", "qr-code", "barcode", "yolo-external" })
        {
            ct.ThrowIfCancellationRequested();
            var folder = Path.Combine(Path.GetFullPath(directory), id); Directory.CreateDirectory(Path.Combine(folder, "images"));
            var text = id switch { "ocr" => "VISION 12345", "qr-code" => "VISIONSTUDIO-QR-2026", "barcode" => "VP12345678", _ => "EXTERNAL MODEL" };
            if (id is "qr-code" or "barcode") WriteCode(Path.Combine(folder, "images/input.png"), text, id == "qr-code");
            else
            {
                using var image = new Mat(160, 640, MatType.CV_8UC1, Scalar.All(255));
                Cv2.PutText(image, text, new Point(24, 110), HersheyFonts.HersheySimplex, 2, Scalar.All(0), 3, LineTypes.AntiAlias);
                Cv2.ImWrite(Path.Combine(folder, "images/input.png"), image);
            }
            using (var image = Cv2.ImRead(Path.Combine(folder, "images/input.png"), ImreadModes.Grayscale))
            using (var preview = new Mat())
            { Cv2.Resize(image, preview, new Size(240, 120), 0, 0, InterpolationFlags.Area); Cv2.ImWrite(Path.Combine(folder, "preview.png"), preview); }
            using (var blank = new Mat(160, 640, MatType.CV_8UC1, Scalar.All(255))) Cv2.ImWrite(Path.Combine(folder, "images/blank.png"), blank);
            var type = id switch { "ocr" => "OCR", "qr-code" => "QRCodeReader", "barcode" => "BarcodeReader", _ => "YoloInference" };
            var nodes = new List<NodeSpecification> { N("input", "ImageInput", new { Path = "images/input.png" }), N("recognize", type, new { }) };
            var edges = new List<NodeConnection> { new("input", "Image", "recognize", "Image") };
            if (id != "yolo-external")
            {
                nodes.Add(N("text", "TextDisplay", new { })); nodes.Add(N("branch", "IfElse", new { }));
                edges.Add(new("recognize", "Text", "text", "String")); edges.Add(new("recognize", "Success", "branch", "Condition"));
            }
            var project = new ProjectDocument { ProjectId = StableId(id), ProjectName = "Phase4B-" + id, Nodes = nodes, Connections = edges };
            await store.SaveAsync(Path.Combine(folder, "project.json"), project, ct);
            var checks = new List<ExampleCheck> { C("recognize", "Success", true) };
            if (id != "yolo-external") checks.Add(C("text", "Text", text));
            var name = id switch { "ocr" => "OCR 英文数字识别", "qr-code" => "二维码识别", "barcode" => "一维条码识别", _ => "YOLO 外部模型推理" };
            var manifest = new ExampleManifest
            {
                Id = id, Name = name, Category = id == "yolo-external" ? "AI 推理" : "字符与条码", Tags = [type, "第四阶段B"],
                Description = id == "yolo-external" ? "调用既有 YOLOStudio 推理及 Tile。未配置外部依赖时明确失败，不生成演示框。" : $"本地合成图真实识别 {text}；Text 进入文本显示，Success 进入条件分支。",
                Instructions = id == "yolo-external" ? "创建副本，配置 PythonExecutable、YoloStudioRoot、ModelPath（匹配的 deployment.json 必须存在），替换为自有授权图像后运行。可启用原系统 Tiled。默认不下载或加载模型。" : "一键运行，选择识别节点的 Result 查看结构化字段及定位 Overlay。绘制轴对齐 ROI 并绑定可限制区域；其他格式先显式转换成 Mono8。可选空白图验证无旧结果。",
                Verification = id == "yolo-external" ? "依赖齐备后必须观察真实输出及模型 SHA256；发布包不含第三方模型。开发机真实 ONNX 和 Tile 记录见验收报告，不将该占位图视为缺陷精度测试。" : "实际文本须与预期一致；空白图 Success=False、Text为空、词/码集合为空，不沿用上一帧。",
                ExternalDependencies = id == "yolo-external" ? ["必需外部依赖：YOLOStudio 原工程、其 Python 环境、授权 ONNX 模型及 deployment.json。请在参数面板配置；本发行包不附带这些资源。"] : [],
                Expected = checks,
                Cases = id == "yolo-external" ? [] : [new("normal", "本地正常图", "images/input.png", checks), new("blank", "空白：清除旧结果", "images/blank.png", [C("recognize", "Success", false), C("text", "Text", "")])]
            };
            await File.WriteAllTextAsync(Path.Combine(folder, "README.md"), $"# {name}\n\n{manifest.Description}\n\n{manifest.Instructions}\n\n{manifest.Verification}\n\n图像为平台自制合成资源，非真实工业样本。OCR 仅随包英文模型。\n", ct);
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != "manifest.json"))
                manifest.Files[Path.GetRelativePath(folder, file).Replace('\\', '/')] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, JsonDefaults.Options), ct);
        }
    }
    private static NodeSpecification N(string id, string type, object p) => new(id, type, type, JsonDefaults.Element(p));
    private static ExampleCheck C(string node, string port, object expected) => new(node, port, JsonDefaults.Element(expected));
    private static Guid StableId(string id) => new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("VisionStudio-Phase4B-" + id))[..16]);
    private static void WriteCode(string path, string text, bool qr)
    {
        var writer = new BarcodeWriterPixelData { Format = qr ? BarcodeFormat.QR_CODE : BarcodeFormat.CODE_128,
            Options = new EncodingOptions { Width = qr ? 300 : 640, Height = qr ? 300 : 200, Margin = qr ? 4 : 20, PureBarcode = true } };
        var pixels = writer.Write(text);
        using var image = new Mat(pixels.Height, pixels.Width, MatType.CV_8UC1);
        var mono = new byte[pixels.Width * pixels.Height];
        for (var i = 0; i < mono.Length; i++) mono[i] = pixels.Pixels[i * 4];
        System.Runtime.InteropServices.Marshal.Copy(mono, 0, image.Data, mono.Length); Cv2.ImWrite(path, image);
    }
}
