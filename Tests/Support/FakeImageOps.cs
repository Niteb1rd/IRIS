using Iris.Core.Overlay;
using Iris.Core.Pipeline;
using OpenCvSharp;

namespace Iris.Tests.Support;

public sealed class FakeImageOps : IPlateImageOps
{
    public IReadOnlyList<PixelRect> Lines { get; set; } = Array.Empty<PixelRect>();

    public Dictionary<PixelRect, double> Distances { get; } = new();

    public Action? OnCheck { get; set; }

    public bool NearOk { get; set; } = true;

    public PixelRect? NearLine { get; set; }

    public Func<ReadItem, ReadResult>? OnRead { get; set; }

    public bool InPlaceOk { get; set; }

    public int InPlaceReads { get; private set; }

    public PixelRect? LastInPlaceBox { get; private set; }

    public IReadOnlyDictionary<PixelRect, double> LastScanDistances => Distances;

    public IReadOnlyList<PixelRect> Scan(CropImage scan) => Lines;

    public PixelRect? Check(CheckItem item)
    {
        OnCheck?.Invoke();
        return null;
    }

    public ReadResult Read(ReadItem item) => OnRead?.Invoke(item) ?? new(ObservationClass.Unclear, "nothing");

    public NearRead ReadNear(CropImage crop, PixelRect expected, double margin) => new(NearOk, NearOk ? NearLine : null);

    public bool ReadDistance(CropImage crop, PixelRect line)
    {
        InPlaceReads++;
        LastInPlaceBox = line;
        return InPlaceOk;
    }

    public Mat? MakeTemplate(CropImage crop, PixelRect nameBox, out PlateColorClass templateClass)
    {
        templateClass = PlateColorClass.Any;
        return null;
    }
}
