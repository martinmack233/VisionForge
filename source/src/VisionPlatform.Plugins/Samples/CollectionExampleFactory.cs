using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Runtime.Projects;
using VisionPlatform.Plugins.Builtin;

namespace VisionPlatform.Plugins.Samples;
public static class CollectionExampleFactory
{
    public static async Task SaveAllAsync(string root, CancellationToken ct = default)
    {
        var store = new ProjectStore(BuiltinRegistration.CreateRegistry());
        foreach (var ocr in new[] { true, false })
        {
            var id = ocr ? "collection-multi-ocr" : "collection-blob-measure";
            var folder = Path.Combine(Path.GetFullPath(root), id); Directory.CreateDirectory(folder);
            using var image = new Mat(480, 640, MatType.CV_8UC1, Scalar.All(ocr ? 255 : 0));
            if (ocr) foreach (var (text, i) in new[] { "VISION", "123456", "PART42" }.Select((t, i) => (t, i)))
                Cv2.PutText(image, text, new(35, 100 + 150 * i), HersheyFonts.HersheySimplex, 1.8, Scalar.All(0), 3, LineTypes.AntiAlias);
            else
            {
                Cv2.Rectangle(image, new Rect(40, 50, 20, 30), Scalar.All(255), -1);
                Cv2.Rectangle(image, new Rect(170, 130, 30, 40), Scalar.All(255), -1);
                Cv2.Rectangle(image, new Rect(350, 250, 50, 50), Scalar.All(255), -1);
                Cv2.Rectangle(image, new Rect(500, 360, 3, 3), Scalar.All(255), -1);
            }
            Cv2.ImWrite(Path.Combine(folder, "input.png"), image);
            var project = Create(ocr);
            await store.SaveAsync(Path.Combine(folder, "project.json"), project, ct);
            using var preview = new Mat(); Cv2.Resize(image, preview, new Size(320, 240)); Cv2.ImWrite(Path.Combine(folder, "preview.png"), preview);
            var instructions = ocr
                ? "打开后先复制为用户工程，再运行。图中三个英文/数字区域使用正式RoiCollection参数，ForEach按0、1、2顺序调用原OCR节点。选择集合结果显示节点，在集合/逐项面板选择结果可高亮及查看局部；选择逐项节点可查看保留迭代端口。参数面板可编辑ROI集合和子流程，OCR文本布局可选择单行、多行块或自动。"
                : "复制并运行。BlobDetection真实输出四个连通域；集合按Area>=100筛除小点，按X稳定排序，再逐项把当前Blob转ROI，用原DistanceMeasure计算ROI中心到右边中点距离，并以>=12像素判定。结果应为10/15/25像素，对应NG/OK/OK。参数面板可改变筛选和判定，旧结果会失效。";
            var readme = $"# {project.ProjectName}\n\n{instructions}\n\n输入图像由OpenCV绘制，为自制合成资源，不含用户图片、模型或数据库；不代表工业准确率认证。所有示例运行使用原Runtime，未触发训练、GPU、相机或通信。\n\n逐项异常可选择停止或记录继续；空集合返回0项。调试图像仅保留显式预算，不长期保留每项整图。正角顺时针，原图像素坐标，索引从0开始。\n\n可选YOLO衔接：在用户副本中显式配置已有授权模型及deployment.json，连接 YoloInference.Result → CollectionAdapter.Detections → CollectionFilter/Sort → CollectionToRois → ForEach.Collection，并连接同一源图到ForEach.Image；选择OCR子流程模板。该可选路径不携带权重、不自动下载，未用用户模型验收。\n";
            await File.WriteAllTextAsync(Path.Combine(folder, "README.md"), readme, ct);
            var manifest = new ExampleManifest { Id = id, Name = project.ProjectName, Category = "集合与子流程", Tags = ["只读集合", "逐项", ocr ? "OCR" : "Blob", "合成样本"],
                Description = ocr ? "三个独立ROI顺序识别，保留每项来源、索引、文本及置信度。无需YOLO权重。" : "真实Blob集合筛选排序，逐项测量并汇总OK/NG。", Instructions = instructions,
                Verification = ocr ? "三项分别返回VISION、123456、PART42；原图ROI与结果关联不混用。缺语言资源应明确报错。" : "筛选保留3项，测量Number=10、15、25，业务状态NG、OK、OK。",
                ExternalDependencies = ocr ? ["随包已审核Tesseract x64与eng语言模型；仅英文/数字能力。"] : [],
                Expected = [new("each", "CompletedCount", JsonDefaults.Element(3)), new("each", "Complete", JsonDefaults.Element(true))] };
            foreach (var path in Directory.EnumerateFiles(folder).Where(p => Path.GetFileName(p) != "manifest.json"))
                manifest.Files[Path.GetFileName(path)] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, ct)));
            await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, JsonDefaults.Options), ct);
        }
    }
    public static ProjectDocument Create(bool ocr)
    {
        var nodes = new List<NodeSpecification> { CollectionSubflows.N("input", "ImageInput", new ImageInputParameters { Path = "input.png" }) };
        var edges = new List<NodeConnection>();
        if (ocr)
        {
            nodes.Add(CollectionSubflows.N("regions", "RoiCollection", new RoiCollectionOptions { Regions = Enumerable.Range(0, 3).Select(i => new RectRoi(10, 5 + 150 * i, 620, 135).ToRotated()).ToList() }));
            edges.Add(new("input", "Image", "regions", "Image"));
        }
        else
        {
            nodes.AddRange([CollectionSubflows.N("blob", "BlobDetection", new { MinArea = 3 }), CollectionSubflows.N("adapt", "CollectionAdapter", new { }),
                CollectionSubflows.N("filter", "CollectionFilter", new CollectionFilterOptions { Field = "Area", Number = 100 }), CollectionSubflows.N("sort", "CollectionSort", new { Field = "X" })]);
            edges.AddRange([new("input", "Image", "blob", "Image"), new("blob", "BlobResults", "adapt", "Blobs"), new("input", "Image", "adapt", "Image"),
                new("adapt", "Collection", "filter", "Collection"), new("filter", "Collection", "sort", "Collection")]);
        }
        nodes.Add(CollectionSubflows.N("each", "ForEach", new ForEachOptions { Body = ocr ? CollectionSubflows.Ocr() : CollectionSubflows.Measure() }));
        nodes.Add(CollectionSubflows.N("display", "CollectionDisplay", new CollectionDisplayOptions { Fields = ocr ? "Index,Text,Confidence,State" : "Index,Number,State", SummaryLines = 3 }));
        edges.AddRange([new(ocr ? "regions" : "sort", "Collection", "each", "Collection"), new("input", "Image", "each", "Image"),
            new("each", "Results", "display", "Collection"), new("input", "Image", "display", "Image")]);
        return new() { ProjectName = ocr ? "多区域英文OCR · 集合逐项" : "多Blob · 筛选排序与逐项测量", Nodes = nodes, Connections = edges };
    }
}
