using VisionPlatform.Algorithms.Blob;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;

public class Phase3BlobFilterTests
{
    [Fact]
    public void DetectorAddsExactRunsAndDigitalPerimeterIncludingHoleWithoutChangingMeasurements()
    {
        using var image = CreateTargets();
        var detected = BlobDetector.Detect(image, null, new BlobOptions());
        Assert.Equal(3, detected.Count); Assert.Equal(40, detected.RasterWidth); Assert.Equal(35, detected.RasterHeight);
        var ring = detected.Items.Single(b => b.Area == 48);
        Assert.Equal(new RectRoi(22, 2, 8, 8), ring.Bounds); Assert.Equal(25.5, ring.CenterX); Assert.Equal(5.5, ring.CenterY);
        Assert.Equal(48d, ring.Perimeter); Assert.Equal(4 * Math.PI * 48 / (48 * 48), ring.Circularity!.Value, 12);
        Assert.Equal(ring.Area, ring.PixelRuns!.Sum(r => r.Length));
        Assert.All(detected.Items, b => Assert.InRange(b.Circularity!.Value, 0, 1));
        using var filtered = BlobFilter.Apply(detected, new());
        using var source = image.Lease(); using var mask = filtered.FilteredMask.Lease();
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++) Assert.Equal(source.GetRowSpan(y)[x] == 0 ? 0 : 255, mask.GetRowSpan(y)[x]);
        Assert.Equal(0, mask.GetRowSpan(5)[25]); // genuine hole, not the bounding rectangle
        Assert.Equal(3, detected.Count); Assert.NotSame(detected, filtered.FilteredBlobs);
    }

    [Theory]
    [InlineData("Area", 20, 30, 24)]
    [InlineData("Width", 3, 3, 24)]
    [InlineData("Height", 4, 4, 16)]
    [InlineData("AspectRatio", .3, .4, 24)]
    [InlineData("Circularity", .7, .9, 16)]
    [InlineData("CenterX", 12, 14, 24)]
    [InlineData("CenterY", 3, 4, 16)]
    public void EachConditionCanIndependentlySelectMeasuredTarget(string property, double minimum, double maximum, int area)
    {
        using var image = CreateTargets(); var detected = BlobDetector.Detect(image, null, new());
        var range = Range(minimum, maximum);
        var options = property switch
        {
            "Area" => new BlobFilterOptions { Area = range }, "Width" => new() { Width = range },
            "Height" => new() { Height = range }, "AspectRatio" => new() { AspectRatio = range },
            "Circularity" => new() { Circularity = range }, "CenterX" => new() { CenterX = range },
            "CenterY" => new() { CenterY = range }, _ => throw new InvalidOperationException()
        };
        using var filtered = BlobFilter.Apply(detected, options);
        var blob = Assert.Single(filtered.FilteredBlobs.Items); Assert.Equal(area, blob.Area);
        Assert.Single(filtered.BoundingBoxes); Assert.Equal(new Point2D(blob.CenterX, blob.CenterY), Assert.Single(filtered.Centroids));
        Assert.Equal(area, CountMask(filtered.FilteredMask));
        using var disabled = BlobFilter.Apply(detected, options with
        {
            Area = options.Area with { Enabled = false }, Width = options.Width with { Enabled = false },
            Height = options.Height with { Enabled = false }, AspectRatio = options.AspectRatio with { Enabled = false },
            Circularity = options.Circularity with { Enabled = false }, CenterX = options.CenterX with { Enabled = false }, CenterY = options.CenterY with { Enabled = false }
        });
        Assert.Equal(3, disabled.BlobCount);
    }

    [Fact]
    public void ConditionsUseAndAndSortingHonorsLimitWithoutMutatingOriginal()
    {
        using var image = CreateTargets(); var detected = BlobDetector.Detect(image, null, new());
        var before = detected.Items.ToArray();
        using var selected = BlobFilter.Apply(detected, new() { Area = Range(16, 48), CenterX = Range(0, 20), Sort = BlobSortOrder.AreaDescending, MaximumCount = 1 });
        Assert.Equal(24, Assert.Single(selected.FilteredBlobs.Items).Area);
        Assert.Equal(before, detected.Items); Assert.Equal(24, CountMask(selected.FilteredMask));
        using var empty = BlobFilter.Apply(detected, new() { Area = Range(100, 200) });
        Assert.Empty(empty.FilteredBlobs.Items); Assert.Empty(empty.BoundingBoxes); Assert.Empty(empty.Centroids);
        Assert.Equal(0, empty.BlobCount); Assert.Equal(0, CountMask(empty.FilteredMask));
        using var zeroLimit = BlobFilter.Apply(detected, new() { MaximumCount = 0 }); Assert.Equal(0, zeroLimit.BlobCount);
    }

    [Theory]
    [InlineData(BlobSortOrder.AreaAscending, 16)]
    [InlineData(BlobSortOrder.AreaDescending, 48)]
    [InlineData(BlobSortOrder.XAscending, 16)]
    [InlineData(BlobSortOrder.YAscending, 16)]
    public void SortOrdersAreDeterministic(BlobSortOrder sort, int expectedFirstArea)
    {
        using var image = CreateTargets(); var detected = BlobDetector.Detect(image, null, new());
        using var first = BlobFilter.Apply(detected, new() { Sort = sort });
        using var second = BlobFilter.Apply(detected, new() { Sort = sort });
        Assert.Equal(expectedFirstArea, first.FilteredBlobs.Items[0].Area);
        Assert.Equal(first.FilteredBlobs.Items, second.FilteredBlobs.Items);
    }

    [Fact]
    public void RotatedRoiRunsRemainInInputRasterAndNestedViewOriginsSurvive()
    {
        using var builder = new ImageFrameBuilder(80, 80, PixelFormat.Mono8);
        for (var y = 0; y < 80; y++) builder.GetRowSpan(y).Fill(240);
        using var image = builder.Build(); using var view = image.CreateView(10, 15, 50, 50);
        var roi = new RotatedRectRoi(35, 40, 24, 10, 35);
        var blobs = BlobDetector.Detect(view, roi, new());
        using var filtered = BlobFilter.Apply(blobs, new());
        Assert.Equal(50, filtered.FilteredMask.Width); Assert.Equal(50, filtered.FilteredMask.Height);
        Assert.Equal(new ImageCoordinateSpace("image", 10, 15), filtered.FilteredMask.CoordinateSpace);
        Assert.Equal(blobs.Items.Sum(b => b.Area), CountMask(filtered.FilteredMask));
        using var lease = filtered.FilteredMask.Lease(); Assert.Equal(0, lease.GetRowSpan(0)[0]);
        var radians = roi.AngleDegrees * Math.PI / 180;
        for (var y = 0; y < 50; y++)
            for (var x = 0; x < 50; x++)
            {
                var dx = x + 10.5 - roi.CenterX; var dy = y + 15.5 - roi.CenterY;
                var inside = Math.Abs(Math.Cos(radians) * dx + Math.Sin(radians) * dy) <= roi.Width / 2 &&
                    Math.Abs(-Math.Sin(radians) * dx + Math.Cos(radians) * dy) <= roi.Height / 2;
                Assert.Equal(inside ? 255 : 0, lease.GetRowSpan(y)[x]);
            }
    }

    [Fact]
    public void MissingOrMalformedMetadataIsRejectedAndFailureDoesNotLeakFrames()
    {
        var baseline = FrameHandle.ActiveBytes;
        using (var image = CreateTargets())
        {
            var blobs = BlobDetector.Detect(image, null, new());
            var active = FrameHandle.ActiveBytes;
            Assert.Throws<ArgumentException>(() => BlobFilter.Apply(new BlobResults(blobs.Items), new()));
            Assert.Throws<ArgumentException>(() => BlobFilter.Apply(blobs with { Items = blobs.Items.Select(b => b with { PixelRuns = null }).ToArray() }, new()));
            Assert.Throws<ArgumentException>(() => BlobFilter.Apply(blobs with { Items = [blobs.Items[0] with { PixelRuns = [new(-1, 0, 3)] }] }, new()));
            Assert.Throws<ArgumentException>(() => BlobFilter.Apply(blobs with { Items = [blobs.Items[0] with { PixelRuns = [] }] }, new()));
            Assert.Throws<ArgumentException>(() => BlobFilter.Apply(blobs with { Items = blobs.Items.Select(b => b with { Perimeter = null }).ToArray() }, new() { Circularity = Range(0, 1) }));
            Assert.Throws<ArgumentException>(() => BlobFilter.Apply(blobs, new() { Area = Range(10, 1) }));
            Assert.Throws<ArgumentException>(() => BlobFilter.Apply(blobs, new() { CenterX = Range(double.NaN, 1) }));
            Assert.Throws<ArgumentException>(() => BlobFilter.Apply(blobs, new() { Circularity = Range(0, 2) }));
            Assert.Throws<ArgumentException>(() => BlobFilter.Apply(blobs, new() { MaximumCount = -1 }));
            Assert.Throws<ArgumentException>(() => BlobFilter.Apply(blobs, new() { Sort = (BlobSortOrder)90 }));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => BlobFilter.Apply(blobs, new(), cancelled.Token));
            Assert.Equal(active, FrameHandle.ActiveBytes);
        }
        Assert.Equal(baseline, FrameHandle.ActiveBytes);
    }

    [Fact]
    public async Task FilterNodeExecutesThroughRuntimeAndEmptyNextRunHasNoOldMask()
    {
        var baseline = FrameHandle.ActiveBytes;
        using (var image = CreateTargets())
        {
            var blobs = BlobDetector.Detect(image, null, new());
            var executor = new RuntimeExecutor(BuiltinRegistration.CreateRegistry());
            using var selected = await executor.ExecuteSingleAsync(new("filter", "BlobFilter", "筛选", JsonDefaults.Element(new BlobFilterOptions { Area = Range(20, 30) })),
                new Dictionary<string, object> { ["BlobResults"] = blobs });
            Assert.True(selected.Success); Assert.Equal(1, selected.Nodes["filter"].Output<int>("BlobCount"));
            Assert.Equal(24, CountMask(selected.Nodes["filter"].Output<ImageFrame>("FilteredMask")));
            using var empty = await executor.ExecuteSingleAsync(new("filter", "BlobFilter", "筛选", JsonDefaults.Element(new BlobFilterOptions { Area = Range(100, 200) })),
                new Dictionary<string, object> { ["BlobResults"] = blobs });
            Assert.True(empty.Success); Assert.Equal(0, empty.Nodes["filter"].Output<int>("BlobCount"));
            Assert.Equal(0, CountMask(empty.Nodes["filter"].Output<ImageFrame>("FilteredMask")));
            Assert.Equal(3, blobs.Count);
        }
        Assert.Equal(baseline, FrameHandle.ActiveBytes);
    }

    private static BlobRangeOptions Range(double minimum, double maximum) => new() { Enabled = true, Minimum = minimum, Maximum = maximum };
    private static ImageFrame CreateTargets()
    {
        using var builder = new ImageFrameBuilder(40, 35, PixelFormat.Mono8);
        for (var y = 2; y < 6; y++) builder.GetRowSpan(y).Slice(2, 4).Fill(240);
        for (var y = 3; y < 11; y++) builder.GetRowSpan(y).Slice(12, 3).Fill(240);
        for (var y = 2; y < 10; y++) builder.GetRowSpan(y).Slice(22, 8).Fill(240);
        for (var y = 4; y < 8; y++) builder.GetRowSpan(y).Slice(24, 4).Clear();
        return builder.Build();
    }
    private static int CountMask(ImageFrame mask)
    {
        var count = 0; using var lease = mask.Lease();
        for (var y = 0; y < mask.Height; y++) foreach (var p in lease.GetRowSpan(y)[..mask.Width]) if (p != 0) count++;
        return count;
    }
}
