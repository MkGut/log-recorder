using System.Collections.Concurrent;

namespace Log.Core.Capture;

public static class CaptureSessionRegistry
{
    private static readonly ConcurrentDictionary<string, byte> ActiveRoots = new(
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<Task> LingeringSourceTasks = [];
    private static readonly object RegistryGate = new();

    public static IDisposable Acquire(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        lock (RegistryGate)
        {
            LingeringSourceTasks.RemoveWhere(static sourceTask => sourceTask.IsCompleted);
            if (!ActiveRoots.IsEmpty || LingeringSourceTasks.Count != 0)
            {
                throw new InvalidOperationException("A capture is already active.");
            }

            ActiveRoots[normalizedRoot] = 0;
        }

        return new Lease(normalizedRoot);
    }

    internal static void TrackLingeringSource(Task sourceTask)
    {
        ArgumentNullException.ThrowIfNull(sourceTask);
        lock (RegistryGate)
        {
            if (sourceTask.IsCompleted)
            {
                _ = sourceTask.Exception;
                return;
            }

            LingeringSourceTasks.Add(sourceTask);
        }

        _ = sourceTask.ContinueWith(
            static completedTask =>
            {
                lock (RegistryGate)
                {
                    LingeringSourceTasks.Remove(completedTask);
                }

                _ = completedTask.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class Lease(string rootDirectory) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                lock (RegistryGate)
                {
                    ActiveRoots.TryRemove(rootDirectory, out _);
                }
            }
        }
    }
}
