namespace Log.Core.Store;

public static class SessionIdPolicy
{
    public static void Validate(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (sessionId.Length > 64 || sessionId.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("Session IDs may contain only ASCII letters, digits, '-' and '_'.", nameof(sessionId));
        }
    }
}
