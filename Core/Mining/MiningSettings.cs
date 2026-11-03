namespace Iris.Core.Mining;

public sealed record MiningSettings
{
    public static MiningSettings Default { get; } = new();

    public double LockHz { get; init; } = 12.0;

    public double HoldMs { get; init; } = 1000.0;

    public int OffTableReads { get; init; } = 2;

    public int ChangeReads { get; init; } = 2;

    public double ClearDigitMargin { get; init; } = 0.08;

    public double ConfidentMargin { get; init; } = 0.08;

    public int AverageFrames { get; init; } = 4;

    public int FuseReads { get; init; } = 5;

    public double LineHeightAt1440 { get; init; } = 12.0;

    public double RowCentreY { get; init; } = 472.0;

    public double SearchHalfHeightLines { get; init; } = 4.0;

    public double LockAboveLines { get; init; } = 1.5;

    public double LockBelowLines { get; init; } = 2.5;

    public double LeftLines { get; init; } = 6.0;

    public double RightLines { get; init; } = 11.0;

    public double CentreOffsetLines { get; init; } = 0.4;

    public double CentreToleranceLines { get; init; } = 1.0;

    public double MinDigitScore { get; init; } = 0.55;

    public double TieMargin { get; init; } = 0.08;

    public double SplitWidthLines { get; init; } = 0.8;

    public double MinInkThreshold { get; init; } = 12.0;

    public double LabelGapLines { get; init; } = 0.6;

    public double LabelPadXLines { get; init; } = 0.5;

    public double LabelPadYLines { get; init; } = 0.25;

    public byte LabelBackAlpha { get; init; } = 0x80;

    public double ColourBlend { get; init; } = 0.3;

    public int ColourStep { get; init; } = 12;

    public double ColourUpdateMs { get; init; } = 250.0;

    public double MinContrast { get; init; } = 3.0;

    public int KeptCropsPerRun { get; init; } = 300;
}
