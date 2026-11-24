using Log.Core.Capture;
using Xunit;

namespace Log.Windows.Tests;

public sealed class SnapshotCaptureEncoderTests
{
    [Fact]
    public async Task AcceptsSampledFramesWithoutCreatingMediaOutput()
    {
        var encoder = new SnapshotCaptureEncoder(2, 2);
        var frame = new FrameSample(0, new byte[16], 2, 2);

        await encoder.WriteAsync(new EncodedFrame(0, 0, frame, false, 0));
        await encoder.FinalizeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await encoder.WriteAsync(new EncodedFrame(1, 1, frame, false, 0)));
    }

    [Fact]
    public async Task RejectsUnexpectedSampleDimensions()
    {
        var encoder = new SnapshotCaptureEncoder(2, 2);
        var frame = new FrameSample(0, new byte[4], 1, 1);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await encoder.WriteAsync(new EncodedFrame(0, 0, frame, false, 0)));
    }
}
