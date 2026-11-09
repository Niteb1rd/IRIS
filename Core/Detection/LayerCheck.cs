namespace Iris.Core.Detection;

public enum LayerRule
{
    Brightest,

    Measure,

    Agree,
}

public sealed class LayerCheck
{
    public const int MaxVerdicts = 256;

    private readonly List<bool> _verdicts = new();

    public LayerRule Rule { get; set; }

    public bool Active => Rule != LayerRule.Brightest;

    public void Note(bool shifted)
    {
        lock (_verdicts)
        {
            if (_verdicts.Count < MaxVerdicts)
                _verdicts.Add(shifted);
        }
    }

    public IReadOnlyList<bool> TakeVerdicts()
    {
        lock (_verdicts)
        {
            if (_verdicts.Count == 0)
                return Array.Empty<bool>();
            var taken = _verdicts.ToArray();
            _verdicts.Clear();
            return taken;
        }
    }
}
