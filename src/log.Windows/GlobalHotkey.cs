using System.Runtime.InteropServices;

namespace Log.Windows;

[Flags]
public enum GlobalHotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000
}

public sealed class GlobalHotkeyRegistration : IDisposable
{
    private readonly nint windowHandle;
    private readonly uint id;
    private int disposed;

    private GlobalHotkeyRegistration(nint windowHandle, uint id)
    {
        this.windowHandle = windowHandle;
        this.id = id;
    }

    public static GlobalHotkeyRegistration Register(
        nint windowHandle,
        uint id,
        GlobalHotkeyModifiers modifiers,
        uint virtualKey)
    {
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);
        GlobalHotkeyValidation.ValidateId(id);
        ArgumentOutOfRangeException.ThrowIfZero(virtualKey);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(virtualKey, byte.MaxValue);
        if (!GlobalHotkeyValidation.HasUserModifier(modifiers))
        {
            throw new ArgumentException(
                "A global hotkey must include at least one modifier.",
                nameof(modifiers));
        }

        if (GlobalHotkeyValidation.HasUnsupportedModifiers(modifiers))
        {
            throw new ArgumentException("The hotkey contains unsupported modifier bits.", nameof(modifiers));
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Global hotkeys require Windows.");
        }

        if (!RegisterHotKey(windowHandle, (int)id, (uint)modifiers, virtualKey))
        {
            throw new HotkeyRegistrationException(id, Marshal.GetLastWin32Error());
        }

        return new GlobalHotkeyRegistration(windowHandle, id);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        UnregisterHotKey(windowHandle, (int)id);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
