using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using VisionPlatform.Algorithms.Blob;
using VisionPlatform.Algorithms.Logic;
using VisionPlatform.Algorithms.Transforms;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Plugins.Samples;

/// <summary>Self-authored small offline lessons. Execution is always through the existing Runtime.</summary>
public static class BeginnerExampleFactory
{
    public const string Category = "新手入门";
    public static readonly string[] Ids = ["beginner-01-display", "beginner-02-crop", "beginner-03-rotated-crop", "beginner-04-collection", "beginner-05-loop"];
    private static NodeSpecification N(string id, string type, string name, object p) => new(id, type, name, JsonDefaults.Element(p));
    private static NodeConnection E(string a, string b, string c, string d) => new(a, b, c, d);
    private static ExampleCheck C(string node, string port, object value, string property = "", double tolerance = 0) => new(node, port, JsonDefaults.Element(value), tolerance, property);

    public static ProjectDocument Create(int lesson)
    {
        var nodes = new List<NodeSpecification> { N("input", "ImageInput", "① 读取自制图片", new ImageInputParameters { Path = "input.png" }) };
        var edges = new List<NodeConnection>();
        if (lesson == 0)
        {
            nodes.AddRange([N("blobs", "BlobDetection", "② 数一数亮色目标", new BlobOptions()),
                N("image", "ImageDisplay", "③ 看图像和目标框", new ImageDisplayParameters()),
                N("count", "TextDisplay", "④ 看数字转文字", new TextDisplayParameters { Prefix = "目标数量：" }),
                N("compare", "Compare", "⑤ 数量是否等于3", new CompareOptions { Operator = CompareOperator.Equal, ValueB = 3 }),
                N("boolean", "TextDisplay", "⑥ 看布尔结果", new TextDisplayParameters { Prefix = "数量等于3：" })]);
            edges.AddRange([E("input", "Image", "blobs", "Image"), E("input", "Image", "image", "Image"), E("blobs", "BlobResults", "image", "Blobs"),
                E("blobs", "BlobCount", "count", "Integer"), E("blobs", "BlobCount", "compare", "ValueA"), E("compare", "Result", "boolean", "Boolean")]);
        }
        else if (lesson is 1 or 2)
        {
            var rotated = lesson == 2;
            nodes.AddRange([N("original", "ImageDisplay", "② 查看原图", new ImageDisplayParameters()),
                N("crop", rotated ? "RotatedCrop" : "CropImage", rotated ? "③ 顺时针30°旋转裁切" : "③ 从(30,30)裁出80×60", rotated
                    ? new RotatedCropOptions { CenterX = 160, CenterY = 120, Width = 120, Height = 60, Angle = 30 }
                    : new CropImageOptions { X = 30, Y = 30, Width = 80, Height = 60 }),
                N("cropped", "ImageDisplay", "④ 查看裁切后的新图", new ImageDisplayParameters())]);
            edges.AddRange([E("input", "Image", "original", "Image"), E("input", "Image", "crop", "Image"), E("crop", "CroppedImage", "cropped", "Image")]);
        }
        else if (lesson == 3)
        {
            nodes.AddRange([N("blobs", "BlobDetection", "② 找到4个目标（含小点）", new BlobOptions()),
                N("adapt", "CollectionAdapter", "③ 转为Blob集合", new CollectionAdapterOptions()),
                N("filter", "CollectionFilter", "④ 保留面积≥100像素²", new CollectionFilterOptions { Field = "Area", Number = 100 }),
                N("sort", "CollectionSort", "⑤ 按X从左到右排序", new CollectionSortOptions { Field = "X" }),
                N("select", "CollectionSelect", "⑥ 索引1＝第2个目标", new CollectionIndexOptions { Index = 1 }),
                N("roi", "ItemToRoi", "⑦ 当前目标转ROI", new ItemRoiOptions()),
                N("crop", "RotatedCrop", "⑧ 按所选ROI裁切", new RotatedCropOptions()),
                N("cropped", "ImageDisplay", "⑨ 看第2个目标局部图", new ImageDisplayParameters()),
                N("count", "CollectionCount", "⑩ 筛选后有多少项", new CollectionCountOptions()),
                N("text", "TextDisplay", "⑪ 显示集合数量", new TextDisplayParameters { Prefix = "筛选后目标数：" }),
                N("display", "CollectionDisplay", "⑫ 看全部筛选结果", new CollectionDisplayOptions { Fields = "Index,Area,X,Y,Width,Height", DecimalPlaces = 2 })]);
            edges.AddRange([E("input", "Image", "blobs", "Image"), E("blobs", "BlobResults", "adapt", "Blobs"), E("input", "Image", "adapt", "Image"),
                E("adapt", "Collection", "filter", "Collection"), E("filter", "Collection", "sort", "Collection"), E("sort", "Collection", "select", "Collection"),
                E("select", "Item", "roi", "Item"), E("roi", "ROI", "crop", "ROI"), E("input", "Image", "crop", "Image"), E("crop", "CroppedImage", "cropped", "Image"),
                E("sort", "Collection", "count", "Collection"), E("count", "Count", "text", "Integer"), E("sort", "Collection", "display", "Collection"), E("input", "Image", "display", "Image")]);
        }
        else if (lesson == 4)
        {
            var body = new SubflowDefinition
            {
                Nodes = [N("current", "IterationInput", "① 当前ROI / 索引 / 共享原图", new { }), N("roi", "ItemToRoi", "② 当前项转ROI", new ItemRoiOptions()),
                    N("blobs", "BlobDetection", "③ 只检测当前ROI", new BlobOptions()), N("judge", "Compare", "④ 目标数≥2为OK", new CompareOptions { Operator = CompareOperator.GreaterOrEqual, ValueB = 2 }),
                    N("index", "TextDisplay", "⑤ 本项索引转文字", new TextDisplayParameters { Prefix = "区域索引=" }), N("collect", "IterationOutput", "⑥ 收集本项文字/数量/判定/ROI", new IterationOutputOptions())],
                Connections = [E("current", "Item", "roi", "Item"), E("current", "Image", "blobs", "Image"), E("roi", "ROI", "blobs", "ROI"),
                    E("blobs", "BlobCount", "judge", "ValueA"), E("current", "Index", "index", "Integer"), E("index", "Text", "collect", "Text"),
                    E("blobs", "BlobCount", "collect", "Number"), E("judge", "Result", "collect", "IsOk"), E("roi", "ROI", "collect", "ROI")]
            };
            body = body with { NodeLocations = body.Nodes.Select((n, i) => (n, i)).ToDictionary(p => p.n.NodeId, p => new Point2D((p.i % 3) * 380, (p.i / 3) * 230)) };
            nodes.AddRange([N("regions", "RoiCollection", "② 三个待处理区域", new RoiCollectionOptions { Regions = Enumerable.Range(0, 3).Select(i => new RectRoi(10 + 130 * i, 30, 110, 130).ToRotated()).ToList() }),
                N("each", "ForEach", "③ 逐项循环：每个ROI检测一次", new ForEachOptions { Body = body, MaximumItems = 10, WorkingImageMegabytes = 16, DebugIterations = 3, DebugImageMegabytes = 4 }),
                N("display", "CollectionDisplay", "④ 查看3次循环的完整结果", new CollectionDisplayOptions { Fields = "Index,Text,Number,State", SummaryLines = 3, DecimalPlaces = 0 }),
                N("complete", "TextDisplay", "⑤ 是否完成整个集合", new TextDisplayParameters { Prefix = "循环全部完成：" })]);
            edges.AddRange([E("input", "Image", "regions", "Image"), E("regions", "Collection", "each", "Collection"), E("input", "Image", "each", "Image"),
                E("each", "Results", "display", "Collection"), E("input", "Image", "display", "Image"), E("each", "Complete", "complete", "Boolean")]);
        }
        else throw new ArgumentOutOfRangeException(nameof(lesson));
        return new() { ProjectName = Titles[lesson], Nodes = nodes, Connections = edges };
    }

    private static readonly string[] Titles = ["01 看懂图像、数字与布尔显示", "02 矩形裁切：只取需要的区域", "03 旋转裁切：把倾斜区域摆正", "04 集合：筛选、排序和选择一项", "05 逐项循环：三个ROI分别检测"];
    private static readonly string[] Flows = ["图片 → Blob检测 → 图像显示\n目标数量 → 文本显示；数量比较 → 布尔显示", "图片 → 矩形裁切 → 图像显示（同时保留原图显示）", "图片 → 旋转裁切 → 图像显示（同时保留原图显示）", "Blob → 集合 → 筛选 → 排序 → 选择第2项 → ROI → 裁切\n筛选集合 → 列表显示 / 数量显示", "ROI集合 + 共享原图 → 逐项子流程 → 结果集合 → 列表与叠加显示\n子流程：当前项 → ROI → Blob计数 → 比较 → 收集本项结果"];
    private static readonly string[] Goals = ["区分图像、整数、Boolean端口；知道节点执行成功和业务判断True是两回事。", "理解左上角X/Y、宽高和原图/裁切局部坐标；原图不会被裁切覆盖。", "理解旋转中心、顺时针角度和输出尺寸；输出局部坐标可映射回原图。", "理解集合与单项的区别；筛选保留来源，索引从0开始。", "理解当前Item、0基Index、显式共享Image与收集输出；学习查看每次迭代。"];
    private static readonly string[] Steps = [
        "1. 点击“一键运行”，软件会创建可写副本。\n2. 选“③ 看图像和目标框”，查看Image输出及3个目标框；点击图像输出端口可预览。\n3. 选“④ 看数字转文字”和“⑥ 看布尔结果”，查看节点摘要和右侧“执行结果”。\n4. 选“⑤ 数量是否等于3”，在参数面板查看比较目标值。图像端口不能直接连接文字端口。",
        "1. 一键运行后，分别选择“② 查看原图”和“④ 查看裁切后的新图”的Image输出。\n2. 选裁切节点，查看左上角(30,30)、宽80、高60；参数单位是原图像素。\n3. 点击CoordinateTransform输出，查看OffsetX=30、OffsetY=30；裁切图(0,0)映射回原图(30,30)。\n4. 图像窗口1:1/适应/平移只改变显示，不改变上述坐标。",
        "1. 一键运行，比较原图倾斜面板与裁切后的水平面板。\n2. 选旋转裁切节点：中心(160,120)、宽120、高60、角度30°（图像Y向下，正角顺时针）。\n3. 查看CroppedImage与CoordinateTransform：变换方向是裁切局部像素中心→原图。\n4. 本例通过参数给定位置，不需要模板或模型。已有ROI/Pose输入连接时会覆盖相应几何参数。",
        "1. 一键运行；原图有3个矩形和1个小点。\n2. 查看筛选参数Area≥100，再看排序参数X升序，小点被滤掉。\n3. 选“⑫ 看全部筛选结果”，打开“集合/逐项”面板，点击列表行以高亮并查看局部。\n4. 索引1是第二项，裁切预览应为30×20。选择单项不会改变原集合。\n5. 在集合显示参数中勾选字段、改变颜色/字号，再应用并重新运行。",
        "1. 一键运行，选“④ 查看3次循环的完整结果”，打开右侧“集合/逐项”。\n2. 三行依次是索引0/1/2，Number为1/2/3，状态NG/OK/OK；全部节点可以执行成功，同时某项业务NG。\n3. 选逐项节点，在参数中点击“编辑逐项子流程”。当前项是本次ROI，Index从0开始，Image是同一张共享原图。\n4. 子流程中Blob的ROI线必须来自当前项；IterationOutput明确收集文字、数量、判定和原图ROI。\n5. 运行后可在逐项调试下拉框选择某次迭代和中间节点/端口。只保留3次调试，切换/重跑后旧结果失效。"
    ];
    private static readonly string[] Exercises = ["把比较目标值3改成2，应用后重新运行：布尔显示应False，图像与目标数量仍为3。", "把X改成40：输出仍80×60，但局部图会右移。将宽度改为0会提示非法参数；超出原图边界会报错。请在副本中修改。", "把角度30改成0再运行，输出会保留倾斜；改回30可恢复。越界填充按参数执行，预览缩放不参与计算。", "把索引1改成0/2查看第1/3项；改成3会明确越界。把面积下限改为2000会得到空集合，选择节点报错而非沿用旧项。", "在子流程把比较目标值2改成3，保存子流程后重新运行，预期NG/NG/OK。删除一个ROI后只循环2次。空集合返回0项；执行异常按“停止/记录继续”策略处理，NG不是执行异常。循环有数量预算，不支持无限或嵌套循环。"];
    private static readonly string[] Summaries = ["目标数量：3；数量等于3：True；原图320×240且有3个目标框。", "裁切图80×60；局部(0,0)对应原图(30,30)，原图仍320×240。", "裁切图120×60；倾斜区域被摆正；坐标矩阵保留30°旋转。", "检测4项 → 筛选3项；索引1选中第二个矩形，裁切图30×20。", "完成3项，Complete=True；索引0/1/2分别返回Number=1/2/3、NG/OK/OK。NG项也完成执行。"];

    public static ExampleManifest Manifest(int lesson) => new()
    {
        Id = Ids[lesson], Name = Titles[lesson], Category = Category, Difficulty = lesson < 3 ? "入门 · 约3分钟" : "入门 · 约5分钟",
        Tags = ["新手", "离线", lesson == 4 ? "循环 ForEach" : lesson is 1 or 2 ? "裁切 Crop" : "显示 Display"],
        Description = Summaries[lesson], Flow = Flows[lesson], LearningGoals = Goals[lesson], Instructions = Steps[lesson], TryIt = Exercises[lesson], ExpectedSummary = Summaries[lesson],
        Verification = "一键运行会核对本次真实输出。下面技术预期用于复核；修改参数后应以新参数结果为准，不要求原示例固定预期仍通过。",
        Expected = lesson switch
        {
            0 => [C("blobs", "BlobCount", 3), C("count", "Text", "目标数量：3"), C("boolean", "Text", "数量等于3：True"), C("image", "Image", 320, "Width")],
            1 => [C("crop", "CroppedImage", 80, "Width"), C("crop", "CroppedImage", 60, "Height"), C("crop", "CoordinateTransform", 30d, "OffsetX"), C("crop", "CoordinateTransform", 30d, "OffsetY")],
            2 => [C("crop", "CroppedImage", 120, "Width"), C("crop", "CroppedImage", 60, "Height"), C("crop", "CoordinateTransform", Math.Cos(Math.PI / 6), "M11", 1e-12)],
            3 => [C("blobs", "BlobCount", 4), C("count", "Count", 3), C("text", "Text", "筛选后目标数：3"), C("crop", "CroppedImage", 30, "Width"), C("crop", "CroppedImage", 20, "Height")],
            _ => [C("each", "CompletedCount", 3), C("each", "Complete", true), C("complete", "Text", "循环全部完成：True")]
        }
    };

    public static async Task SaveAllAsync(string root, CancellationToken ct = default)
    {
        root = Path.GetFullPath(root);
        foreach (var id in Ids) if (Directory.Exists(Path.Combine(root, id))) throw new IOException("示例目录已存在，拒绝覆盖：" + id);
        var store = new ProjectStore(BuiltinRegistration.CreateRegistry());
        for (var lesson = 0; lesson < Ids.Length; lesson++)
        {
            ct.ThrowIfCancellationRequested(); var directory = Path.Combine(root, Ids[lesson]); Directory.CreateDirectory(directory);
            using var input = new Mat(lesson == 4 ? 200 : 240, lesson == 4 ? 400 : 320, MatType.CV_8UC1, Scalar.All(20));
            if (lesson == 4)
            {
                for (var i = 0; i < 3; i++) for (var j = 0; j <= i; j++) Cv2.Rectangle(input, new Rect(30 + 130 * i, 45 + j * 35, 55, 20), Scalar.All(220), -1);
            }
            else if (lesson == 2)
            {
                var corners = new RotatedRect(new Point2f(160, 120), new Size2f(120, 60), 30).Points().Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray();
                Cv2.FillConvexPoly(input, corners, Scalar.All(220));
                Cv2.Circle(input, new Point(143, 110), 7, Scalar.All(40), -1);
            }
            else
            {
                Cv2.Rectangle(input, new Rect(40, 40, lesson == 3 ? 20 : 40, 30), Scalar.All(220), -1);
                Cv2.Rectangle(input, new Rect(140, 70, lesson == 3 ? 30 : 50, lesson == 3 ? 20 : 40), Scalar.All(220), -1);
                Cv2.Rectangle(input, new Rect(240, 140, lesson == 3 ? 40 : 30, 40), Scalar.All(220), -1);
                if (lesson == 3) Cv2.Rectangle(input, new Rect(290, 35, 3, 3), Scalar.All(220), -1);
            }
            Cv2.ImWrite(Path.Combine(directory, "input.png"), input);
            using var preview = new Mat(); Cv2.CvtColor(input, preview, ColorConversionCodes.GRAY2BGR);
            if (lesson == 1) Cv2.Rectangle(preview, new Rect(30, 30, 80, 60), new Scalar(0, 170, 255), 2);
            if (lesson == 4) for (var i = 0; i < 3; i++) { Cv2.Rectangle(preview, new Rect(10 + 130 * i, 30, 110, 130), new Scalar(0, 170, 255), 2); Cv2.PutText(preview, "ROI " + i, new Point(15 + 130 * i, 22), HersheyFonts.HersheySimplex, .5, new Scalar(0, 170, 255)); }
            Cv2.ImWrite(Path.Combine(directory, "preview.png"), preview);
            await store.SaveAsync(Path.Combine(directory, "project.json"), Create(lesson), ct);
            var m = Manifest(lesson);
            await File.WriteAllTextAsync(Path.Combine(directory, "README.md"), $"# {m.Name}\n\n## 学习目标\n{m.LearningGoals}\n\n## 流程\n{m.Flow}\n\n## 跟着操作\n{m.Instructions}\n\n## 应看到什么\n{m.ExpectedSummary}\n\n## 动手试一试\n{m.TryIt}\n\n所有图片由OpenCV绘制，是自制合成资源。预览上的彩色框是位置示意，不是检测结果；实际输出由原Runtime计算。打开/运行会复制到用户可写工程，内置示例保持只读。修改后在编辑器按F5运行当前副本；回到示例中心再次一键运行会创建新的初始副本。保存后可关闭重开。无需Python、YOLO权重、相机或网络；不含无限/嵌套循环，不代表产线精度认证。\n", ct);
            foreach (var file in Directory.EnumerateFiles(directory)) m.Files[Path.GetFileName(file)] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file, ct)));
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(m, JsonDefaults.Options), ct);
        }
    }
}
