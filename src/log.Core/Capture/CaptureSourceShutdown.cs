using System.Runtime.CompilerServices;

namespace Log.Core.Capture;

internal static class CaptureSourceShutdown
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);
    private static readonly ConditionalWeakTable<Task, object> TimedOutSources = new();

    public static async Task<bool> StopAsync(
        Task sourceTask,
        CancellationTokenSource sourceCancellation)
    {
        ArgumentNullException.ThrowIfNull(sourceTask);
        ArgumentNullException.ThrowIfNull(sourceCancellation);

        sourceCancellation.Cancel();
        if (TimedOutSources.TryGetValue(sourceTask, out _))
        {
            if (!sourceTask.IsCompleted)
            {
                return false;
            }

            return await ObserveCompletedSourceAsync(sourceTask).ConfigureAwait(false);
        }

        try
        {
            await sourceTask.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (sourceTask.IsCompleted)
            {
                return await ObserveCompletedSourceAsync(sourceTask).ConfigureAwait(false);
            }

            CaptureSessionRegistry.TrackLingeringSource(sourceTask);
            TimedOutSources.GetValue(sourceTask, static _ => new object());
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception exception) when (IsExpectedShutdownException(exception))
        {
            return true;
        }

        return true;
    }

    private static async Task<bool> ObserveCompletedSourceAsync(Task sourceTask)
    {
        try
        {
            await sourceTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsExpectedShutdownException(exception))
        {
        }

        return true;
    }

    private static bool IsExpectedShutdownException(Exception exception)
    {
        return exception is IOException
            or InvalidOperationException
            or ObjectDisposedException
            or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException;
    }
}
