namespace Log.Core.Capture;

internal static class CaptureSourceHealth
{
    public static ValueTask WaitForRecordingAsync(
        CapturePauseTracker pauseTracker,
        Task sourceTask,
        CancellationTokenSource sourceHealthCancellation)
    {
        ArgumentNullException.ThrowIfNull(pauseTracker);
        ArgumentNullException.ThrowIfNull(sourceTask);
        ArgumentNullException.ThrowIfNull(sourceHealthCancellation);
        return WaitForAsync(
            pauseCancellationToken => pauseTracker.WaitForRecordingAsync(pauseCancellationToken),
            sourceHealthCancellation,
            [sourceTask]);
    }

    public static ValueTask WaitUntilAsync(
        CapturePauseTracker pauseTracker,
        long targetTicks,
        Task sourceTask,
        CancellationTokenSource sourceHealthCancellation)
    {
        ArgumentNullException.ThrowIfNull(pauseTracker);
        ArgumentNullException.ThrowIfNull(sourceTask);
        ArgumentNullException.ThrowIfNull(sourceHealthCancellation);
        return WaitForAsync(
            pauseCancellationToken => pauseTracker.WaitUntilAsync(targetTicks, pauseCancellationToken),
            sourceHealthCancellation,
            [sourceTask]);
    }

    public static ValueTask WaitForRecordingAsync(
        CapturePauseTracker pauseTracker,
        Task screenTask,
        Task cameraTask,
        CancellationTokenSource sourceHealthCancellation)
    {
        ArgumentNullException.ThrowIfNull(pauseTracker);
        ArgumentNullException.ThrowIfNull(screenTask);
        ArgumentNullException.ThrowIfNull(cameraTask);
        ArgumentNullException.ThrowIfNull(sourceHealthCancellation);
        return WaitForAsync(
            pauseCancellationToken => pauseTracker.WaitForRecordingAsync(pauseCancellationToken),
            sourceHealthCancellation,
            [screenTask, cameraTask]);
    }

    public static ValueTask WaitUntilAsync(
        CapturePauseTracker pauseTracker,
        long targetTicks,
        Task screenTask,
        Task cameraTask,
        CancellationTokenSource sourceHealthCancellation)
    {
        ArgumentNullException.ThrowIfNull(pauseTracker);
        ArgumentNullException.ThrowIfNull(screenTask);
        ArgumentNullException.ThrowIfNull(cameraTask);
        ArgumentNullException.ThrowIfNull(sourceHealthCancellation);
        return WaitForAsync(
            pauseCancellationToken => pauseTracker.WaitUntilAsync(targetTicks, pauseCancellationToken),
            sourceHealthCancellation,
            [screenTask, cameraTask]);
    }

    private static ValueTask WaitForAsync(
        Func<CancellationToken, ValueTask> operationFactory,
        CancellationTokenSource sourceHealthCancellation,
        IReadOnlyList<Task> sourceTasks)
    {
        return WaitForAsyncCore(operationFactory, sourceHealthCancellation, sourceTasks);
    }

    private static async ValueTask WaitForAsyncCore(
        Func<CancellationToken, ValueTask> operationFactory,
        CancellationTokenSource sourceHealthCancellation,
        IReadOnlyList<Task> sourceTasks)
    {
        var pendingSources = sourceTasks
            .Where(static sourceTask => !sourceTask.IsCompletedSuccessfully)
            .ToList();
        var operation = operationFactory(sourceHealthCancellation.Token).AsTask();
        while (pendingSources.Count != 0)
        {
            for (var index = pendingSources.Count - 1; index >= 0; index--)
            {
                var sourceTask = pendingSources[index];
                if (!sourceTask.IsCompleted)
                {
                    continue;
                }

                if (sourceTask.IsCompletedSuccessfully)
                {
                    pendingSources.RemoveAt(index);
                    continue;
                }

                await CancelAndObserveOperation(operation, sourceHealthCancellation).ConfigureAwait(false);
                await sourceTask.ConfigureAwait(false);
                pendingSources.RemoveAt(index);
            }

            if (operation.IsCompleted)
            {
                await PropagateCompletedSourceFaultAsync(
                    pendingSources,
                    operation,
                    sourceHealthCancellation).ConfigureAwait(false);
                await operation.ConfigureAwait(false);
                return;
            }

            if (pendingSources.Count == 0)
            {
                await operation.ConfigureAwait(false);
                return;
            }

            var sourceCompletion = Task.WhenAny(pendingSources);
            var completed = await Task.WhenAny(operation, sourceCompletion).ConfigureAwait(false);
            if (completed == operation)
            {
                await PropagateCompletedSourceFaultAsync(
                    pendingSources,
                    operation,
                    sourceHealthCancellation).ConfigureAwait(false);
                await operation.ConfigureAwait(false);
                return;
            }

            var completedSource = await sourceCompletion.ConfigureAwait(false);
            if (!completedSource.IsCompletedSuccessfully)
            {
                await CancelAndObserveOperation(operation, sourceHealthCancellation).ConfigureAwait(false);
                await completedSource.ConfigureAwait(false);
            }
        }

        await operation.ConfigureAwait(false);
    }

    private static async ValueTask PropagateCompletedSourceFaultAsync(
        IReadOnlyList<Task> pendingSources,
        Task operation,
        CancellationTokenSource sourceHealthCancellation)
    {
        foreach (var sourceTask in pendingSources)
        {
            if (!sourceTask.IsCompleted || sourceTask.IsCompletedSuccessfully)
            {
                continue;
            }

            await CancelAndObserveOperation(operation, sourceHealthCancellation)
                .ConfigureAwait(false);
            await sourceTask.ConfigureAwait(false);
        }
    }

    private static ValueTask CancelAndObserveOperation(
        Task operation,
        CancellationTokenSource sourceHealthCancellation)
    {
        sourceHealthCancellation.Cancel();
        return ObserveOperationAsync(operation);
    }

    private static async ValueTask ObserveOperationAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch
        {
            // The source fault is the failure being propagated; still observe the wait's cancellation.
        }
    }
}
