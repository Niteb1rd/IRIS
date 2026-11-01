namespace Iris.Core.Lookup;

public enum RsiStatus
{
    Found,

    NotFound,

    Failed,
}

public sealed class RsiResult<T>
{
    private RsiResult(RsiStatus status, T? data, string? reason)
    {
        Status = status;
        Data = data;
        Reason = reason;
    }

    public RsiStatus Status { get; }

    public T? Data { get; }

    public string? Reason { get; }

    public bool IsFound => Status == RsiStatus.Found;

    public static RsiResult<T> Found(T data) => new(RsiStatus.Found, data, null);

    public static RsiResult<T> NotFound() => new(RsiStatus.NotFound, default, null);

    public static RsiResult<T> Failed(string reason) => new(RsiStatus.Failed, default, reason);

    public override string ToString() => Status switch
    {
        RsiStatus.Found => "Found",
        RsiStatus.NotFound => "NotFound",
        _ => $"Failed({Reason})",
    };
}
