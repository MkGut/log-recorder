using System.Runtime.InteropServices;

namespace Log.Windows;

public sealed class MediaFoundationException : Exception
{
    public MediaFoundationException(string stage, int hresult, Exception? innerException = null)
        : base($"Media Foundation failed during {stage} with HRESULT 0x{hresult:X8}.", innerException)
    {
        Stage = stage;
        HResultCode = hresult;
        HResult = hresult;
    }

    public string Stage { get; }
    public int HResultCode { get; }
}

public sealed class MediaFoundationSession : IDisposable
{
    public const uint MfVersion = 0x0002_0070;
    private static readonly object RuntimeGate = new();
    private static int activeSessionCount;
    private bool disposed;

    private MediaFoundationSession()
    {
    }

    public static MediaFoundationSession Start()
    {
        lock (RuntimeGate)
        {
            var session = new MediaFoundationSession();
            try
            {
                if (activeSessionCount == 0)
                {
                    var hresult = MFStartup(MfVersion, flags: 0);
                    if (hresult < 0)
                    {
                        throw new MediaFoundationException("startup", hresult);
                    }
                }

                activeSessionCount++;
                return session;
            }
            catch (DllNotFoundException exception)
            {
                throw new MediaFoundationException("startup", exception.HResult, exception);
            }
            catch (EntryPointNotFoundException exception)
            {
                throw new MediaFoundationException("startup", exception.HResult, exception);
            }
            catch (BadImageFormatException exception)
            {
                throw new MediaFoundationException("startup", exception.HResult, exception);
            }
            catch (FileLoadException exception)
            {
                throw new MediaFoundationException("startup", exception.HResult, exception);
            }
            catch (TypeLoadException exception)
            {
                throw new MediaFoundationException("startup", exception.HResult, exception);
            }
            catch (TypeInitializationException exception)
            {
                throw new MediaFoundationException("startup type initialization", exception.HResult, exception);
            }
        }
    }

    public void Dispose()
    {
        lock (RuntimeGate)
        {
            if (disposed)
            {
                return;
            }

            MediaFoundationException? shutdownFailure = null;
            if (activeSessionCount == 1)
            {
                try
                {
                    var hresult = MFShutdown();
                    if (hresult < 0)
                    {
                        shutdownFailure = new MediaFoundationException("shutdown", hresult);
                    }
                }
                catch (DllNotFoundException exception)
                {
                    shutdownFailure = new MediaFoundationException("shutdown", exception.HResult, exception);
                }
                catch (EntryPointNotFoundException exception)
                {
                    shutdownFailure = new MediaFoundationException("shutdown", exception.HResult, exception);
                }
                catch (BadImageFormatException exception)
                {
                    shutdownFailure = new MediaFoundationException("shutdown", exception.HResult, exception);
                }
                catch (FileLoadException exception)
                {
                    shutdownFailure = new MediaFoundationException("shutdown", exception.HResult, exception);
                }
                catch (TypeLoadException exception)
                {
                    shutdownFailure = new MediaFoundationException("shutdown", exception.HResult, exception);
                }
                catch (TypeInitializationException exception)
                {
                    shutdownFailure = new MediaFoundationException(
                        "shutdown type initialization",
                        exception.HResult,
                        exception);
                }
                catch (Exception exception)
                {
                    shutdownFailure = new MediaFoundationException("shutdown", exception.HResult, exception);
                }
            }

            if (shutdownFailure is not null)
            {
                throw shutdownFailure;
            }

            if (activeSessionCount > 0)
            {
                activeSessionCount--;
            }

            disposed = true;
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();
}
