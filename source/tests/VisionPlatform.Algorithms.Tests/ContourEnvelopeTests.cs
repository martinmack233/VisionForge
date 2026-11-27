using OpenCvSharp;
using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;

public sealed class ContourEnvelopeTests
{
    [Fact]
    public void RectanglesAndEnclosingCircleRetainOriginAndCoverEveryPoint()
    {
        var origin = new ItemOrigin(Guid.NewGuid(), "fixture", 17, Guid.NewGuid(), "source", "observation-17");
        var known = new RotatedRectRoi(50040, 70030, 42, 18, 27, "source");
        var contour = new ContourItem(origin, known.Corners(), true);
        var input = new VisionCollection(VisionItemKind.Contour, [contour]);
        var legacy = Assert.IsType<ContourItem>(Assert.Single(ShapeProcessors.Analyze(input, new(), default).Items));
        var axis = Result(input, ContourEnvelopeMode.AxisAlignedRectangle);
        var bounds = axis.AxisAlignedBounds!;
        Assert.Equal(legacy.Analysis!.AxisAlignedBounds, bounds);
        Assert.Equal(axis.Rectangle, bounds.ToRotated("source"));
        Assert.All(contour.Points, p =>
        {
            Assert.InRange(p.X, bounds.X, bounds.X + bounds.Width);
            Assert.InRange(p.Y, bounds.Y, bounds.Y + bounds.Height);
        });
        var rotated = Result(input, ContourEnvelopeMode.MinimumAreaRectangle);
        Assert.Equal(legacy.Region, rotated.Rectangle);
        Assert.Equal(4, rotated.Corners.Count);
        Assert.InRange(rotated.Rectangle!.Width, 41.999, 42.001);
        Assert.InRange(rotated.Rectangle.Height, 17.999, 18.001);
        Assert.InRange(rotated.Rectangle.AngleDegrees, 26.999, 27.001);
        Assert.All(known.Corners(), p => Assert.Contains(rotated.Corners, q => double.Hypot(p.X - q.X, p.Y - q.Y) < .001));
        var external = Result(input, ContourEnvelopeMode.MinimumEnclosingCircle);
        Assert.All(contour.Points, p => Assert.True(double.Hypot(p.X - external.Circle!.Center.X, p.Y - external.Circle.Center.Y) <= external.Circle.Radius));
        Assert.Equal(double.Hypot(21, 9), external.Circle!.Radius, 4);
        Assert.Equal(external.Circle.Radius * 2, external.Fields["Diameter"].Number);
        Assert.All(new[] { axis, rotated, external }, item => Assert.Equal(origin, item.Origin));
    }

    [Fact]
    public void InscribedCircleUsesTrueConcaveAndHollowMasksAfterCollectionReordering()
    {
        using var build = new ImageFrameBuilder(220, 120, PixelFormat.Mono8, coordinateSpace: new("raw", 101, 815));
        using var draw = Mat.FromPixelData(120, 220, MatType.CV_8UC1, build.DataPointer, build.Stride);
        Cv2.Rectangle(draw, new Rect(8, 8, 42, 8), Scalar.All(255), -1);
        Cv2.Rectangle(draw, new Rect(8, 8, 12, 51), Scalar.All(255), -1);
        Cv2.Circle(draw, new Point(110, 55), 30, Scalar.All(255), -1);
        Cv2.Circle(draw, new Point(110, 55), 17, Scalar.All(0), -1);
        Cv2.Circle(draw, new Point(110, 55), 3, Scalar.All(255), -1); // disconnected island inside the hole
        Cv2.Rectangle(draw, new Rect(168, 15, 35, 31), Scalar.All(255), -1);
        using var image = build.Build();
        var extracted = Extract(image);
        Assert.Equal(5, extracted.Count);
        var ring = extracted.Items.Cast<ContourItem>().Single(i => i.IsHole == false && i.Points.Min(p => p.X) == 181);
        var selected = extracted.With(extracted.Items.Where(i => ((ContourItem)i).IsHole == false).OrderByDescending(i => i.Origin.SourceIndex));
        var results = ContourEnvelopeProcessor.Process(selected, new() { Mode = ContourEnvelopeMode.MaximumInscribedCircle }, image);
        Assert.Equal(selected.Items.Select(i => i.Origin), results.Items.Select(i => i.Origin));
        Assert.Equal(4, results.Count);
        using var pixels = image.Lease();
        foreach (var item in results.Items.Cast<ContourEnvelopeItem>())
        {
            Assert.Equal(ContourEnvelopeStatus.Valid, item.Status);
            var circle = item.Circle!;
            var cx = circle.Center.X - 101;
            var cy = circle.Center.Y - 815;
            Assert.Equal(255, pixels.GetRowSpan((int)cy)[(int)cx]);
            for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                if (pixels.GetRowSpan(y)[x] != 0) continue;
                var dx = Math.Max(0, Math.Abs(cx - x) - 0.5);
                var dy = Math.Max(0, Math.Abs(cy - y) - 0.5);
                Assert.True(double.Hypot(dx, dy) >= circle.Radius - 1e-9);
            }
        }
        var inscribedRing = results.Items.Cast<ContourEnvelopeItem>().Single(i => i.Origin == ring.Origin).Circle!;
        Assert.InRange(inscribedRing.Radius, 5.5, 7.5);
        Assert.InRange(double.Hypot(inscribedRing.Center.X - 211, inscribedRing.Center.Y - 870), 20, 27);
        var inscribedRectangle = results.Items.Cast<ContourEnvelopeItem>().Single(i => i.Circle!.Center.X > 269);
        Assert.Equal(15.5, inscribedRectangle.Circle!.Radius, 10);
        var hole = Result(extracted.With(extracted.Items.Where(i => ((ContourItem)i).IsHole == true)), ContourEnvelopeMode.MaximumInscribedCircle, image);
        Assert.Equal(ContourEnvelopeStatus.HoleBoundary, hole.Status);
        Assert.Null(hole.Circle);
    }

    [Fact]
    public void RejectsMissingProvenanceDifferentAllocationTruncationAndPerObjectBudget()
    {
        using var builder = new ImageFrameBuilder(120, 100, PixelFormat.Mono8);
        using var mat = Mat.FromPixelData(100, 120, MatType.CV_8UC1, builder.DataPointer, builder.Stride);
        Cv2.Rectangle(mat, new Rect(0, 5, 18, 19), Scalar.All(255), -1);
        Cv2.Rectangle(mat, new Rect(26, 45, 70, 21), Scalar.All(255), -1);
        using var mask = builder.Build();
        var extracted = Extract(mask);
        var complete = extracted.With(extracted.Items.Where(i => ((ContourItem)i).Points.Min(p => p.X) == 26));
        var touching = extracted.With(extracted.Items.Where(i => ((ContourItem)i).Points.Min(p => p.X) == 0));
        const ContourEnvelopeMode mode = ContourEnvelopeMode.MaximumInscribedCircle;
        using var wrong = new ImageFrameBuilder(120, 100, PixelFormat.Mono8).Build(mask.FrameId);
        Assert.Equal(ContourEnvelopeStatus.SourceMismatch, Result(complete, mode, wrong).Status);
        Assert.Equal(ContourEnvelopeStatus.MissingMask, Result(complete, mode).Status);
        Assert.Equal(ContourEnvelopeStatus.IncompleteBoundary, Result(touching, mode, mask).Status);
        foreach (var retrieval in new[] { ContourRetrieval.External, ContourRetrieval.List })
        {
            var partial = Extract(mask, new RectRoi(20, 30, 85, 65).ToRotated(), retrieval);
            Assert.Equal(ContourEnvelopeStatus.IncompleteBoundary, Result(partial, mode, mask).Status);
        }
        var clipped = Extract(mask, new RectRoi(50, 38, 60, 40).ToRotated());
        Assert.Equal(ContourEnvelopeStatus.IncompleteBoundary, Result(clipped, mode, mask).Status);
        Assert.Equal(ContourEnvelopeStatus.OverBudget, Result(complete, mode, mask, max: 80).Status);
        var detached = new VisionCollection(VisionItemKind.Contour, [((ContourItem)complete.Items[0]) with { RasterSource = null }]);
        Assert.Equal(ContourEnvelopeStatus.IncompleteBoundary, Result(detached, mode, mask).Status);
        Assert.Equal(1, mask.BufferHandle.ReferenceCount);
    }

    [Fact]
    public void EmptyAndDegenerateContoursHaveExplicitResults()
    {
        using var mask = new ImageFrameBuilder(30, 30, PixelFormat.Mono8).Build();
        var empty = Extract(mask);
        var options = new ContourEnvelopeOptions { Mode = ContourEnvelopeMode.MaximumInscribedCircle };
        Assert.Empty(ContourEnvelopeProcessor.Process(empty, options, mask).Items);
        var item = new ContourItem(new(Guid.NewGuid(), "fixture", 0, mask.FrameId, "image", "zero-area"),
            [new(2, 3), new(5, 3), new(8, 3)], true);
        foreach (var mode in Enum.GetValues<ContourEnvelopeMode>())
        {
            var result = Result(new(VisionItemKind.Contour, [item]), mode);
            Assert.Equal(ContourEnvelopeStatus.Degenerate, result.Status);
            Assert.False(result.Fields["Valid"].Boolean);
            Assert.Null(result.Region);
            Assert.Null(result.Circle);
        }
        Assert.Throws<OperationCanceledException>(() => ContourEnvelopeProcessor.Process(new(VisionItemKind.Contour, [item]), options, mask, new(true)));
    }

    private static VisionCollection Extract(ImageFrame image, RotatedRectRoi? roi = null, ContourRetrieval mode = ContourRetrieval.Tree) =>
        ShapeProcessors.Contours(image, new() { Retrieval = mode }, roi, Guid.NewGuid(), "contours", default);

    private static ContourEnvelopeItem Result(VisionCollection collection, ContourEnvelopeMode mode, ImageFrame? mask = null, int max = 2_000_000) =>
        Assert.IsType<ContourEnvelopeItem>(Assert.Single(ContourEnvelopeProcessor.Process(collection, new() { Mode = mode, MaximumObjectPixels = max }, mask).Items));
}
