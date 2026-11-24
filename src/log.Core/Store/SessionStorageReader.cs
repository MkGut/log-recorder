namespace Log.Core.Store;

public sealed record SessionStorageUsage(
    long TotalBytes,
    int FileCount);

public static class SessionStorageReader
{
    public static SessionStorageUsage Measure(string sessionDirectory)
    {
        return Measure(sessionDirectory, CancellationToken.None);
    }

    public static SessionStorageUsage Measure(
        string sessionDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(sessionDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The session directory was not found: '{root}'.");
        }

        var pending = new Stack<string>();
        pending.Push(root);
        var totalBytes = 0L;
        var fileCount = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Session storage cannot contain reparse points.");
            }

            if (Directory.Exists(current))
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pending.Push(child);
                }

                continue;
            }

            totalBytes = checked(totalBytes + new FileInfo(current).Length);
            fileCount = checked(fileCount + 1);
        }

        return new SessionStorageUsage(totalBytes, fileCount);
    }
}
