using Log.Core.Store;
using Xunit;

namespace Log.Core.Tests;

public sealed class StorageGuardTests
{
    [Theory]
    [InlineData(30 * TimeSpan.TicksPerMinute, 30_000_000_000L, 15_000_000_000L)]
    [InlineData(90 * TimeSpan.TicksPerMinute, 30_000_000_000L, 45_000_000_000L)]
    [InlineData(1L, 1L, 1L)]
    [InlineData(TimeSpan.TicksPerHour, long.MaxValue, long.MaxValue)]
    public void CaptureEstimateUsesExactArithmeticWithoutIntermediateOverflow(
        long durationTicks, long bytesPerHour, long expectedBytes)
    {
        var availability = StorageGuard.Check(
            Path.GetTempPath(), TimeSpan.FromTicks(durationTicks), bytesPerHour, 0);

        Assert.Equal(expectedBytes, availability.CaptureBytes);
    }

    [Fact]
    public void CaptureEstimateRejectsAnUnrepresentableFinalSize()
    {
        Assert.Throws<OverflowException>(() => StorageGuard.Check(
            Path.GetTempPath(), TimeSpan.FromHours(2), long.MaxValue, 0));
    }

    [Fact]
    public void CalculatesAndEnforcesCaptureReserve()
    {
        var availability = new StorageAvailability(100, 80, 20);
        Assert.True(availability.IsSufficient);

        var insufficient = new StorageAvailability(99, 80, 20);
        var exception = Assert.Throws<InsufficientStorageException>(
            () => StorageGuard.EnsureSufficient(insufficient));
        Assert.Equal(insufficient, exception.Availability);
    }

    [Fact]
    public void CheckReadsFreeSpaceFromTheTargetVolume()
    {
        var availability = StorageGuard.Check(
            Path.GetTempPath(),
            TimeSpan.FromHours(1),
            estimatedBytesPerHour: 0,
            reserveBytes: 0);

        Assert.True(availability.AvailableBytes > 0);
        Assert.Equal(0, availability.RequiredBytes);
    }

    [Fact]
    public void CheckRequiredBytesUsesTheDestinationVolume()
    {
        var availability = StorageGuard.CheckRequiredBytes(Path.GetTempPath(), 123);

        Assert.True(availability.AvailableBytes > 0);
        Assert.Equal(123, availability.RequiredBytes);
    }
}
