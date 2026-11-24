using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Log.Windows;

public sealed class WindowsGraphicsCaptureDeviceException : Exception
{
    public WindowsGraphicsCaptureDeviceException(string stage, int hresult, Exception? innerException = null)
        : base($"Windows Graphics Capture device creation failed during {stage} with HRESULT 0x{hresult:X8}.", innerException)
    {
        Stage = stage;
        HResultCode = hresult;
        HResult = hresult;
    }

    public string Stage { get; }
    public int HResultCode { get; }
}

/// <summary>
/// Creates the WinRT D3D device required by a free-threaded Graphics Capture frame pool.
/// </summary>
public static class WindowsGraphicsCaptureDevice
{
    private const int UnexpectedError = unchecked((int)0x8000FFFF);
    private const uint D3dDriverTypeHardware = 1;
    private const uint D3d11CreateDeviceBgraSupport = 0x20;
    private const uint D3d11SdkVersion = 7;
    private static readonly Guid DxgiDeviceIid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    public static IDirect3DDevice CreateHardware()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Graphics Capture requires Windows.");
        }

        nint nativeDevice = 0;
        nint immediateContext = 0;
        nint dxgiDevice = 0;
        nint graphicsDevice = 0;
        try
        {
            var hresult = D3D11CreateDevice(
                pAdapter: 0,
                driverType: D3dDriverTypeHardware,
                software: 0,
                flags: D3d11CreateDeviceBgraSupport,
                featureLevels: 0,
                featureLevelCount: 0,
                sdkVersion: D3d11SdkVersion,
                device: out nativeDevice,
                featureLevel: out _,
                immediateContext: out immediateContext);
            ThrowIfFailed("D3D11CreateDevice", hresult);
            ThrowIfNull("D3D11CreateDevice device", nativeDevice);
            ThrowIfNull("D3D11CreateDevice context", immediateContext);

            hresult = Marshal.QueryInterface(nativeDevice, in DxgiDeviceIid, out dxgiDevice);
            ThrowIfFailed("IDXGIDevice query", hresult);
            ThrowIfNull("IDXGIDevice query", dxgiDevice);

            hresult = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out graphicsDevice);
            ThrowIfFailed("WinRT D3D device projection", hresult);
            ThrowIfNull("WinRT D3D device projection", graphicsDevice);

            return MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
        }
        catch (WindowsGraphicsCaptureDeviceException)
        {
            throw;
        }
        catch (COMException exception)
        {
            throw new WindowsGraphicsCaptureDeviceException(
                "WinRT D3D device projection",
                exception.HResult,
                exception);
        }
        catch (InvalidCastException exception)
        {
            throw new WindowsGraphicsCaptureDeviceException(
                "WinRT D3D device projection",
                exception.HResult,
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new WindowsGraphicsCaptureDeviceException(
                "WinRT D3D device projection",
                exception.HResult,
                exception);
        }
        catch (DllNotFoundException exception)
        {
            throw new WindowsGraphicsCaptureDeviceException("D3D11 load", exception.HResult, exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new WindowsGraphicsCaptureDeviceException("D3D11 entry point lookup", exception.HResult, exception);
        }
        catch (BadImageFormatException exception)
        {
            throw new WindowsGraphicsCaptureDeviceException("D3D11 architecture check", exception.HResult, exception);
        }
        catch (FileLoadException exception)
        {
            throw new WindowsGraphicsCaptureDeviceException("D3D11 platform load", exception.HResult, exception);
        }
        catch (TypeLoadException exception)
        {
            throw new WindowsGraphicsCaptureDeviceException(
                "WinRT D3D device type load",
                exception.HResult,
                exception);
        }
        catch (TypeInitializationException exception)
        {
            throw new WindowsGraphicsCaptureDeviceException(
                "WinRT D3D device type initialization",
                exception.HResult,
                exception);
        }
        finally
        {
            ReleaseNoThrow(graphicsDevice);
            ReleaseNoThrow(dxgiDevice);
            ReleaseNoThrow(immediateContext);
            ReleaseNoThrow(nativeDevice);
        }
    }

    private static void ThrowIfFailed(string stage, int hresult)
    {
        if (hresult < 0)
        {
            throw new WindowsGraphicsCaptureDeviceException(stage, hresult);
        }
    }

    private static void ThrowIfNull(string stage, nint pointer)
    {
        if (pointer == 0)
        {
            throw new WindowsGraphicsCaptureDeviceException(stage, UnexpectedError);
        }
    }

    private static void ReleaseNoThrow(nint unknown)
    {
        if (unknown == 0)
        {
            return;
        }

        try
        {
            Marshal.Release(unknown);
        }
        catch
        {
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        nint pAdapter,
        uint driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint device,
        out uint featureLevel,
        out nint immediateContext);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        nint dxgiDevice,
        out nint graphicsDevice);
}
