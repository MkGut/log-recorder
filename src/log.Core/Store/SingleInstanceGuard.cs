using System.Security.Cryptography;
using System.Text;

namespace Log.Core.Store;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly EventWaitHandle instanceToken;
    private readonly Action? activationRequested;
    private readonly RegisteredWaitHandle? activationWait;
    private int disposed;

    private SingleInstanceGuard(EventWaitHandle instanceToken, Action? activationRequested)
    {
        this.instanceToken = instanceToken;
        this.activationRequested = activationRequested;
        if (activationRequested is not null)
        {
            activationWait = ThreadPool.RegisterWaitForSingleObject(
                instanceToken,
                OnActivationSignal,
                this,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
    }

    public static bool TryAcquire(string applicationId, out SingleInstanceGuard? guard)
    {
        return TryAcquire(applicationId, activationRequested: null, out guard);
    }

    public static bool TryAcquire(
        string applicationId,
        Action? activationRequested,
        out SingleInstanceGuard? guard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        if (applicationId.Length > 64 || applicationId.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("Application IDs may contain only ASCII letters, digits, '-' and '_'.", nameof(applicationId));
        }

        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var identityHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        var instanceName = $"{applicationId}-{identityHash}";
        var waitHandleOptions = new NamedWaitHandleOptions
        {
            CurrentUserOnly = true,
            CurrentSessionOnly = true // Local namespace: another user cannot pre-create the name
        };
        EventWaitHandle instanceToken;
        bool createdNew;
        // The event both marks ownership and carries coalesced activation requests.
        try
        {
            instanceToken = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                instanceName,
                waitHandleOptions,
                out createdNew);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // A different named kernel object must not permit a second instance.
            guard = null;
            return false;
        }

        if (!createdNew)
        {
            _ = instanceToken.Set();
            instanceToken.Dispose();
            guard = null;
            return false;
        }

        try
        {
            guard = new SingleInstanceGuard(instanceToken, activationRequested);
            return true;
        }
        catch
        {
            instanceToken.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        activationWait?.Unregister(null);
        instanceToken.Dispose();
    }

    private static void OnActivationSignal(object? state, bool timedOut)
    {
        if (timedOut
            || state is not SingleInstanceGuard guard
            || Volatile.Read(ref guard.disposed) != 0)
        {
            return;
        }

        guard.activationRequested?.Invoke();
    }
}
