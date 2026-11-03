namespace Iris.Core.Detection;

public sealed record NameplateDetectorSettings
{
    public static NameplateDetectorSettings Default { get; } = new();

    public double RegionLeft { get; init; } = 0.15;

    public double RegionRight { get; init; } = 0.85;

    public double RegionTop { get; init; } = 0.10;

    public double RegionBottom { get; init; } = 0.70;

    public int ReferenceHeight { get; init; } = 1440;

    public int ScaleHeight { get; init; }

    public int EffectiveScaleHeight(int imageHeight) => ScaleHeight > 0 ? ScaleHeight : imageHeight;

    public int ContextPadding(int scaleHeight) => 3 * Scale(scaleHeight, LineMaxHeight) + Scale(scaleHeight, HeightTolerancePx) + 2;

    public int VerticalContextPadding(int scaleHeight) => Scale(scaleHeight, TopHatKernelPx) + 2;

    public int MinNameLength { get; init; } = 4;

    public int MaxNameLength { get; init; } = 64;

    public double MinNameScore { get; init; } = -0.20;

    public double MinDistanceScore { get; init; } = -0.35;

    public double MaxDistanceGap { get; init; } = 4.0;

    public double DistanceEndScore { get; init; } = -1.0;

    public int MaxDistanceValue { get; init; } = 1000;

    public double DistanceMinInk { get; init; } = 0.003;

    public double WordGap { get; init; } = 0.33;

    public double ClearLines { get; init; } = 3.0;

    public bool CrowdException { get; init; } = true;

    public double SpeckArea { get; init; } = 4.0;

    public double CenterTolerance { get; init; } = 5.0;

    public double LeftTolerance { get; init; } = 2.0;

    public double LeftWidthDifference { get; init; } = 8.0;

    public int MaxRows { get; init; } = 3;

    public double RowGapMax { get; init; } = 0.8;

    public double RowPitchMin { get; init; } = 1.05;

    public double RowPitchMax { get; init; } = 1.8;

    public double RowMinCaps { get; init; } = 11.0;

    public double NarrowMinWidth { get; init; } = 6.0;

    public double NarrowMaxLines { get; init; } = 2.2;

    public bool SubPixelO0Rule { get; init; } = true;

    public bool UnderscoreHyphenRule { get; init; } = true;

    public PlateColorSettings PlateColorRules { get; init; } = PlateColorSettings.Default;

    public double SpeedJoinGap { get; init; } = 1.5;

    public double SpeedJoinMaxLines { get; init; } = 8.0;

    public double BracketMinScore { get; init; } = -0.7;


    public double ReadCropMinHeight { get; init; } = 30.0;

    public double ReadCropMinWidth { get; init; } = 60.0;

    public double NearCropMinHeight { get; init; } = 12.0;

    public double NearCropMinWidth { get; init; } = 30.0;

    public double NearHeightTolerance { get; init; } = 0.35;

    public double NearHeightMinPx { get; init; } = 2.0;

    public double NearSlackPx { get; init; } = 3.0;

    public int NearMaxTries { get; init; } = 3;

    public double AnchorMaxOffsetPx { get; init; } = 8.0;

    public double AnchorMaxRowOffsetLines { get; init; } = 0.8;

    public double TopGapMaxLines { get; init; } = 1.4;

    public double TopHeightDiffMinPx { get; init; } = 4.0;

    public double TopHeightDiffLines { get; init; } = 0.45;

    public double TopCentreTolerancePx { get; init; } = 5.0;

    public double TopLeftTolerancePx { get; init; } = 2.0;

    public double TopLeftWidthDifferencePx { get; init; } = 8.0;


    public double TopHatKernelPx { get; init; } = 11.0;

    public double TopHatMin { get; init; } = 28.0;

    public double GreenTopHatMin { get; init; } = 20.0;

    public double CyanBlueMin { get; init; } = 150.0;

    public double CyanGreenMin { get; init; } = 140.0;

    public double RedMin { get; init; } = 140.0;

    public double RedOverGreenMin { get; init; } = 80.0;

    public double RedOverBlueMin { get; init; } = 60.0;

    public double GreenMin { get; init; } = 90.0;

    public double GreenOverRedMin { get; init; } = 35.0;

    public double GreenOverBlueMin { get; init; } = 30.0;


    public const bool ContrastMaskEnabled = true;

    public bool ContrastMask { get; init; } = ContrastMaskEnabled;

    public double BgDarkMax { get; init; } = 100.0;

    public double BgFullContrast { get; init; } = 180.0;

    public double ContrastHatMax { get; init; } = 45.0;

    public double BackgroundShrinkPx { get; init; } = 4.0;

    public int BackgroundBlur { get; init; } = 15;


    public double LineMaxHeight { get; init; } = 22.0;

    public double LineMinHeight { get; init; } = 10.0;

    public double LineMinWidth { get; init; } = 24.0;

    public double LineAspectMin { get; init; } = 1.5;

    public double JoinWidthPx { get; init; } = 7.0;

    public double FillMin { get; init; } = 0.12;

    public double FillMax { get; init; } = 0.75;


    public double HeightTolerancePx { get; init; } = 4.0;

    public double HeightToleranceLines { get; init; } = 0.45;

    public double RowTolerancePx { get; init; } = 3.0;

    public double RowToleranceLines { get; init; } = 0.25;

    public double RowJoinTopPx { get; init; } = 2.0;

    public double RowJoinHeightPx { get; init; } = 3.0;

    public double RowJoinGapPx { get; init; } = 4.0;

    public const int MaxLineWidth = 832;

    public const double PairGapMaxLines = 1.4;


    public const double MaxPieceCaps = 8.0;

    public const int MaxSplitSpans = 1500;

    public const double NameMinInk = 0.02;

    public const double O0Decide = 0.95;

    public const double BarUnderscoreMin = 0.87;

    public const double BarHyphenMax = 0.71;

    public const double BarMaxThick = 0.45;

    public const double BarRowFraction = 0.25;

    public const int EvidenceMaxKeys = 16;

    public const double EvidenceLlrClip = 4.0;

    public const double EvidenceDecideOdds = 19.0;

    public const double EvidenceDecideLlr = 2.9444389791664403;

    public const int PlateColorMinPixels = 30;

    public const int PlateMaskMinPixels = 8;

    public int Scale(int frameHeight, double value) =>
        Math.Max(1, (int)Math.Floor(value * frameHeight / ReferenceHeight + 0.5));

    public string? Validate()
    {
        if (!(RegionLeft >= 0 && RegionLeft < RegionRight && RegionRight <= 1))
            return "The scan region needs 0 <= left < right <= 1.";
        if (!(RegionTop >= 0 && RegionTop < RegionBottom && RegionBottom <= 1))
            return "The scan region needs 0 <= top < bottom <= 1.";
        if (ReferenceHeight <= 0)
            return "ReferenceHeight must be positive.";
        if (ScaleHeight < 0)
            return "ScaleHeight must not be negative (0 = the height of the image).";
        if (MinNameLength < 1 || MaxNameLength < MinNameLength)
            return "The name length limits are not usable.";
        if (ClearLines < 0 || SpeckArea < 0 || CenterTolerance < 0 || LeftTolerance < 0 || LeftWidthDifference < 0 || DistanceMinInk < 0)
            return "The isolation and pairing numbers must not be negative.";
        if (SpeedJoinGap < 0 || SpeedJoinMaxLines < 0)
            return "The speed group join limits must not be negative.";
        if (MaxRows < 1 || RowGapMax < 0 || RowPitchMin < 0 || RowPitchMax < RowPitchMin || RowMinCaps < 0)
            return "The row limits are not usable (at least 1 row, positive gap and pitch, maximum pitch at least the minimum).";
        if (NarrowMinWidth < 0 || NarrowMaxLines < 0)
            return "The narrow row limits must not be negative.";
        if (MaxDistanceValue < 0)
            return "MaxDistanceValue must not be negative.";
        if (!(BgFullContrast > BgDarkMax) || ContrastHatMax < TopHatMin || BackgroundShrinkPx < 1 || BackgroundBlur < 1)
            return "The contrast rule needs BgFullContrast above BgDarkMax, ContrastHatMax at least TopHatMin, a shrink and a blur of at least 1.";
        return null;
    }
}
