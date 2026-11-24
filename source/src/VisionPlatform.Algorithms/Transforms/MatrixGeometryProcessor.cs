using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using OpenCvSharp;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Numerics;
using VisionPlatform.Algorithms.Recognition;
using VisionPlatform.Imaging;

namespace VisionPlatform.Algorithms.Transforms;

public enum MatrixOperation
{
    [Display(Name="矩阵乘法",Description="A列数必须等于B行数，计算A×B。")]
    Multiply,
    [Display(Name="转置",Description="交换A的行列，仅需要A，B不参与。")]
    Transpose,
    [Display(Name="求逆",Description="A须为可逆方阵，最多128阶；拒绝奇异或病态矩阵。")]
    Inverse
}
public enum GeometricTransformKind
{
    [Display(Name="仿射（2×3）",Description="平移、旋转、缩放和剪切；矩形可变为平行四边形。")]
    Affine,
    [Display(Name="透视（3×3）",Description="齐次坐标映射；不是相机标定或自动配准。")]
    Perspective
}
public sealed record MatrixCreateOptions
{
    [DisplayName("行数"), Range(1, 1000)] public int Rows { get; init; } = 2;
    [DisplayName("列数"), Range(1, 1000)] public int Columns { get; init; } = 2;
    [DisplayName("矩阵元素（逐行）"), Description("每行数字用空格或逗号分隔；换行或分号分行。Float64，最多1000000元素；不执行表达式。")] public string ValuesText { get; init; } = "1, 0; 0, 1";
}
public sealed record MatrixMathOptions { [DisplayName("矩阵运算")] public MatrixOperation Operation { get; init; } }
public sealed record MatrixElementOptions
{
    [DisplayName("行索引（0基）"), Range(0, 999)] public int Row { get; init; }
    [DisplayName("列索引（0基）"), Range(0, 999)] public int Column { get; init; }
}
public sealed record TransformCreateOptions
{
    [DisplayName("变换类型")] public GeometricTransformKind Kind { get; init; }
    [DisplayName("源坐标空间")] public string SourceSpaceId { get; init; } = "image";
    [DisplayName("目标坐标空间")] public string TargetSpaceId { get; init; } = "warped";
    [DisplayName("矩阵系数（逐行）"), Description("仿射2×3；透视3×3。定义源像素中心→目标像素中心，不是逆采样矩阵。连接Matrix或Affine端口时使用端口值。")] public string MatrixText { get; init; } = "1, 0, 0; 0, 1, 0";
    [DisplayName("显式求逆"), Description("求逆会交换源/目标坐标空间。适用于既有裁切的‘裁切→原图’映射转换为‘原图→裁切’。")] public bool Invert { get; init; }
}
public sealed record EstimateTransformOptions
{
    [DisplayName("求解类型")] public GeometricTransformKind Kind { get; init; }
    [DisplayName("源坐标空间")] public string SourceSpaceId { get; init; } = "image";
    [DisplayName("目标坐标空间")] public string TargetSpaceId { get; init; } = "warped";
    [DisplayName("源对应点（每行X,Y）"), Description("仿射至少3对，透视至少4对；点按明确行顺序一一对应，最多1000对。不是自动特征匹配或相机标定。")] public string SourcePointsText { get; init; } = "0,0; 100,0; 0,100";
    [DisplayName("目标对应点（每行X,Y）")] public string TargetPointsText { get; init; } = "10,20; 110,20; 10,120";
}
public sealed record WarpImageOptions
{
    [DisplayName("输出宽度（像素）"), Range(1, 32766)] public int Width { get; init; } = 640;
    [DisplayName("输出高度（像素）"), Range(1, 32766)] public int Height { get; init; } = 480;
    [DisplayName("插值方式"), Description("仿射支持Nearest/Linear/Cubic/Lanczos4；透视首版仅Nearest/Linear，明确拒绝其他值。")] public CropInterpolation Interpolation { get; init; } = CropInterpolation.Linear;
    [DisplayName("边界模式"), Description("透视首版仅Constant/Replicate；仿射另支持Reflect/Reflect101/Wrap。输入区域之外明确填充，不裁掉ROI身份。")] public CropBorderMode BorderMode { get; init; } = CropBorderMode.Constant;
    [DisplayName("填充值（各通道相同）"), Range(0, 255)] public byte BorderValue { get; init; }
    [DisplayName("输入/输出各自像素预算"), Range(1, 100000000), Description("默认1600万；大图先连接ROI。预算分别约束输入视图与新输出，不复制整幅原图。")] public int MaximumPixels { get; init; } = 16_000_000;
}
public sealed record MapGeometryOptions
{
    [DisplayName("未接输入时的X（像素中心）")] public double X { get; init; }
    [DisplayName("未接输入时的Y（像素中心）")] public double Y { get; init; }
    [DisplayName("参数点的源坐标空间")] public string CoordinateSpaceId { get; init; } = "image";
}
public sealed record TransformEstimate(ProjectiveTransform2D Transform, double RootMeanSquareError, int PairCount);
public sealed record WarpImageResult(ImageFrame Image, ProjectiveTransform2D Transform, ProjectiveTransform2D InverseTransform);

public static class MatrixGeometryProcessor
{
    public static NumericMatrix Parse(int rows, int columns, string text)
    {
        if (rows < 1 || columns < 1 || (long)rows * columns > NumericMatrix.MaximumElements || text is null || text.Length > 32_000_000)
            throw new ArgumentException("矩阵行列或元素文本预算无效。");
        var lines = text.Split([';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != rows) throw new ArgumentException($"矩阵要求{rows}行，实际{lines.Length}行；用换行或分号分行。");
        var values = new List<double>(rows * columns);
        foreach (var line in lines)
        {
            var fields = line.Split([',', ' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length != columns) throw new ArgumentException($"每行应有{columns}个数值。");
            foreach (var field in fields) if (double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)) values.Add(value); else throw new ArgumentException("矩阵只接受有限十进制数字，不执行表达式。");
        }
        return new(rows, columns, values);
    }
    public static NumericMatrix Math(NumericMatrix a, NumericMatrix? b, MatrixOperation operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(a); ct.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(operation)) throw new ArgumentException("未知矩阵运算。");
        using var am = MatOf(a); using var output = new Mat();
        switch (operation)
        {
            case MatrixOperation.Multiply:
                if (b is null || a.Columns != b.Rows) throw new ArgumentException("矩阵乘法要求A列数等于B行数，并连接B。");
                if ((long)a.Rows * b.Columns > NumericMatrix.MaximumElements || (long)a.Rows * a.Columns * b.Columns > 100_000_000) throw new ArgumentException("矩阵乘法超过1000000输出元素或1亿乘加预算。");
                using (var bm = MatOf(b)) { using var empty = new Mat(); Cv2.Gemm(am, bm, 1, empty, 0, output); }
                break;
            case MatrixOperation.Transpose: Cv2.Transpose(am, output); break;
            case MatrixOperation.Inverse:
                if (a.Rows != a.Columns || a.Rows > 128) throw new ArgumentException("求逆要求方阵，最多128×128。");
                var inverseCondition = Cv2.Invert(am, output, DecompTypes.SVD);
                if (!double.IsFinite(inverseCondition) || inverseCondition < 1e-12) throw new ArgumentException("矩阵奇异或病态（倒条件数<1e-12），拒绝伪逆冒充可逆结果。");
                break;
        }
        ct.ThrowIfCancellationRequested(); return FromMat(output);
    }
    public static ProjectiveTransform2D Create(NumericMatrix matrix, GeometricTransformKind kind, string source, string target)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentException("未知几何变换类型。");
        if (kind == GeometricTransformKind.Affine)
        {
            if (matrix.Rows != 2 || matrix.Columns != 3) throw new ArgumentException("仿射矩阵必须为2×3。");
            return new(new(3, 3, matrix.Values.Concat(new double[] { 0, 0, 1 })), source, target);
        }
        return new(matrix, source, target);
    }
    public static EdgePointSet ParsePoints(string text, string space)
    {
        if (text is null || text.Length > 100000) throw new ArgumentException("对应点文本超出100000字符预算。");
        var rows = text.Split([';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length;
        if (rows is < 1 or > 1000) throw new ArgumentException("对应点数量必须在1..1000。");
        var m = Parse(rows, 2, text); return new(Array.AsReadOnly(Enumerable.Range(0, rows).Select(i => new Point2D(m[i, 0], m[i, 1])).ToArray()), space);
    }
    public static TransformEstimate Estimate(EdgePointSet source, EdgePointSet target, GeometricTransformKind kind, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (!Enum.IsDefined(kind)) throw new ArgumentException("未知变换类型。");
        var count = source.Points.Count; var minimum = kind == GeometricTransformKind.Affine ? 3 : 4;
        if (count != target.Points.Count || count < minimum || count > 1000) throw new ArgumentException($"源/目标对应点数量必须相等，{kind}至少{minimum}对，最多1000对。");
        ValidatePoints(source.Points); ValidatePoints(target.Points);
        if (!NonCollinear(source.Points) || !NonCollinear(target.Points)) throw new ArgumentException("源或目标对应点共线/退化，无法确定二维变换。");
        NumericMatrix matrix;
        if (kind == GeometricTransformKind.Affine)
        {
            using var design = new Mat(count * 2, 6, MatType.CV_64FC1, Scalar.All(0)); using var values = new Mat(count * 2, 1, MatType.CV_64FC1); using var solution = new Mat();
            for (var i = 0; i < count; i++)
            { var s = source.Points[i]; var t = target.Points[i]; design.Set(i * 2, 0, s.X); design.Set(i * 2, 1, s.Y); design.Set(i * 2, 2, 1d); design.Set(i * 2 + 1, 3, s.X); design.Set(i * 2 + 1, 4, s.Y); design.Set(i * 2 + 1, 5, 1d); values.Set(i * 2, 0, t.X); values.Set(i * 2 + 1, 0, t.Y); }
            if (!Cv2.Solve(design, values, solution, DecompTypes.SVD)) throw new ArgumentException("仿射对应点求解失败。");
            matrix = new(2, 3, Enumerable.Range(0, 6).Select(i => solution.At<double>(i, 0)));
        }
        else
        {
            ValidateProjectiveRank(source.Points, target.Points);
            using var homography = Cv2.FindHomography(source.Points.Select(p => new Point2d(p.X, p.Y)), target.Points.Select(p => new Point2d(p.X, p.Y)), HomographyMethods.None);
            if (homography.Empty()) throw new ArgumentException("透视对应点退化，未得到有效矩阵。"); matrix = FromMat(homography);
        }
        var transform = Create(matrix, kind, source.CoordinateSpaceId, target.CoordinateSpaceId); var sum = 0d;
        for (var i = 0; i < count; i++) { var mapped = transform.Map(source.Points[i]); sum += System.Math.Pow(mapped.X - target.Points[i].X, 2) + System.Math.Pow(mapped.Y - target.Points[i].Y, 2); }
        ct.ThrowIfCancellationRequested(); return new(transform, System.Math.Sqrt(sum / count), count);
    }
    public static WarpImageResult Warp(ImageFrame image, ProjectiveTransform2D transform, WarpImageOptions options, RotatedRectRoi? roi = null, CancellationToken ct = default)
    {
        TransformFrame.ValidateInput(image); if (transform.SourceSpaceId != image.CoordinateSpace.Id) throw new ArgumentException("图像与变换的源坐标空间不一致。");
        if (transform.SourceSpaceId == transform.TargetSpaceId) throw new ArgumentException("变换输出必须使用独立目标坐标空间，不能沿用源空间掩盖坐标变化。");
        if (!Enum.IsDefined(options.Interpolation) || !Enum.IsDefined(options.BorderMode) || options.MaximumPixels is < 1 or > 100_000_000 || (long)options.Width * options.Height > options.MaximumPixels)
            throw new ArgumentException("变换插值/边界/像素预算无效；大图请显式设置ROI和输出尺寸。");
        TransformFrame.ValidateDimensions(options.Width, options.Height, image.PixelFormat);
        if (!transform.IsAffine && (options.Interpolation is not (CropInterpolation.Nearest or CropInterpolation.Linear) || options.BorderMode is not (CropBorderMode.Constant or CropBorderMode.Replicate)))
            throw new ArgumentException("透视首版支持Nearest/Linear与Constant/Replicate；不静默替换插值或边界模式。");
        using var view = RecognitionRegion.Open(image, roi, options.MaximumPixels); ct.ThrowIfCancellationRequested();
        if (view.Width > 32766 || view.Height > 32766) throw new ArgumentException("OpenCV几何重采样输入每轴最多32766像素，请先显式指定ROI。");
        var inverse = transform.Inverse(); ValidateDomain(transform, new(view.CoordinateSpace.OriginX, view.CoordinateSpace.OriginY, view.Width - 1, view.Height - 1)); ValidateDomain(inverse, new(0, 0, options.Width - 1, options.Height - 1));
        var h = transform.Matrix; var local = new double[9];
        for (var row = 0; row < 3; row++) { local[row * 3] = h[row, 0]; local[row * 3 + 1] = h[row, 1]; local[row * 3 + 2] = h[row, 0] * view.CoordinateSpace.OriginX + h[row, 1] * view.CoordinateSpace.OriginY + h[row, 2]; }
        using var source = new OpenCvImageLease(view); using var builder = new ImageFrameBuilder(options.Width, options.Height, image.PixelFormat, coordinateSpace: new(transform.TargetSpaceId)); using var output = TransformFrame.Target(builder);
        var interpolation = options.Interpolation switch { CropInterpolation.Nearest => InterpolationFlags.Nearest, CropInterpolation.Linear => InterpolationFlags.Linear, CropInterpolation.Cubic => InterpolationFlags.Cubic, _ => InterpolationFlags.Lanczos4 };
        var border = options.BorderMode switch { CropBorderMode.Constant => BorderTypes.Constant, CropBorderMode.Replicate => BorderTypes.Replicate, CropBorderMode.Reflect => BorderTypes.Reflect, CropBorderMode.Reflect101 => BorderTypes.Reflect101, _ => BorderTypes.Wrap };
        if (transform.IsAffine)
        { using var matrix = MatOf(new(2, 3, local.Take(6).Select(v => v / local[8]))); Cv2.WarpAffine(source.Mat, output, matrix, new(options.Width, options.Height), interpolation, border, Scalar.All(options.BorderValue)); }
        else
        { using var matrix = MatOf(new(3, 3, local)); Cv2.WarpPerspective(source.Mat, output, matrix, new(options.Width, options.Height), interpolation, border, Scalar.All(options.BorderValue)); }
        ct.ThrowIfCancellationRequested(); return new(builder.Build(image.FrameId, image.Timestamp), transform, inverse);
    }
    public static EdgePointSet Map(EdgePointSet points, ProjectiveTransform2D transform, CancellationToken ct = default)
    {
        if (points.CoordinateSpaceId != transform.SourceSpaceId) throw new ArgumentException("点集和变换源坐标空间不一致。"); if (points.Points.Count > 100000) throw new ArgumentException("点集超过100000点预算。");
        var output = new Point2D[points.Points.Count]; for (var i = 0; i < output.Length; i++) { if ((i & 255) == 0) ct.ThrowIfCancellationRequested(); output[i] = transform.Map(points.Points[i]); } return new(Array.AsReadOnly(output), transform.TargetSpaceId);
    }
    public static EdgePointSet MapRectangle(RotatedRectRoi roi, ProjectiveTransform2D transform, CancellationToken ct = default)
    { roi.Validate(); if (roi.CoordinateSpaceId != transform.SourceSpaceId) throw new ArgumentException("ROI和变换源坐标空间不一致。"); var corners = roi.Corners(); ValidateConnectedDomain(transform, corners); return Map(new(corners, roi.CoordinateSpaceId), transform, ct); }
    public static void ValidateConnectedDomain(ProjectiveTransform2D transform, IReadOnlyList<Point2D> corners)
    { if (corners.Count == 0) return; foreach (var p in corners) transform.Map(p); var signs = corners.Select(p => System.Math.Sign(transform.Denominator(p))).Distinct().ToArray(); if (signs.Length != 1) throw new ArgumentException("几何区域穿过投影地平线，不会用有限四边形冒充无界区域。"); }
    private static void ValidateDomain(ProjectiveTransform2D transform, RectRoi rectangle) => ValidateConnectedDomain(transform, [new(rectangle.X, rectangle.Y), new(rectangle.X + rectangle.Width, rectangle.Y), new(rectangle.X + rectangle.Width, rectangle.Y + rectangle.Height), new(rectangle.X, rectangle.Y + rectangle.Height)]);
    private static void ValidatePoints(IReadOnlyList<Point2D> points)
    { if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || System.Math.Abs(p.X) > 1e9 || System.Math.Abs(p.Y) > 1e9) || points.Distinct().Count() != points.Count) throw new ArgumentException("对应点包含重复点、非有限坐标或超出±1e9的数值。"); }
    private static bool NonCollinear(IReadOnlyList<Point2D> points)
    { var a = points[0]; var b = points.OrderByDescending(p => System.Math.Pow(p.X - a.X, 2) + System.Math.Pow(p.Y - a.Y, 2)).First(); var scale = System.Math.Max(1, System.Math.Pow(b.X - a.X, 2) + System.Math.Pow(b.Y - a.Y, 2)); return points.Any(c => System.Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) > scale * 1e-12); }
    private static void ValidateProjectiveRank(IReadOnlyList<Point2D> source, IReadOnlyList<Point2D> target)
    {
        // Four pairs with three collinear points do not determine a homography, even when
        // OpenCV happens to return a finite invertible matrix. Check the normalized DLT rank.
        static Point2D[] Normalize(IReadOnlyList<Point2D> points)
        { var x=points.Average(p=>p.X);var y=points.Average(p=>p.Y);var scale=System.Math.Sqrt(points.Average(p=>(p.X-x)*(p.X-x)+(p.Y-y)*(p.Y-y))/2);return points.Select(p=>new Point2D((p.X-x)/scale,(p.Y-y)/scale)).ToArray(); }
        var s=Normalize(source);var t=Normalize(target);
        using var design=new Mat(source.Count*2,9,MatType.CV_64FC1,Scalar.All(0));
        for(var i=0;i<s.Length;i++)
        {
            var x=s[i].X;var y=s[i].Y;var u=t[i].X;var v=t[i].Y;
            design.Set(2*i,0,x);design.Set(2*i,1,y);design.Set(2*i,2,1d);design.Set(2*i,6,-u*x);design.Set(2*i,7,-u*y);design.Set(2*i,8,-u);
            design.Set(2*i+1,3,x);design.Set(2*i+1,4,y);design.Set(2*i+1,5,1d);design.Set(2*i+1,6,-v*x);design.Set(2*i+1,7,-v*y);design.Set(2*i+1,8,-v);
        }
        using var singular=new Mat();using var left=new Mat();using var right=new Mat();Cv2.SVDecomp(design,singular,left,right,SVD.Flags.NoUV);
        if(singular.Rows<8||singular.At<double>(7,0)<=singular.At<double>(0,0)*1e-10)throw new ArgumentException("对应点不能确定唯一透视变换：归一化设计矩阵秩不足8或数值病态（四点中三点共线等）。");
    }
    internal static Mat MatOf(NumericMatrix matrix)
    { var mat = new Mat(matrix.Rows, matrix.Columns, MatType.CV_64FC1); for (var r = 0; r < matrix.Rows; r++) for (var c = 0; c < matrix.Columns; c++) mat.Set(r, c, matrix[r, c]); return mat; }
    internal static NumericMatrix FromMat(Mat mat)
    { var rows = mat.Rows; var columns = mat.Cols; var values = new double[checked(rows * columns)]; for (var r = 0; r < rows; r++) for (var c = 0; c < columns; c++) values[r * columns + c] = mat.At<double>(r, c); return new(rows, columns, values); }
}
