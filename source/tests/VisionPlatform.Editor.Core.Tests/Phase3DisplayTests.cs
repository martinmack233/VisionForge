using System.Globalization;
using System.Text.Json;
using OpenCvSharp;
using VisionPlatform.Algorithms.Blob;
using VisionPlatform.Algorithms.Transforms;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using VisionPlatform.Runtime.Projects;
using Xunit;
using PixelFormat = VisionPlatform.Contracts.Images.PixelFormat;

namespace VisionPlatform.Editor.Core.Tests;

public sealed class Phase3DisplayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VisionStudio-Phase3Display-" + Guid.NewGuid().ToString("N"));
    private static NodeSpecification Node(string id, string type, object parameters) => new(id, type, type, JsonDefaults.Element(parameters));
    public Phase3DisplayTests()
    {
        Directory.CreateDirectory(_root);
        using var mat = new Mat(30, 40, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mat, new Rect(2, 2, 8, 6), Scalar.All(255), -1);
        Cv2.Rectangle(mat, new Rect(20, 15, 3, 3), Scalar.All(255), -1);
        Cv2.ImWrite(Path.Combine(_root, "input.png"), mat);
    }
    private RuntimeExecutor Executor() => new(BuiltinRegistration.CreateRegistry());
    private async Task<string> Save(ProjectDocument project, string name = "project.json")
    {
        var path = Path.Combine(_root, name); await new ProjectStore(BuiltinRegistration.CreateRegistry()).SaveAsync(path, project); return path;
    }
    private ProjectDocument FilterFlow() => new()
    {
        Nodes = [Node("input", "ImageInput", new { Path = "input.png" }), Node("blob", "BlobDetection", new { Threshold = 100, MinArea = 1 }),
            Node("filter", "BlobFilter", new { Area = new { Enabled = true, Minimum = 20, Maximum = 1000 } }), Node("display", "ImageDisplay", new { })],
        Connections = [new("input", "Image", "blob", "Image"), new("blob", "BlobResults", "filter", "BlobResults"),
            new("filter", "FilteredMask", "display", "Image"), new("filter", "FilteredBlobs", "display", "Blobs")],
        ROI = [new(10, 10, 10, 10)]
    };

    [Theory]
    [InlineData("String", "measured result", "measured result")]
    [InlineData("String", "", "")]
    [InlineData("Number", 1.25, "1.25")]
    [InlineData("Integer", 17, "17")]
    [InlineData("Boolean", true, "True")]
    [InlineData("Boolean", false, "False")]
    public async Task TextDisplayUsesExplicitTypedValueAndInvariantFormatting(string port, object value, string expected)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            using var result = await Executor().ExecuteSingleAsync(Node("text", "TextDisplay", new { }), new Dictionary<string, object> { [port] = value }, _root);
            Assert.True(result.Success, result.ErrorMessage); Assert.Equal(expected, result.Nodes["text"].Output<string>("Text"));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public async Task DisplayParametersControlOverlayVisibilityAndCurrentTextPrefix()
    {
        using var input = FrameFactory.CopyFrom(new byte[16], 4, 4, PixelFormat.Mono8);
        var blobs = new BlobResults([new(1, 1, 1, 1, new(1, 1, 1, 1))]);
        using (var hidden = await Executor().ExecuteSingleAsync(Node("display", "ImageDisplay", new ImageDisplayParameters { ShowOverlays = false }),
            new Dictionary<string, object> { ["Image"] = input, ["Blobs"] = blobs, ["ROI"] = new RotatedRectRoi(2, 2, 2, 2) }, _root))
        {
            Assert.True(hidden.Success); Assert.Single(hidden.Nodes["display"].Outputs); Assert.Contains("Image", hidden.Nodes["display"].Outputs.Keys);
        }
        using var text = await Executor().ExecuteSingleAsync(Node("text", "TextDisplay", new TextDisplayParameters { Prefix = "前景像素=" }),
            new Dictionary<string, object> { ["Integer"] = 17 }, _root);
        Assert.True(text.Success); Assert.Equal("前景像素=17", text.Nodes["text"].Output<string>("Text"));
    }

    [Fact]
    public async Task TextDisplayRejectsMissingAmbiguousAndNonFiniteValues()
    {
        foreach (var inputs in new Dictionary<string, object>[] { [], new() { ["String"] = "a", ["Integer"] = 1 }, new() { ["Number"] = double.NaN } })
        {
            using var result = await Executor().ExecuteSingleAsync(Node("text", "TextDisplay", new { }), inputs, _root);
            Assert.False(result.Success); Assert.Empty(result.Nodes["text"].Outputs); Assert.NotNull(result.Nodes["text"].ErrorMessage);
        }
    }

    [Fact]
    public void ImageCannotConnectToTextPort()
    {
        var registry = BuiltinRegistration.CreateRegistry();
        var graph = new NodeGraph([Node("input", "ImageInput", new { Path = "input.png" }), Node("text", "TextDisplay", new { })],
            [new("input", "Image", "text", "String")]);
        Assert.Contains(new GraphValidator(registry).Validate(graph), error => error.Contains("type", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PixelFormat.Mono8)]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    [InlineData(PixelFormat.BayerRG8)]
    [InlineData(PixelFormat.BayerGB8)]
    public async Task ImageDisplayOwnsOnlySharedHeaderAndPreservesEveryFormat(PixelFormat format)
    {
        using var input = FrameFactory.CopyFrom(new byte[6 * 4 * format.BytesPerPixel()], 6, 4, format);
        var before = FrameHandle.ActiveBytes; var refCount = input.BufferHandle.ReferenceCount;
        using (var result = await Executor().ExecuteSingleAsync(Node("display", "ImageDisplay", new { }), new Dictionary<string, object> { ["Image"] = input }, _root))
        {
            Assert.True(result.Success); var output = result.Nodes["display"].Output<ImageFrame>("Image");
            Assert.NotSame(input, output); Assert.Same(input.BufferHandle, output.BufferHandle);
            Assert.Equal(format, output.PixelFormat); Assert.Equal(before, FrameHandle.ActiveBytes);
            Assert.Equal(refCount + 1, input.BufferHandle.ReferenceCount);
        }
        Assert.False(input.IsDisposed); Assert.Equal(refCount, input.BufferHandle.ReferenceCount);
        using var lease = input.Lease(); Assert.Equal(0, lease.GetRowSpan(0)[0]);
    }

    [Fact]
    public async Task ImageDisplayRejectsOverlayInAnotherCoordinateSpaceWithoutLeaking()
    {
        using var input = FrameFactory.CopyFrom(new byte[16], 4, 4, PixelFormat.Mono8);
        var before = FrameHandle.ActiveBytes;
        using var result = await Executor().ExecuteSingleAsync(Node("display", "ImageDisplay", new { }),
            new Dictionary<string, object> { ["Image"] = input, ["Blobs"] = new BlobResults([], "crop:other") }, _root);
        Assert.False(result.Success); Assert.Contains("coordinate", result.Nodes["display"].ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, FrameHandle.ActiveBytes); Assert.False(input.IsDisposed);
    }

    [Fact]
    public async Task FilteredDisplayShowsSelectedBlobsAndEmptyRerunClearsOldResults()
    {
        var before = FrameHandle.ActiveBytes;
        using (var session = new EditorSession())
        {
            await session.OpenAsync(await Save(FilterFlow())); await session.RunAsync();
            session.SelectedNode = session.Nodes.Single(n => n.TypeId == "BlobDetection");
            Assert.Equal(2, session.Overlays.Count(o => o.Kind == OverlayKind.Blob));
            session.SelectedNode = session.Nodes.Single(n => n.TypeId == "ImageDisplay");
            Assert.Single(session.Overlays); Assert.Equal(OverlayKind.Blob, session.Overlays[0].Kind);
            Assert.Equal(48, ((BlobResults)session.SelectedOutputs.Single(o => o.PortName == "Blobs").Value).Items[0].Area);
            var filter = session.Nodes.Single(n => n.TypeId == "BlobFilter");
            session.CommitParameters(filter, JsonDefaults.Element(new BlobFilterOptions { Area = new() { Enabled = true, Minimum = 100, Maximum = 1000 } }));
            Assert.Empty(session.Results); Assert.Empty(session.SelectedOutputs); Assert.Empty(session.Overlays);
            await session.RunAsync(); session.SelectedNode = session.Nodes.Single(n => n.TypeId == "ImageDisplay");
            Assert.Empty(session.Overlays); Assert.Equal(0, ((BlobResults)session.SelectedOutputs.Single(o => o.PortName == "Blobs").Value).Count);
            Assert.Equal(0, CountNonzero(session.DisplayImage!));
            await session.NewAsync(); Assert.Empty(session.Results); Assert.Empty(session.SelectedOutputs); Assert.Null(session.DisplayImage);
        }
        Assert.Equal(before, FrameHandle.ActiveBytes);
    }

    [Fact]
    public async Task MedianThresholdTextFlowUsesMeasuredCountAndSingleRunHasFreshInputs()
    {
        var project = new ProjectDocument
        {
            Nodes = [Node("input", "ImageInput", new { Path = "input.png" }), Node("median", "MedianBlur", new { KernelSize = 3 }),
                Node("threshold", "Threshold", new { ThresholdValue = 100 }), Node("text", "TextDisplay", new { })],
            Connections = [new("input", "Image", "median", "Image"), new("median", "ProcessedImage", "threshold", "Image"), new("threshold", "ForegroundPixelCount", "text", "Integer")]
        };
        var file = await Save(project);
        using var session = new EditorSession(); await session.OpenAsync(file); await session.RunAsync();
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "Threshold");
        var count = (int)session.SelectedOutputs.Single(o => o.PortName == "ForegroundPixelCount").Value;
        Assert.Equal(CountNonzero((ImageFrame)session.SelectedOutputs.Single(o => o.PortName == "ProcessedImage").Value), count);
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "TextDisplay");
        Assert.Equal(count.ToString(CultureInfo.InvariantCulture), session.SelectedOutputs.Single(o => o.PortName == "Text").Value);
        await session.RunAsync(EditorRunMode.Single);
        Assert.Single(session.Results); Assert.Equal("Success", session.Results[0].Status);
        Assert.Equal(count.ToString(CultureInfo.InvariantCulture), session.SelectedOutputs.Single(o => o.PortName == "Text").Value);
        session.CommitParameters(session.SelectedNode!, JsonDefaults.Element(new TextDisplayParameters())); Assert.Empty(session.SelectedOutputs);
    }

    [Fact]
    public async Task CropDisplayHidesOriginalCoordinateOverlaysAndRoiBindsExistingParameters()
    {
        var project = new ProjectDocument { Nodes = [Node("input", "ImageInput", new { Path = "input.png" }),
            Node("crop", "RotatedCrop", new { CenterX = 12, CenterY = 10, Width = 12, Height = 10, Angle = 20 }), Node("display", "ImageDisplay", new { })],
            Connections = [new("input", "Image", "crop", "Image"), new("crop", "CroppedImage", "display", "Image")], ROI = [new(12.5, 10.5, 12, 10, 20)] };
        using var session = new EditorSession(); await session.OpenAsync(await Save(project)); await session.RunAsync();
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "ImageDisplay");
        Assert.StartsWith("crop:", session.DisplayImage!.CoordinateSpace.Id); Assert.Empty(session.Overlays); Assert.Null(session.EditedRoi);
        Assert.Throws<InvalidOperationException>(() => session.UpdateRoi(new(5, 5, 4, 4, 0, session.DisplayImage.CoordinateSpace.Id)));
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "ImageInput");
        var edited = new RotatedRectRoi(15.5, 12.5, 14, 10, 25); session.UpdateRoi(edited);
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "RotatedCrop"); session.BindRoiToSelectedNode();
        var options = session.SelectedNode.Parameters.Deserialize<RotatedCropOptions>(JsonDefaults.Options)!;
        Assert.Equal(15, options.CenterX); Assert.Equal(12, options.CenterY); Assert.Equal(25, options.Angle);
        Assert.Equal(edited, session.SnapshotProject().ROI[0]);
        var saved = Path.Combine(_root, "crop-saved.json"); await session.SaveAsync(saved); await session.NewAsync(); await session.OpenAsync(saved);
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "RotatedCrop"); Assert.Equal(edited, session.EditedRoi);
    }

    [Fact]
    public async Task DownstreamCropParameterEditDoesNotOverwriteOriginalRoiWithLocalCoordinates()
    {
        var original = new RotatedRectRoi(15, 12, 10, 8);
        var project = new ProjectDocument
        {
            Nodes = [Node("input", "ImageInput", new { Path = "input.png" }),
                Node("first", "CropImage", new { X = 5, Y = 5, Width = 20, Height = 15 }), Node("second", "CropImage", new { X = 2, Y = 2, Width = 8, Height = 6 })],
            Connections = [new("input", "Image", "first", "Image"), new("first", "CroppedImage", "second", "Image")], ROI = [original]
        };
        using var session = new EditorSession(); await session.OpenAsync(await Save(project)); await session.RunAsync();
        session.SelectedNode = session.Nodes.Single(n => n.NodeId == "second");
        session.CommitParameters(session.SelectedNode, JsonDefaults.Element(new CropImageOptions { X = 2, Y = 2, Width = 6, Height = 6 }));
        Assert.Equal(original, session.SnapshotProject().ROI[0]); Assert.Null(session.EditedRoi);
        await session.RunAsync(); Assert.All(session.Results, r => Assert.Equal("Success", r.Status));
    }

    [Fact]
    public async Task CropBindingRejectsRotationAndFractionalDimensionsWithoutSilentlyRounding()
    {
        using var session = new EditorSession(); await session.ImportImageAsync(Path.Combine(_root, "input.png"));
        session.AddNode("CropImage"); var crop = session.SelectedNode!;
        var input = session.Nodes.Single(n => n.TypeId == "ImageInput");
        Assert.True(session.TryConnect(input.Outputs.Single(p => p.Name == "Image"), crop.Inputs.Single(p => p.Name == "Image"), out _));
        session.UpdateRoi(new(10, 10, 10, 8, 15)); Assert.Throws<NotSupportedException>(session.BindRoiToSelectedNode);
        session.UpdateRoi(new(10, 10, 10.5, 8)); Assert.Throws<ArgumentException>(session.BindRoiToSelectedNode);
        session.UpdateRoi(new(10, 10, 10, 8)); session.BindRoiToSelectedNode();
        var options = crop.Parameters.Deserialize<CropImageOptions>(JsonDefaults.Options)!;
        Assert.Equal(5, options.X); Assert.Equal(6, options.Y); Assert.Equal(10, options.Width); Assert.Equal(8, options.Height);
    }

    private static int CountNonzero(ImageFrame image)
    {
        using var lease = image.Lease(); var total = 0;
        for (var y = 0; y < image.Height; y++) foreach (var p in lease.GetRowSpan(y)[..image.Width]) if (p != 0) total++;
        return total;
    }
    public void Dispose()
    {
        var target = Path.GetFullPath(_root); var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("VisionStudio-Phase3Display-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture directory.");
        Directory.Delete(target, true);
    }
}
