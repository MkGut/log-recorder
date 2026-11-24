using Log.Core.Capture;
using Xunit;

namespace Log.Core.Tests;

[Collection("CaptureSessionRegistry")]
public sealed class CaptureSessionRegistryTests
{
    [Fact]
    public void AllowsOnlyOneActiveCapturePerStoreAndReleasesAfterDispose()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        using var first = CaptureSessionRegistry.Acquire(root);

        Assert.Throws<InvalidOperationException>(() => CaptureSessionRegistry.Acquire(root));

        first.Dispose();
        using var second = CaptureSessionRegistry.Acquire(root);
    }
}
