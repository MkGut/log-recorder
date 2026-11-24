using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Log.Windows;

public static class CaptureWindowPolicy
{
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    public static void ExcludeFromCapture(nint windowHandle)
    {
        SetCaptureExclusion(windowHandle, exclude: true);
    }

    /// <summary>
    /// Chooses whether the window is hidden from screen captures (screenshots,
    /// recordings, screen sharing). Passing <see langword="false"/> clears the
    /// affinity so the window appears in captures like any other window.
    /// </summary>
    public static void SetCaptureExclusion(nint windowHandle, bool exclude)
    {
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Window capture affinity requires Windows.");
        }

        var affinity = exclude ? WdaExcludeFromCapture : WdaNone;
        if (!SetWindowDisplayAffinity(windowHandle, affinity))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not set the log window capture affinity.");
        }

        if (!GetWindowDisplayAffinity(windowHandle, out var applied))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not verify the log window capture affinity.");
        }

        if (applied != affinity)
        {
            throw new InvalidOperationException("Windows did not confirm the requested capture affinity for the log window.");
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint hWnd, out uint dwAffinity);
}
