using System.Globalization;
using System.Text;
using Iris.Core.Overlay;

namespace Iris.Core;

public interface ITraceSink
{
    bool Enabled { get; }

    void Write(string line);
}

public static class TraceSinkExtensions
{
    public static TraceLine? Line(this ITraceSink? sink, double t, string thread, string ev) =>
        sink is { Enabled: true } ? TraceLine.Begin(t, thread, ev) : null;
}

public sealed class TraceLine
{
    [ThreadStatic]
    private static TraceLine? _current;

    private readonly StringBuilder _text = new(256);

    private TraceLine()
    {
    }

    public static TraceLine Begin(double t, string thread, string ev)
    {
        var line = _current ??= new TraceLine();
        line._text.Clear();
        line._inArray = false;
        line._firstInObject = false;
        line._text.Append('{');
        line.Field("t").Number(t, 2);
        line.Str("thr", thread).Str("ev", ev);
        return line;
    }

    public TraceLine Num(string name, double value, int decimals = 2)
    {
        Field(name).Number(value, decimals);
        return this;
    }

    public TraceLine Int(string name, long value)
    {
        Field(name)._text.Append(value.ToString(CultureInfo.InvariantCulture));
        return this;
    }

    public TraceLine Bool(string name, bool value)
    {
        Field(name)._text.Append(value ? "true" : "false");
        return this;
    }

    public TraceLine Str(string name, string? value)
    {
        Field(name);
        if (value is null)
            _text.Append("null");
        else
            Quoted(value);
        return this;
    }

    public TraceLine Box(string name, PixelRect box)
    {
        Field(name)._text.Append('[');
        Number(box.X, 1)._text.Append(',');
        Number(box.Y, 1)._text.Append(',');
        Number(box.Width, 1)._text.Append(',');
        Number(box.Height, 1)._text.Append(']');
        return this;
    }

    public TraceLine Array(string name)
    {
        Field(name)._text.Append('[');
        _inArray = true;
        _firstItem = true;
        return this;
    }

    public TraceLine Item()
    {
        if (!_firstItem)
            _text.Append("},");
        _firstItem = false;
        _text.Append('{');
        _firstInObject = true;
        return this;
    }

    public TraceLine EndArray()
    {
        if (!_firstItem)
            _text.Append('}');
        _text.Append(']');
        _inArray = false;
        _firstInObject = false;
        return this;
    }

    public string Text()
    {
        _text.Append('}');
        return _text.ToString();
    }

    private bool _inArray;
    private bool _firstItem;
    private bool _firstInObject;

    private TraceLine Field(string name)
    {
        if (_inArray && _firstInObject)
            _firstInObject = false;
        else if (_text.Length > 1)
            _text.Append(',');
        Quoted(name);
        _text.Append(':');
        return this;
    }

    private TraceLine Number(double value, int decimals)
    {
        if (!double.IsFinite(value))
        {
            _text.Append("null");
            return this;
        }

        var rounded = Math.Round(value, decimals);
        _text.Append(rounded.ToString(rounded == Math.Floor(rounded) ? "0" : "0.##########", CultureInfo.InvariantCulture));
        return this;
    }

    private void Quoted(string value)
    {
        _text.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    _text.Append("\\\"");
                    break;
                case '\\':
                    _text.Append("\\\\");
                    break;
                default:
                    if (c < ' ')
                        _text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        _text.Append(c);
                    break;
            }
        }

        _text.Append('"');
    }
}
