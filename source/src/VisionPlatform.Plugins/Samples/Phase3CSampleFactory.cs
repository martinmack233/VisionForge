using System.Collections.ObjectModel;
using OpenCvSharp;
using VisionPlatform.Algorithms.Matching;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Plugins.Samples;

/// <summary>Deterministic synthetic examples. Never a substitute for production-camera validation.</summary>
public static class Phase3CSampleFactory
{
    public static void CreateResources(string directory, bool includeLargeImage = true)
    {
        Directory.CreateDirectory(directory); DemoProjectFactory.CreateResources(directory);
        using (var image = new Mat(144, 200, MatType.CV_8UC1, Scalar.All(20)))
        {
            Cv2.Rectangle(image, new Rect(20, 20, 20, 16), Scalar.All(220), -1);
            Cv2.Rectangle(image, new Rect(85, 42, 14, 12), Scalar.All(230), -1);
            Cv2.Rectangle(image, new Rect(160, 100, 3, 3), Scalar.All(245), -1);
            Cv2.ImWrite(Path.Combine(directory, "inspection.png"), image);
        }
        if (includeLargeImage)
        {
            using var builder = new ImageFrameBuilder(8192, 10000, PixelFormat.Mono8);
            var even = Enumerable.Range(0, 8192).Select(x => x % 2 == 0 ? (byte)23 : (byte)231).ToArray();
            var odd = even.Select(v => v == 23 ? (byte)231 : (byte)23).ToArray();
            for (var y = 0; y < 10000; y++) (y % 2 == 0 ? even : odd).AsSpan().CopyTo(builder.GetRowSpan(y));
            using var image = builder.Build(); ImageFile.Save(image, Path.Combine(directory, "large-8192x10000.bmp"));
        }
    }

    public static IReadOnlyDictionary<string, ProjectDocument> CreateProjects()
    {
        var original = DemoProjectFactory.Create(false);
        var projects = new Dictionary<string, ProjectDocument>(StringComparer.Ordinal)
        {
            ["flow1-preprocessing.json"] = new()
            {
                ProjectName = "C1-中值与Otsu预处理", Nodes = [N("input", "ImageInput", new { Path = "inspection.png" }), N("median", "MedianBlur", new { KernelSize = 3 }),
                    N("otsu", "OtsuThreshold", new { }), N("morph", "Morphology", new { Operation = "Open" }), N("display", "ImageDisplay", new { })],
                Connections = [E("input", "Image", "median", "Image"), E("median", "ProcessedImage", "otsu", "Image"), E("otsu", "BinaryImage", "morph", "Image"), E("morph", "ProcessedImage", "display", "Image")]
            },
            ["flow2-filter.json"] = new()
            {
                ProjectName = "C2-目标筛选与判定", Nodes = [N("input", "ImageInput", new { Path = "inspection.png" }), N("threshold", "Threshold", new { ThresholdValue = 128 }),
                    N("blob", "BlobDetection", new { MinArea = 1 }), N("filter", "BlobFilter", new { Area = new { Enabled = true, Minimum = 100, Maximum = 1000 } }),
                    N("decision", "ResultDecision", new { MinimumBlobCount = 2, MaximumBlobCount = 2 }), N("report", "ResultDisplay", new { })],
                Connections = [E("input", "Image", "threshold", "Image"), E("threshold", "ProcessedImage", "blob", "Image"), E("blob", "BlobResults", "filter", "BlobResults"),
                    E("filter", "BlobCount", "decision", "BlobCount"), E("decision", "Decision", "report", "Decision"), E("filter", "BlobCount", "report", "BlobCount")]
            },
            ["flow3-matching.json"] = original with
            {
                ProjectName = "C3-模板定位旋转裁切", Nodes = [original.Nodes.Single(n => n.NodeId == "input"), original.Nodes.Single(n => n.NodeId == "match"),
                    N("crop", "RotatedCrop", new { Width = 72, Height = 64 }), N("display", "ImageDisplay", new { })],
                Connections = [E("input", "Image", "match", "Image"), E("input", "Image", "crop", "Image"), E("match", "Pose2D", "crop", "Pose2D"), E("crop", "CroppedImage", "display", "Image")], ROI = []
            },
            ["flow4-conditional-tcp.json"] = new()
            {
                ProjectName = "C4-比较条件通信", Nodes = [N("input", "ImageInput", new { Path = "inspection.png" }), N("blob", "BlobDetection", new { MinArea = 20 }),
                    N("compare", "Compare", new { ValueB = 2, Operator = "GreaterOrEqual" }), N("if", "IfElse", new { }), N("connect", "TcpConnect", new { Options = new { Host = "127.0.0.1", Port = 9000 } }),
                    N("ok", "TcpSend", new { SendEnabled = true, DeliveryKey = "phase3c-sample-1", LiteralMessage = "OK" }), N("ng", "TcpSend", new { SendEnabled = true, DeliveryKey = "phase3c-sample-1", LiteralMessage = "NG" }),
                    N("merge", "Merge", new { }), N("report", "ResultDisplay", new { })],
                Connections = [E("input", "Image", "blob", "Image"), E("blob", "BlobCount", "compare", "ValueA"), E("compare", "Result", "if", "Condition"), E("if", NodePorts.Completed, "connect", NodePorts.Execute),
                    E("connect", "Connection", "ok", "Connection"), E("connect", "Connection", "ng", "Connection"), E("if", "True", "ok", NodePorts.Execute), E("if", "False", "ng", NodePorts.Execute),
                    E("ok", NodePorts.Completed, "merge", "TrueControl"), E("ok", "Status", "merge", "TrueValue"), E("ng", NodePorts.Completed, "merge", "FalseControl"), E("ng", "Status", "merge", "FalseValue"),
                    E("merge", "Communication", "report", "Communication"), E("compare", "Result", "report", "IsOk"), E("blob", "BlobCount", "report", "BlobCount")]
            },
            ["flow5-large.json"] = new()
            {
                ProjectName = "C5-82MP原始像素查看", Nodes = [N("input", "ImageInput", new { Path = "large-8192x10000.bmp" }), N("display", "ImageDisplay", new { })],
                Connections = [E("input", "Image", "display", "Image")]
            },
            ["flow6-enhance-transform.json"] = new()
            {
                ProjectName = "C6-增强及图像变换", Nodes = [N("input", "ImageInput", new { Path = "inspection.png" }), N("normalize", "Normalize", new { Minimum = 10, Maximum = 240 }),
                    N("equalize", "EqualizeHist", new { }), N("clahe", "CLAHE", new { ClipLimit = 2.0, TileGridSize = 8 }), N("adaptive", "AdaptiveThreshold", new { Method = "Gaussian", BlockSize = 11, C = 2.0 }),
                    N("rotate", "RotateImage", new { Angle = 15, OutputSize = "ExpandToFit" }), N("resize", "ResizeImage", new { Mode = "Scale", ScaleX = .75, ScaleY = .75 }),
                    N("flip", "FlipImage", new { Direction = "Horizontal" }), N("display", "ImageDisplay", new { })],
                Connections = [E("input", "Image", "normalize", "Image"), E("normalize", "ProcessedImage", "equalize", "Image"), E("equalize", "ProcessedImage", "clahe", "Image"), E("clahe", "ProcessedImage", "adaptive", "Image"),
                    E("adaptive", "BinaryImage", "rotate", "Image"), E("rotate", "RotatedImage", "resize", "Image"), E("resize", "ResizedImage", "flip", "Image"), E("flip", "FlippedImage", "display", "Image")]
            }
        };
        return new ReadOnlyDictionary<string, ProjectDocument>(projects);
    }

    public static async Task SaveAllAsync(string directory, bool includeLargeImage = true)
    {
        await Task.Run(() => CreateResources(directory, includeLargeImage)).ConfigureAwait(false);
        var matcher = new CvInspectTemplateMatcher();
        var store = new ProjectStore(BuiltinRegistration.CreateRegistry(), new Dictionary<string, string> { [matcher.AlgorithmId] = matcher.Version });
        foreach (var (name, project) in CreateProjects()) await store.SaveAsync(Path.Combine(directory, name), project).ConfigureAwait(false);
    }
    private static NodeSpecification N(string id, string type, object options) => new(id, type, type, JsonDefaults.Element(options));
    private static NodeConnection E(string source, string port, string target, string input) => new(source, port, target, input);
}
