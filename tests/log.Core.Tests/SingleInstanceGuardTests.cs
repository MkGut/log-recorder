using Log.Core.Store;
using Xunit;

namespace Log.Core.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void SecondGuardForTheSameUserAndApplicationCannotAcquire()
    {
        var applicationId = $"log-tests-{Guid.NewGuid():N}";
        Assert.True(SingleInstanceGuard.TryAcquire(applicationId, out var first));
        using (first)
        {
            Assert.False(SingleInstanceGuard.TryAcquire(applicationId, out var second));
            Assert.Null(second);
        }

        Assert.True(SingleInstanceGuard.TryAcquire(applicationId, out var afterRelease));
        afterRelease!.Dispose();
    }

    [Fact]
    public async Task SecondGuardSignalsThePrimaryInstanceToActivate()
    {
        var applicationId = $"log-tests-{Guid.NewGuid():N}";
        var activation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(SingleInstanceGuard.TryAcquire(
            applicationId,
            () => activation.TrySetResult(),
            out var first));

        using (first)
        {
            Assert.False(SingleInstanceGuard.TryAcquire(applicationId, out var second));
            Assert.Null(second);
            await activation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void GuardCanBeDisposedByConcurrentShutdownCallbacks()
    {
        var applicationId = $"log-tests-{Guid.NewGuid():N}";
        Assert.True(SingleInstanceGuard.TryAcquire(applicationId, out var first));

        Parallel.For(0, 32, _ => first!.Dispose());

        Assert.True(SingleInstanceGuard.TryAcquire(applicationId, out var afterRelease));
        afterRelease!.Dispose();
    }
}
