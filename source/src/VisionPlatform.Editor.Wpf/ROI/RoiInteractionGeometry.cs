using VisionPlatform.Contracts.Geometry;

namespace VisionPlatform.Editor.Wpf.ROI;

public static class RoiInteractionGeometry
{
    public static bool Contains(RotatedRectRoi roi, Point2D point)
    {
        var radians = roi.AngleDegrees * Math.PI / 180; var c = Math.Cos(radians); var s = Math.Sin(radians);
        var x = point.X - roi.CenterX; var y = point.Y - roi.CenterY;
        return Math.Abs(c * x + s * y) <= roi.Width / 2 && Math.Abs(-s * x + c * y) <= roi.Height / 2;
    }
    public static RotatedRectRoi Move(RotatedRectRoi roi, double dx, double dy) => roi with { CenterX = roi.CenterX + dx, CenterY = roi.CenterY + dy };
    public static RotatedRectRoi ResizeCorner(RotatedRectRoi roi, int corner, Point2D point)
    {
        if (corner is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(corner));
        var anchor = roi.Corners()[(corner + 2) % 4];
        var radians = roi.AngleDegrees * Math.PI / 180; var c = Math.Cos(radians); var s = Math.Sin(radians);
        var dx = point.X - anchor.X; var dy = point.Y - anchor.Y;
        var xSign = corner is 0 or 3 ? -1 : 1; var ySign = corner is 0 or 1 ? -1 : 1;
        var width = Math.Max(2, xSign * (c * dx + s * dy));
        var height = Math.Max(2, ySign * (-s * dx + c * dy));
        var u = xSign * width / 2; var v = ySign * height / 2;
        return roi with { CenterX = anchor.X + c * u - s * v, CenterY = anchor.Y + s * u + c * v, Width = width, Height = height };
    }
    public static RotatedRectRoi RotateTo(RotatedRectRoi roi, Point2D point)
    {
        var angle = Math.Atan2(point.Y - roi.CenterY, point.X - roi.CenterX) * 180 / Math.PI + 90;
        if (angle > 180) angle -= 360;
        return roi with { AngleDegrees = angle };
    }
    public static bool IsInside(RotatedRectRoi roi, int width, int height, ImageCoordinateSpace coordinateSpace)
    {
        try { roi.Validate(); } catch (ArgumentException) { return false; }
        return roi.CoordinateSpaceId == coordinateSpace.Id && roi.Corners().All(p => p.X >= coordinateSpace.OriginX &&
            p.Y >= coordinateSpace.OriginY && p.X <= coordinateSpace.OriginX + width && p.Y <= coordinateSpace.OriginY + height);
    }
}
