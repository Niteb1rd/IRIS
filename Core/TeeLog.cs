namespace Iris.Core;

public sealed class TeeLog : ILog
{
    private readonly ILog[] _targets;

    public TeeLog(params ILog[] targets)
    {
        _targets = targets;
    }

    public void Write(DateTimeOffset utcTime, string message)
    {
        foreach (var target in _targets)
        {
            try
            {
                target.Write(utcTime, message);
            }
            catch
            {
            }
        }
    }
}
