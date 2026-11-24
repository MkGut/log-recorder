using Log.Core.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinBuffer = global::Windows.Storage.Streams.Buffer;

namespace Log.Windows;

internal static class WindowsFramePixelCopy
{
    public static async Task<FrameSample> CopySurfaceAsync(
        IDirect3DSurface surface,
        long sourceTimestampTicks,
        long arrivalTimestampTicks,
        FrameTimestampQuality timestampQuality,
        int sourceEpoch = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        cancellationToken.ThrowIfCancellationRequested();
        var copyTask = SoftwareBitmap.CreateCopyFromSurfaceAsync(surface).AsTask();
        SoftwareBitmap bitmap;
        try
        {
            bitmap = await copyTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = copyTask.ContinueWith(
                static task => DisposeLateBitmapResult(task),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }

        try
        {
            return CopyBitmap(
                bitmap,
                sourceTimestampTicks,
                arrivalTimestampTicks,
                timestampQuality,
                sourceEpoch);
        }
        finally
        {
            DisposeNoThrow(bitmap);
        }
    }

    private static void DisposeLateBitmapResult(Task<SoftwareBitmap> copyTask)
    {
        if (copyTask.Status == TaskStatus.RanToCompletion)
        {
            DisposeNoThrow(copyTask.Result);
        }
        else
        {
            _ = copyTask.Exception;
        }
    }

    public static FrameSample CopyBitmap(
        SoftwareBitmap bitmap,
        long sourceTimestampTicks,
        long arrivalTimestampTicks,
        FrameTimestampQuality timestampQuality,
        int sourceEpoch = 0)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var bgraBitmap = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? bitmap
            : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        try
        {
            var width = bgraBitmap.PixelWidth;
            var height = bgraBitmap.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                throw new InvalidDataException("Windows returned an empty camera frame.");
            }

            var byteCount = checked(width * height * 4);
            var buffer = new WinBuffer((uint)byteCount)
            {
                Length = (uint)byteCount
            };
            bgraBitmap.CopyToBuffer(buffer);
            var payload = new byte[byteCount];
            using (var reader = DataReader.FromBuffer(buffer))
            {
                reader.ReadBytes(payload);
            }

            return new FrameSample(
                sourceTimestampTicks,
                payload,
                width,
                height,
                arrivalTimestampTicks,
                timestampQuality,
                sourceEpoch);
        }
        finally
        {
            if (!ReferenceEquals(bgraBitmap, bitmap))
            {
                DisposeNoThrow(bgraBitmap);
            }
        }
    }

    private static void DisposeNoThrow(SoftwareBitmap? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch
        {
        }
    }
}
