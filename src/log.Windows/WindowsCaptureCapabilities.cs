using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace Log.Windows;

public sealed record WindowsCaptureCapabilities(
    bool ApiAvailable,
    bool IsSupported,
    int? FailureHResult)
{
    public static WindowsCaptureCapabilities Inspect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsCaptureCapabilities(false, false, null);
        }

        try
        {
            return new WindowsCaptureCapabilities(
                ApiAvailable: true,
                IsSupported: GraphicsCaptureSession.IsSupported(),
                FailureHResult: null);
        }
        catch (COMException exception)
        {
            return new WindowsCaptureCapabilities(false, false, exception.HResult);
        }
        catch (Exception exception) when (IsCapabilityProbeFailure(exception))
        {
            return new WindowsCaptureCapabilities(false, false, GetFailureHResult(exception));
        }
    }

    private static bool IsCapabilityProbeFailure(Exception? exception)
    {
        return exception is COMException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or FileLoadException
            or TypeLoadException
            or InvalidCastException
            or ArgumentException
            or PlatformNotSupportedException
            || exception is TypeInitializationException { InnerException: Exception innerException }
                && IsCapabilityProbeFailure(innerException);
    }

    private static int GetFailureHResult(Exception? exception)
    {
        return exception is TypeInitializationException { InnerException: Exception innerException }
            ? GetFailureHResult(innerException)
            : exception?.HResult ?? 0;
    }
}
