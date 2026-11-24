using VisionPlatform.Contracts.Numerics;

namespace VisionPlatform.Contracts.Geometry;

/// <summary>Forward homogeneous mapping of original pixel-center indices, x right/y down.
/// This remains a projective type even when its last row represents an affine transform.</summary>
public sealed class ProjectiveTransform2D
{
    public NumericMatrix Matrix { get; }
    public string SourceSpaceId { get; }
    public string TargetSpaceId { get; }
    public bool IsAffine => Matrix[2, 0] == 0 && Matrix[2, 1] == 0;

    public ProjectiveTransform2D(NumericMatrix matrix, string sourceSpaceId = "image", string targetSpaceId = "warped")
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (matrix.Rows != 3 || matrix.Columns != 3) throw new ArgumentException("透视坐标变换需要3×3矩阵。");
        if (string.IsNullOrWhiteSpace(sourceSpaceId) || string.IsNullOrWhiteSpace(targetSpaceId) || sourceSpaceId.Length > 256 || targetSpaceId.Length > 256)
            throw new ArgumentException("源和目标坐标空间必须明确，最长256字符。");
        var norm = matrix.Values.Max(Math.Abs);
        if (norm == 0) throw new ArgumentException("变换矩阵不能全为0。");
        var scale = Math.Abs(matrix[2, 2]) > norm * 1e-14 ? matrix[2, 2] : norm;
        Matrix = new(3, 3, matrix.Values.Select(v => v / scale)); SourceSpaceId = sourceSpaceId; TargetSpaceId = targetSpaceId;
        Invert(Matrix); // Validate invertibility without calling another transform constructor.
    }

    public Point2D Map(Point2D point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) throw new ArgumentException("点坐标必须为有限数值。");
        var denominator = Denominator(point);
        var magnitude = Math.Abs(Matrix[2, 0] * point.X) + Math.Abs(Matrix[2, 1] * point.Y) + Math.Abs(Matrix[2, 2]);
        if (!double.IsFinite(denominator) || Math.Abs(denominator) <= Math.Max(1e-15, magnitude * 1e-12))
            throw new ArgumentException("点位于透视无穷远或过于接近投影地平线，不能映射为有限像素。");
        var x = (Matrix[0, 0] * point.X + Matrix[0, 1] * point.Y + Matrix[0, 2]) / denominator;
        var y = (Matrix[1, 0] * point.X + Matrix[1, 1] * point.Y + Matrix[1, 2]) / denominator;
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException("透视映射发生数值溢出。");
        return new(x, y);
    }

    public double Denominator(Point2D point) => Matrix[2, 0] * point.X + Matrix[2, 1] * point.Y + Matrix[2, 2];
    public ProjectiveTransform2D Inverse() => new(new(3, 3, Invert(Matrix)), TargetSpaceId, SourceSpaceId);
    public static ProjectiveTransform2D FromAffine(CoordinateTransform2D affine) => new(new(3, 3,
        [affine.M11, affine.M12, affine.OffsetX, affine.M21, affine.M22, affine.OffsetY, 0, 0, 1]), affine.SourceSpaceId, affine.TargetSpaceId);
    public CoordinateTransform2D ToAffine()
    {
        if (!IsAffine || Math.Abs(Matrix[2, 2]) < 1e-15) throw new InvalidOperationException("真实透视矩阵不能伪装为仿射CoordinateTransform2D。");
        var w = Matrix[2, 2];
        return new(Matrix[0, 0] / w, Matrix[0, 1] / w, Matrix[1, 0] / w, Matrix[1, 1] / w,
            Matrix[0, 2] / w, Matrix[1, 2] / w, SourceSpaceId, TargetSpaceId);
    }
    public override string ToString() => $"{(IsAffine ? "仿射" : "透视")} {SourceSpaceId} → {TargetSpaceId}（像素中心，正向3×3）";

    private static double[] Invert(NumericMatrix matrix)
    {
        var a = new double[3, 6]; var rowScale = new double[3];
        for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++) { a[r, c] = matrix[r, c]; rowScale[r] = Math.Max(rowScale[r], Math.Abs(a[r, c])); a[r, c + 3] = r == c ? 1 : 0; }
        if (rowScale.Any(s => s == 0)) throw new ArgumentException("变换矩阵奇异，不能求逆。");
        for (var col = 0; col < 3; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < 3; r++) if (Math.Abs(a[r, col]) / rowScale[r] > Math.Abs(a[pivot, col]) / rowScale[pivot]) pivot = r;
            if (Math.Abs(a[pivot, col]) <= 1e-14 * rowScale[pivot]) throw new ArgumentException("变换矩阵奇异或数值退化，不能求逆。");
            if (pivot != col) { for (var c = 0; c < 6; c++) (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]); (rowScale[col], rowScale[pivot]) = (rowScale[pivot], rowScale[col]); }
            var divisor = a[col, col]; for (var c = 0; c < 6; c++) a[col, c] /= divisor;
            for (var r = 0; r < 3; r++) if (r != col) { var factor = a[r, col]; for (var c = 0; c < 6; c++) a[r, c] -= factor * a[col, c]; }
        }
        var output = new double[9]; for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++) output[r * 3 + c] = a[r, c + 3];
        if (output.Any(x => !double.IsFinite(x))) throw new ArgumentException("矩阵求逆发生数值溢出。");
        return output;
    }
}
