namespace Log.Core.Store;

public static class SessionIdGenerator
{
    public static string Create(DateTimeOffset now)
    {
        var id = $"{now:yyyy-MM-dd}_s_{Guid.NewGuid():N}"[..23];
        SessionIdPolicy.Validate(id);
        return id;
    }
}
