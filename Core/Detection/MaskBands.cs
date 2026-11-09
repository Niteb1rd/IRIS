namespace Iris.Core.Detection;

public readonly record struct MaskBand(int From, int Rows, int KeepFrom, int KeepRows);

public static class MaskBands
{
    public static IReadOnlyList<MaskBand> Plan(int rows, int bands, int overlap, int alignment)
    {
        if (bands <= 1 || alignment < 1 || rows % alignment != 0 || rows < bands * (2 * overlap + alignment))
            return new[] { new MaskBand(0, rows, 0, rows) };

        var plan = new MaskBand[bands];
        for (var i = 0; i < bands; i++)
        {
            var keepFrom = Cut(rows, bands, i, alignment);
            var keepEnd = Cut(rows, bands, i + 1, alignment);
            var from = i == 0 ? 0 : AlignDown(keepFrom - overlap, alignment);
            var end = i == bands - 1 ? rows : Math.Min(rows, AlignUp(keepEnd + overlap, alignment));
            plan[i] = new MaskBand(from, end - from, keepFrom, keepEnd - keepFrom);
        }

        return plan;
    }

    private static int Cut(int rows, int bands, int index, int alignment) =>
        index == bands ? rows : AlignDown(rows * index / bands, alignment);

    private static int AlignDown(int value, int alignment) => value / alignment * alignment;

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
}
