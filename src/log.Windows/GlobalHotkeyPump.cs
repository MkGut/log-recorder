using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Log.Windows;

public sealed record GlobalHotkeyBinding
{
    public GlobalHotkeyBinding(
        uint id,
        GlobalHotkeyModifiers modifiers,
        uint virtualKey)
    {
        GlobalHotkeyValidation.ValidateId(id);
        ArgumentOutOfRangeException.ThrowIfZero(virtualKey);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(virtualKey, byte.MaxValue);
        if (!GlobalHotkeyValidation.HasUserModifier(modifiers))
        {
            throw new ArgumentException("A global hotkey must include at least one modifier.", nameof(modifiers));
        }

        if (GlobalHotkeyValidation.HasUnsupportedModifiers(modifiers))
        {
            throw new ArgumentException("The hotkey contains unsupported modifier bits.", nameof(modifiers));
        }

        Id = id;
        Modifiers = modifiers;
        VirtualKey = virtualKey;
    }

    public uint Id { get; }
    public GlobalHotkeyModifiers Modifiers { get; }
    public uint VirtualKey { get; }
}

public sealed class HotkeyRegistrationException : Win32Exception
{
    public HotkeyRegistrationException(uint hotkeyId, int nativeErrorCode)
        : base(nativeErrorCode, $"Windows could not register global hotkey {hotkeyId}.")
    {
        HotkeyId = hotkeyId;
    }

    public uint HotkeyId { get; }
    public bool IsCollision => NativeErrorCode == 1409;
}

internal static class GlobalHotkeyValidation
{
    private const uint MaxApplicationHotkeyId = 0xBFFF;
    private const GlobalHotkeyModifiers SupportedModifiers = GlobalHotkeyModifiers.Alt
        | GlobalHotkeyModifiers.Control
        | GlobalHotkeyModifiers.Shift
        | GlobalHotkeyModifiers.Windows
        | GlobalHotkeyModifiers.NoRepeat;

    public static bool HasUserModifier(GlobalHotkeyModifiers modifiers)
    {
        return (modifiers & (GlobalHotkeyModifiers.Alt
            | GlobalHotkeyModifiers.Control
            | GlobalHotkeyModifiers.Shift
            | GlobalHotkeyModifiers.Windows)) != 0;
    }

    public static bool HasUnsupportedModifiers(GlobalHotkeyModifiers modifiers)
    {
        return ((uint)modifiers & ~(uint)SupportedModifiers) != 0;
    }

    public static void ValidateId(uint id)
    {
        ArgumentOutOfRangeException.ThrowIfZero(id);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(id, MaxApplicationHotkeyId);
    }
}

public sealed class GlobalHotkeyPump : IDisposable
{
    private const uint Infinite = 0xFFFF_FFFF;
    private const uint QsAllInput = 0x04FF;
    private const uint MwmoInputAvailable = 0x0004;
    private const uint WaitObject0 = 0;
    private const uint WaitFailed = 0xFFFF_FFFF;
    private const uint WmQuit = 0x0012;
    private const uint WmHotkey = 0x0312;

    private readonly (GlobalHotkeyBinding Binding, Action Handler)[] registrations;
    private readonly Action<Exception>? handlerFaulted;
    private readonly ManualResetEventSlim started = new(false);
    private readonly ManualResetEvent stopRequested = new(false);
    private readonly Thread thread;
    private Exception? startupException;
    private int startedDisposed;
    private int stopRequestedDisposed;
    private int disposed;

    public GlobalHotkeyPump(
        IEnumerable<(GlobalHotkeyBinding Binding, Action Handler)> registrations,
        Action<Exception>? handlerFaulted = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Global hotkeys require Windows.");
        }

        this.handlerFaulted = handlerFaulted;
        this.registrations = registrations
            .Select(static registration =>
            {
                ArgumentNullException.ThrowIfNull(registration.Binding);
                ArgumentNullException.ThrowIfNull(registration.Handler);
                return registration;
            })
            .GroupBy(static registration => registration.Binding.Id)
            .Select(static group => group.Count() == 1
                ? group.Single()
                : throw new ArgumentException("Hotkey IDs must be unique.", nameof(registrations)))
            .ToArray();
        if (this.registrations.Length == 0)
        {
            throw new ArgumentException("At least one hotkey registration is required.", nameof(registrations));
        }

        thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "log global hotkey"
        };
        try
        {
            thread.Start();
        }
        catch
        {
            DisposeStartedSignal();
            DisposeStopSignal();
            throw;
        }

        started.Wait();
        if (startupException is not null)
        {
            thread.Join();
            DisposeStartedSignal();
            DisposeStopSignal();
            throw startupException;
        }
    }

    public bool IsActive => Volatile.Read(ref disposed) == 0 && thread.IsAlive;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            stopRequested.Set();
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref stopRequestedDisposed) != 0)
        {
            // The pump may have completed and disposed its signal concurrently.
        }

        if (thread.IsAlive && !ReferenceEquals(Thread.CurrentThread, thread))
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }

        // Keep the signal alive if native teardown outlives the bounded join.
        // The pump thread disposes it after a late self-initiated exit.
        if (!thread.IsAlive)
        {
            DisposeStartedSignal();
            DisposeStopSignal();
        }
    }

    private void Pump()
    {
        var registeredIds = new List<uint>(registrations.Length);
        try
        {
            // Thread-associated hotkeys require a message queue on this pump thread.
            PeekMessage(out _, nint.Zero, 0, 0, removeMessage: false);
            foreach (var registration in registrations)
            {
                if (!RegisterHotKey(nint.Zero, (int)registration.Binding.Id, (uint)registration.Binding.Modifiers, registration.Binding.VirtualKey))
                {
                    throw new HotkeyRegistrationException(
                        registration.Binding.Id,
                        Marshal.GetLastWin32Error());
                }

                registeredIds.Add(registration.Binding.Id);
            }

            started.Set();
            var waitHandles = new[] { stopRequested.SafeWaitHandle.DangerousGetHandle() };
            while (true)
            {
                var waitResult = MsgWaitForMultipleObjectsEx(
                    (uint)waitHandles.Length,
                    waitHandles,
                    Infinite,
                    QsAllInput,
                    MwmoInputAvailable);
                if (waitResult == WaitObject0)
                {
                    return;
                }

                if (waitResult == WaitFailed)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The global hotkey message pump failed.");
                }

                if (waitResult != WaitObject0 + (uint)waitHandles.Length)
                {
                    throw new Win32Exception("The global hotkey message pump wait failed.");
                }

                while (PeekMessage(out var message, nint.Zero, 0, 0, removeMessage: true))
                {
                    if (message.Message == WmQuit)
                    {
                        return;
                    }

                    if (message.Message == WmHotkey)
                    {
                        var registration = registrations.FirstOrDefault(item => item.Binding.Id == (uint)message.WParam);
                        if (registration.Binding is not null
                            && Volatile.Read(ref disposed) == 0)
                        {
                            try
                            {
                                registration.Handler();
                            }
                            catch (Exception exception)
                            {
                                ReportFault(exception);
                            }
                        }
                    }
                }

                if (Volatile.Read(ref disposed) != 0)
                {
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            if (started.IsSet)
            {
                ReportFault(exception);
            }
            else
            {
                startupException = exception;
                started.Set();
            }
        }
        finally
        {
            foreach (var id in registeredIds)
            {
                UnregisterHotKey(nint.Zero, (int)id);
            }

            if (Volatile.Read(ref disposed) != 0)
            {
                DisposeStartedSignal();
                DisposeStopSignal();
            }
        }
    }

    private void DisposeStartedSignal()
    {
        if (Interlocked.Exchange(ref startedDisposed, 1) == 0)
        {
            started.Dispose();
        }
    }

    private void DisposeStopSignal()
    {
        if (Interlocked.Exchange(ref stopRequestedDisposed, 1) == 0)
        {
            stopRequested.Dispose();
        }
    }

    private void ReportFault(Exception exception)
    {
        if (handlerFaulted is null)
        {
            return;
        }

        try
        {
            handlerFaulted(exception);
        }
        catch
        {
            // A reporting callback must not take down the global hotkey pump.
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out NativeMessage lpMsg,
        nint hWnd,
        uint wMsgFilterMin,
        uint wMsgFilterMax,
        [MarshalAs(UnmanagedType.Bool)] bool removeMessage);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MsgWaitForMultipleObjectsEx(
        uint count,
        nint[] handles,
        uint milliseconds,
        uint wakeMask,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint HWnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }
}
