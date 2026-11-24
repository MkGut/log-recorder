using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT.Interop;

namespace Log.Windows;

public sealed class WindowsGraphicsCaptureConsentException : Exception
{
    public WindowsGraphicsCaptureConsentException(string stage, int hresult, Exception? innerException = null)
        : base($"Windows Graphics Capture consent failed during {stage} with HRESULT 0x{hresult:X8}.", innerException)
    {
        Stage = stage;
        HResultCode = hresult;
        HResult = hresult;
    }

    public string Stage { get; }
    public int HResultCode { get; }
}

/// <summary>
/// Brokers the OS-mediated capture picker for a desktop shell window.
/// </summary>
public static class WindowsGraphicsCapturePicker
{
    public static async Task<GraphicsCaptureItem?> PickAsync(
        nint ownerWindowHandle,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(ownerWindowHandle);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Graphics Capture requires Windows.");
        }

        try
        {
            var picker = new GraphicsCapturePicker();
            try
            {
                InitializePicker(picker, ownerWindowHandle);
                cancellationToken.ThrowIfCancellationRequested();
                var pickTask = picker.PickSingleItemAsync().AsTask();
                try
                {
                    return await pickTask.WaitAsync(cancellationToken).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // AsTask(token) cancels only the managed wait; release a late COM result.
                    _ = pickTask.ContinueWith(
                        static task =>
                        {
                            if (task.Status == TaskStatus.RanToCompletion && task.Result is { } item)
                            {
                                ReleaseComObjectNoThrow(item);
                            }
                            else
                            {
                                _ = task.Exception;
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    throw;
                }
            }
            finally
            {
                ReleaseComObjectNoThrow(picker);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WindowsGraphicsCaptureConsentException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new WindowsGraphicsCaptureConsentException(
                "system picker consent",
                exception.HResult,
                exception);
        }
        catch (COMException exception)
        {
            throw new WindowsGraphicsCaptureConsentException("system picker", exception.HResult, exception);
        }
        catch (InvalidCastException exception)
        {
            throw new WindowsGraphicsCaptureConsentException(
                "desktop picker initialization",
                exception.HResult,
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new WindowsGraphicsCaptureConsentException(
                "desktop picker initialization",
                exception.HResult,
                exception);
        }
        catch (DllNotFoundException exception)
        {
            throw new WindowsGraphicsCaptureConsentException("system picker platform load", exception.HResult, exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new WindowsGraphicsCaptureConsentException(
                "system picker platform entry point lookup",
                exception.HResult,
                exception);
        }
        catch (BadImageFormatException exception)
        {
            throw new WindowsGraphicsCaptureConsentException(
                "system picker platform architecture check",
                exception.HResult,
                exception);
        }
        catch (FileLoadException exception)
        {
            throw new WindowsGraphicsCaptureConsentException("system picker platform load", exception.HResult, exception);
        }
        catch (TypeLoadException exception)
        {
            throw new WindowsGraphicsCaptureConsentException(
                "system picker platform type load",
                exception.HResult,
                exception);
        }
        catch (TypeInitializationException exception)
        {
            throw new WindowsGraphicsCaptureConsentException(
                "system picker platform type initialization",
                exception.HResult,
                exception);
        }
    }

    private static void InitializePicker(GraphicsCapturePicker picker, nint ownerWindowHandle)
    {
        InitializeWithWindow.Initialize(picker, ownerWindowHandle);
    }

    private static void ReleaseComObjectNoThrow(object value)
    {
        try
        {
            if (Marshal.IsComObject(value))
            {
                Marshal.ReleaseComObject(value);
            }
        }
        catch
        {
            // Picker teardown must not mask a consent result.
        }
    }
}
