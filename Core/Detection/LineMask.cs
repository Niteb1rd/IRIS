namespace Iris.Core.Detection;

public sealed class LineMask
{
    public LineMask(int height, int width, byte[] data)
    {
        if (height <= 0 || width <= 0 || data.Length != height * width)
            throw new ArgumentException("Mask data does not match its size.");
        Height = height;
        Width = width;
        Data = data;
    }

    public int Height { get; }

    public int Width { get; }

    public byte[] Data { get; }

    public int OriginX { get; init; }

    public int OriginY { get; init; }

    public int ColumnOffset { get; init; }

    public int RowOffset { get; init; }

    public byte[]? InkColors { get; init; }
}
