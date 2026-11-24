using OpenCvSharp;
using VisionPlatform.Algorithms.Blob;
using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Algorithms.Matching;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Matching;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Plugins.Samples;

/// <summary>Deterministic synthetic fixtures, explicitly separate from production camera validation.</summary>
public static class DemoProjectFactory
{
    public static void CreateResources(string directory)
    {
        Directory.CreateDirectory(directory);
        using var template = new Mat(64, 72, MatType.CV_8UC1);
        var random = new Random(20261030);
        var rows = template.Rows; var cols = template.Cols;
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < cols; x++) template.Set(y, x, (byte)random.Next(65, 185));
        Cv2.GaussianBlur(template, template, new Size(3, 3), .7);
        Cv2.Rectangle(template, new Rect(4, 4, 64, 56), new Scalar(225), 2);
        Cv2.Circle(template, new Point(20, 21), 9, new Scalar(25), -1);
        Cv2.PutText(template, "R", new Point(36, 45), HersheyFonts.HersheySimplex, .7, new Scalar(245), 2);
        using var templateFrame = FrameFactory.CopyFromMat(template, PixelFormat.Mono8);
        using var model = new TemplateModel(templateFrame, "synthetic-locator");
        TemplateResourceStore.Save(Path.Combine(directory, "template.json"), model);
        // Teaching and inspection use the same preprocessing domain.
        using var filteredTemplate = GaussianBlurProcessor.Process(templateFrame, new() { KernelSize = 5, SigmaX = 1.2 });
        using var filteredModel = new TemplateModel(filteredTemplate, "synthetic-locator-gaussian");
        TemplateResourceStore.Save(Path.Combine(directory, "template-blurred.json"), filteredModel);
        WriteScene(template, Path.Combine(directory, "sample.png"), 221.5, 163.5, 10, 1.1, 2);
        WriteScene(template, Path.Combine(directory, "shifted.png"), 178.5, 155.5, -10, .9, 2);
        WriteScene(template, Path.Combine(directory, "ng.png"), 221.5, 163.5, 10, 1.1, 3);
        using var mono = ImageFile.Load(Path.Combine(directory, "sample.png"));
        using var color = PixelFormatConverter.Convert(mono, PixelFormat.BGR8);
        ImageFile.Save(color.Image, Path.Combine(directory, "sample-color.png"));
        using var empty = new Mat(320, 384, MatType.CV_8UC1, new Scalar(35));
        Cv2.ImWrite(Path.Combine(directory, "absent.png"), empty);
    }

    private static void WriteScene(Mat template, string path, double cx, double cy, double angle, double scale, int count)
    {
        var r = angle * Math.PI / 180; var c = scale * Math.Cos(r); var s = scale * Math.Sin(r);
        var tx = (template.Cols - 1) / 2.0; var ty = (template.Rows - 1) / 2.0;
        using var matrix = new Mat(2, 3, MatType.CV_64FC1);
        matrix.Set(0, 0, c); matrix.Set(0, 1, -s); matrix.Set(0, 2, cx - c * tx + s * ty);
        matrix.Set(1, 0, s); matrix.Set(1, 1, c); matrix.Set(1, 2, cy - s * tx - c * ty);
        using var scene = new Mat();
        Cv2.WarpAffine(template, scene, matrix, new Size(384, 320), InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(35));
        var points = new[] { (58.0, -8.0), (72.0, 6.0), (55.0, 9.0) };
        foreach (var (x, y) in points.Take(count))
            Cv2.Circle(scene, new Point((int)Math.Round(cx + c * x - s * y), (int)Math.Round(cy + s * x + c * y)),
                Math.Max(3, (int)Math.Round(4.5 * scale)), new Scalar(245), -1);
        Cv2.ImWrite(path, scene);
    }

    public static ProjectDocument Create(bool preprocess)
    {
        var matcher = new CvInspectTemplateMatcher();
        var nodes = new List<NodeSpecification>
        {
            Node("input", "ImageInput", new ImageInputParameters { Path = preprocess ? "sample-color.png" : "sample.png" })
        };
        var edges = new List<NodeConnection>();
        var source = "input"; var port = "Image";
        if (preprocess)
        {
            nodes.Add(Node("convert", "PixelFormatConvert", new PixelFormatConvertParameters { TargetFormat = PixelFormat.Mono8 }));
            nodes.Add(Node("blur", "GaussianBlur", new GaussianBlurParameters { Options = new GaussianBlurOptions { KernelSize = 5, SigmaX = 1.2 } }));
            edges.Add(new(source, port, "convert", "Image")); edges.Add(new("convert", "ConvertedImage", "blur", "Image"));
            source = "blur"; port = "ProcessedImage";
        }
        nodes.Add(Node("match", "TemplateMatching", new TemplateMatchingParameters
        {
            TemplatePath = preprocess ? "template-blurred.json" : "template.json",
            Options = new TemplateMatchOptions { MinimumScore = .7, SearchRoi = new(80, 65, 270, 210) }
        }));
        nodes.Add(Node("roi", "ROITransform", new ROITransformParameters
        { ReferencePose = new(100, 100), ReferenceROI = new(165, 100, 50, 34) }));
        nodes.Add(Node("blob", "BlobDetection", new BlobOptions { Threshold = 180, MinArea = 10, MaxArea = 200 }));
        nodes.Add(Node("decision", "ResultDecision", new ResultDecisionParameters()));
        edges.AddRange(new[] { new NodeConnection(source, port, "match", "Image"),
            new(source, port, "roi", "Image"), new("match", "Pose2D", "roi", "Pose2D"),
            new(source, port, "blob", "Image"), new("roi", "ROI", "blob", "ROI"),
            new("blob", "BlobCount", "decision", "BlobCount"), new("match", "Success", "decision", "MatchSuccess") });
        return new ProjectDocument
        {
            ProjectName = preprocess ? "DemoInspection-B" : "DemoInspection-A", Nodes = nodes, Connections = edges,
            ROI = [new(165, 100, 50, 34)], TemplateResources = [new("locator", preprocess ? "template-blurred.json" : "template.json")],
            AlgorithmVersion = new() { [matcher.AlgorithmId] = matcher.Version }
        };
    }
    public static NodeSpecification Node<T>(string id, string type, T parameters) => new(id, type, type, JsonDefaults.Element(parameters));
}
