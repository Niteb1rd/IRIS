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

    public byte this[int y, int x] => Data[y * Width + x];

    public static LineMask FromPackedBits(int height, int width, byte[] bits)
    {
        var data = new byte[checked(height * width)];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)((bits[i >> 3] >> (7 - (i & 7))) & 1);
        return new LineMask(height, width, data);
    }
}
