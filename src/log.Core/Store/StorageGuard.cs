namespace Log.Core.Store;

public readonly record struct StorageAvailability(
    long AvailableBytes,
    long CaptureBytes,
    long ReserveBytes)
{
    public long RequiredBytes => checked(CaptureBytes + ReserveBytes);
    public bool IsSufficient => AvailableBytes >= RequiredBytes;
}

public sealed class InsufficientStorageException : IOException
{
    public InsufficientStorageException(StorageAvailability availability)
        : base($"The session needs {availability.RequiredBytes:N0} bytes, but only {availability.AvailableBytes:N0} bytes are available.")
    {
        Availability = availability;
    }

    public StorageAvailability Availability { get; }
}

public static class StorageGuard
{
    public static StorageAvailability Check(
        string rootDirectory,
        TimeSpan duration,
        long estimatedBytesPerHour,
        long reserveBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentOutOfRangeException.ThrowIfNegative(duration.Ticks);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedBytesPerHour);
        ArgumentOutOfRangeException.ThrowIfNegative(reserveBytes);

        var fullPath = Path.GetFullPath(rootDirectory);
        var driveRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            throw new ArgumentException("The storage path has no drive root.", nameof(rootDirectory));
        }

        var drive = new DriveInfo(driveRoot);
        return new StorageAvailability(
            drive.AvailableFreeSpace,
            CalculateCaptureBytes(duration, estimatedBytesPerHour),
            reserveBytes);
    }

    public static StorageAvailability CheckRequiredBytes(
        string rootDirectory,
        long requiredBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);

        var fullPath = Path.GetFullPath(rootDirectory);
        var driveRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            throw new ArgumentException("The storage path has no drive root.", nameof(rootDirectory));
        }

        return new StorageAvailability(
            new DriveInfo(driveRoot).AvailableFreeSpace,
            requiredBytes,
            0);
    }

    public static void EnsureSufficient(StorageAvailability availability)
    {
        if (!availability.IsSufficient)
        {
            throw new InsufficientStorageException(availability);
        }
    }

    private static long CalculateCaptureBytes(TimeSpan duration, long estimatedBytesPerHour)
    {
        // Widen before multiplying; realistic capture rates overflow a long in ticks.
        var numerator = (Int128)estimatedBytesPerHour * duration.Ticks;
        return checked((long)((numerator + TimeSpan.TicksPerHour - 1) / TimeSpan.TicksPerHour));
    }
}
