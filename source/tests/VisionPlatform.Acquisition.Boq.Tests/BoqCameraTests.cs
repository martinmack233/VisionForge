using System.Text.Json;
using VisionPlatform.Acquisition.Boq;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Images;
using Xunit;

namespace VisionPlatform.Acquisition.Boq.Tests;

public sealed class BoqCameraTests
{
    [Fact]
    public async Task OptionalMissingSdkDoesNotThrowDuringEnumeration()
    {
        var provider = new BoqCameraProvider(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "BoqApi_CSharp.dll"));
        Assert.False(provider.IsSdkAvailable);
        Assert.Empty(await provider.EnumerateAsync());
        Assert.Contains("博峤", provider.LastError);
        await using var device = provider.Create("serial-1");
        await Assert.ThrowsAsync<FileNotFoundException>(() => device.OpenAsync(new()));
        Assert.Equal(CameraState.Faulted, device.State);
    }

    [Theory]
    [InlineData(0x01080001, PixelFormat.Mono8, 1)]
    [InlineData(0x01080009, PixelFormat.BayerRG8, 1)]
    [InlineData(0x0108000A, PixelFormat.BayerGB8, 1)]
    [InlineData(0x02180014, PixelFormat.RGB8, 3)]
    [InlineData(0x02180015, PixelFormat.BGR8, 3)]
    public void SdkPixelsAreCopiedWithoutChangingChannelsOrBayerPattern(int pfnc, PixelFormat expected, int channels)
    {
        var rowBytes = 4 * channels;
        var stride = rowBytes + 2;
        var buffer = Enumerable.Range(0, stride * 2).Select(i => (byte)(i + 1)).ToArray();
        using var frame = BoqCameraDevice.CopyFrame(new(buffer, 4, 2, buffer.Length, pfnc, DateTimeOffset.UtcNow));
        Assert.Equal(expected, frame.PixelFormat); Assert.Equal(stride, frame.Stride);
        using var lease = frame.Lease();
        var expectedFirst = buffer.AsSpan(0, rowBytes).ToArray();
        var expectedSecond = buffer.AsSpan(stride, rowBytes).ToArray();
        Array.Fill(buffer, (byte)255); // SDK may reclaim/reuse the array immediately after callback return.
        Assert.Equal(expectedFirst, lease.GetRowSpan(0).ToArray());
        Assert.Equal(expectedSecond, lease.GetRowSpan(1).ToArray());
    }

    [Fact]
    public void UnsupportedOrAmbiguousSdkBuffersAreRejected()
    {
        Assert.Throws<NotSupportedException>(() => BoqCameraDevice.CopyFrame(new(new byte[8], 4, 2, 8, 0x01100003, DateTimeOffset.UtcNow)));
        Assert.Throws<InvalidDataException>(() => BoqCameraDevice.CopyFrame(new(new byte[9], 4, 2, 9, 0x01080001, DateTimeOffset.UtcNow)));
        Assert.Throws<InvalidDataException>(() => BoqCameraDevice.CopyFrame(new(new byte[7], 4, 2, 8, 0x01080001, DateTimeOffset.UtcNow)));
        Assert.Throws<InvalidDataException>(() => BoqCameraDevice.CopyFrame(new(new byte[4], 4, 2, 4, 0x01080001, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task EnumeratedSdkSimulatorIsIdentifiedAndNeverAutoOpened()
    {
        var factory = new FakeFactory();
        var provider = new BoqCameraProvider(factory);
        var device = Assert.Single(await provider.EnumerateAsync());
        Assert.True(device.IsVirtual); Assert.Contains("SDK 模拟", device.DisplayName);
        Assert.Equal(0, factory.ConnectCount);
        await using var camera = provider.Create(device.DeviceId);
        Assert.Equal(0, factory.ConnectCount);
    }

    [Fact]
    public async Task SingleCaptureHasNewFrameIdentityAndSurvivesDeviceDisposal()
    {
        var factory = new FakeFactory();
        var provider = new BoqCameraProvider(factory);
        var device = provider.Create("fake");
        await device.OpenAsync(new());
        using var first = await device.CaptureSingleAsync();
        Assert.Equal(first.FrameId, device.LastFrame!.FrameId); Assert.Equal(1, device.LastFrame.Sequence);
        using var second = await device.CaptureSingleAsync();
        Assert.NotEqual(first.FrameId, second.FrameId); Assert.Equal(2, device.LastFrame.Sequence);
        await device.DisposeAsync();
        using var lease = first.Lease();
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, lease.GetRowSpan(0).ToArray());
        Assert.Equal(1, factory.Camera.DisposeCount);
    }

    [Fact]
    public async Task ContinuousQueueIsOneSlotAndReleasesDiscardedFrames()
    {
        var factory = new FakeFactory();
        await using var camera = new BoqCameraProvider(factory).Create("fake");
        await camera.OpenAsync(new()); await camera.StartAcquisitionAsync();
        factory.Camera.Emit(1); factory.Camera.Emit(2); factory.Camera.Emit(3);
        Assert.Equal(2, ((BoqCameraDevice)camera).DroppedFrames);
        await using var reader = camera.ReadFramesAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        using var frame = reader.Current;
        using var lease = frame.Lease(); Assert.Equal(3, lease.GetRowSpan(0)[0]);
        await camera.StopAcquisitionAsync();
        Assert.Equal(CameraState.Open, camera.State);
        Assert.Equal(1, factory.Camera.StopCount);
        Assert.False(frame.IsDisposed);
    }

    [Fact]
    public async Task DisconnectFailsWaitingCaptureAndPreventsLateFrames()
    {
        var factory = new FakeFactory(); factory.Camera.EmitOnGrab = false;
        await using var camera = new BoqCameraProvider(factory).Create("fake");
        await camera.OpenAsync(new());
        var capture = camera.CaptureSingleAsync();
        await factory.Camera.GrabCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        factory.Camera.Disconnect();
        await Assert.ThrowsAsync<IOException>(() => capture);
        Assert.Equal(CameraState.Faulted, camera.State);
        Assert.Contains("断线", camera.LastError);
        factory.Camera.Emit(9); Assert.Null(camera.LastFrame);
    }

    [Fact]
    public async Task CancelledCaptureRequiresReopenSoLateCallbacksCannotBecomeNextFrame()
    {
        var factory = new FakeFactory(); factory.Camera.EmitOnGrab = false;
        await using var camera = new BoqCameraProvider(factory).Create("fake");
        await camera.OpenAsync(new());
        using var cancel = new CancellationTokenSource();
        var capture = camera.CaptureSingleAsync(cancel.Token);
        await factory.Camera.GrabCalled.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        factory.Camera.Emit(8); Assert.Null(camera.LastFrame);
        Assert.Equal(CameraState.Faulted, camera.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.CaptureSingleAsync());
    }

    [Fact]
    public async Task ParameterWritesAreExplicitAndNeverHappenOnCreateOrOpen()
    {
        var factory = new FakeFactory();
        await using var camera = new BoqCameraProvider(factory).Create("fake");
        await camera.OpenAsync(new()); Assert.Equal(0, factory.Camera.WriteCount);
        await camera.SetParameterAsync("ExposureTimeRaw", JsonSerializer.SerializeToElement(5));
        Assert.Equal(1, factory.Camera.WriteCount);
        await camera.StartAcquisitionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.SetParameterAsync("ExposureTimeRaw", JsonSerializer.SerializeToElement(6)));
        await Assert.ThrowsAsync<NotSupportedException>(() => camera.SetParameterAsync("Unknown", JsonSerializer.SerializeToElement(6)));
    }

    private sealed class FakeFactory : IBoqSdkFactory
    {
        public FakeCamera Camera { get; } = new();
        public int ConnectCount;
        public IReadOnlyList<BoqSdkIdentity> Enumerate() => [new("fake", "FakeGigeCamera", "BOQVIEW", "fake")];
        public IBoqSdkCamera Connect(string serial) { ConnectCount++; return Camera; }
    }
    private sealed class FakeCamera : IBoqSdkCamera
    {
        public event Action<BoqSdkFrame>? FrameReceived;
        public event Action? Disconnected;
        public bool EmitOnGrab = true;
        public int DisposeCount, StopCount, WriteCount;
        public TaskCompletionSource GrabCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<CameraParameterCapability> QueryCapabilities() => [new("ExposureTimeRaw", "曝光", CameraParameterKind.Integer, true, true, 0, 100)];
        public IReadOnlyDictionary<string, JsonElement> ReadParameters() => new Dictionary<string, JsonElement>();
        public void SetParameter(string name, JsonElement value) => WriteCount++;
        public void GrabOne(uint timeoutMilliseconds) { GrabCalled.TrySetResult(); if (EmitOnGrab) Emit(1); }
        public void Start() { }
        public void Stop() => StopCount++;
        public void Dispose() => DisposeCount++;
        public void Emit(byte value) => FrameReceived?.Invoke(new([value, 2, 3, 4, 5, 6, 7, 8], 4, 2, 8, 0x01080001, DateTimeOffset.UtcNow));
        public void Disconnect() => Disconnected?.Invoke();
    }
}
