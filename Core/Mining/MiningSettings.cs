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

    public double SnapChoiceMargin { get; init; } = 0.08;

    public int AverageFrames { get; init; } = 4;

    public int FuseReads { get; init; } = 5;

    public int GeometryReads { get; init; } = 5;

    public int GeometryMinReads { get; init; } = 3;

    public double GeometryChangePx { get; init; } = 1.0;

    public double LabelSizeChangePx { get; init; } = 2.0;

    public double LabelSizeChangeShare { get; init; } = 0.15;

    public double LineHeightAt1440 { get; init; } = 12.0;

    public double RowCentreY { get; init; } = 472.0;

    public double SearchAboveLines { get; init; } = 4.0;

    public double SearchBelowLines { get; init; } = 11.0;

    public double RowSearchHalfLines { get; init; } = 4.0;

    public int PinHeightMin { get; init; } = 13;

    public int PinHeightMax { get; init; } = 26;

    public double PinMinScore { get; init; } = 0.7;

    public double PinDigitRatio { get; init; } = 0.5;

    public double SmallRowLines { get; init; } = 0.75;

    public double LockAboveLines { get; init; } = 1.5;

    public double LockBelowLines { get; init; } = 2.5;

    public double LeftLines { get; init; } = 6.0;

    public double RightLines { get; init; } = 11.0;

    public double CentreOffsetLines { get; init; } = 0.4;

    public double CentreToleranceLines { get; init; } = 1.0;

    public double MinDigitScore { get; init; } = 0.55;

    public double TieMargin { get; init; } = 0.08;

    public double SplitWidthLines { get; init; } = 0.8;

    public double SplitWholeMaxLines { get; init; } = 1.05;

    public double CondensedMinRed { get; init; } = 150;

    public double CondensedGreenShare { get; init; } = 0.7;

    public double CondensedBlueShare { get; init; } = 0.4;

    public double CondensedNarrowShare { get; init; } = 0.52;

    public double CondensedOneShare { get; init; } = 0.5;

    public double CondensedPairShare { get; init; } = 0.6;

    public double CondensedPitchShare { get; init; } = 0.55;

    public double CondensedCellLines { get; init; } = 0.45;

    public int CondensedMinInkPixels { get; init; } = 4;

    public double CondensedShapeMinShare { get; init; } = 0.35;

    public double CondensedShapeMaxShare { get; init; } = 0.8;

    public double CondensedMaxShapeLines { get; init; } = 2.6;

    public double CondensedCutReach { get; init; } = 0.2;

    public double MinInkThreshold { get; init; } = 12.0;

    public double LabelGapLines { get; init; } = 0.6;

    public double LabelPadXLines { get; init; } = 0.5;

    public double LabelPadYLines { get; init; } = 0.25;

    public byte LabelBackAlpha { get; init; } = 0x80;

    public double ColourBlend { get; init; } = 0.3;

    public int ColourStep { get; init; } = 12;

    public double ColourUpdateMs { get; init; } = 250.0;

    public double MinContrast { get; init; } = 3.0;

    public int KeptCropsPerRun { get; init; } = 2000;

    public double TableCropCooldownMs { get; init; } = 10000.0;

    public int TableCropsPerRun { get; init; } = 300;

    public int LossStreakReads { get; init; } = 3;

    public int LossAfterReads { get; init; } = 2;

    public double LossCropMarginLines { get; init; } = 1.0;

    public double LossCooldownMs { get; init; } = 10000.0;
}
