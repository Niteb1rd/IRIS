namespace Iris.Core;

public interface ILog
{
    void Write(DateTimeOffset utcTime, string message);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public void Write(DateTimeOffset utcTime, string message)
    {
    }
}
