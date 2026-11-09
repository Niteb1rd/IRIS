namespace Iris.Core.Pipeline;

public sealed record PipelineSettings
{
    public const int LostRefusalSamplesPerMinute = 5;

    public static PipelineSettings Default { get; } = new();

    public double ScanHz { get; init; } = 8.0;

    public double PlateCheckHz { get; init; } = 4.0;

    public double PlateHz { get; init; } = 12.0;

    public int MaxReadsPerTick { get; init; } = 10;

    public int ConfirmReads { get; init; } = 3;

    public double ConfirmSpanMs { get; init; } = 150.0;

    public int KnownConfirmReads { get; init; } = 2;

    public double KnownConfirmSpanMs { get; init; } = 60.0;

    public double StableWidthLines { get; init; } = 0.3;

    public double StableWidthMinPx { get; init; } = 2.0;

    public int StopReads { get; init; } = 3;

    public int UnconfirmedCheckFails { get; init; } = 2;

    public int ConfirmedCheckFails { get; init; } = 4;

    public int ConfirmedNoTrackChecks { get; init; } = 6;

    public double ConfirmedHoldMaxMs { get; init; } = 30000.0;

    public double StoppedTrackHz { get; init; } = 24.0;

    public double SearchBaseLines { get; init; } = 2.5;

    public double SearchGrowLinesPerSec { get; init; } = 6.0;

    public double SearchMaxLines { get; init; } = 12.0;

    public double LostAfterMs { get; init; } = 100.0;

    public double LostSearchGrowLinesPerSec { get; init; } = 30.0;

    public double LostSearchMaxLines { get; init; } = 10.0;

    public int MaxPatchPixels { get; init; } = 150000;

    public int MaxTickPixels { get; init; } = 400000;

    public const double PixelReferenceHeight = 1440.0;

    public long PatchPixelsAt(int windowHeight) => ScaledArea(MaxPatchPixels, windowHeight);

    public long TickPixelsAt(int windowHeight) => ScaledArea(MaxTickPixels, windowHeight);

    private static long ScaledArea(int pixels, int windowHeight)
    {
        if (windowHeight <= 0)
            return pixels;
        var scale = windowHeight / PixelReferenceHeight;
        return Math.Max(1L, (long)Math.Round(pixels * scale * scale));
    }

    public double MaxPatchWindowShare { get; init; } = 0.10;

    public double VelocityGain { get; init; } = 0.3;

    public double MaxSpeedLinesPerMs { get; init; } = 0.33;

    public double MaxLookAheadMs { get; init; } = 400.0;

    public double VelocityWindowMs { get; init; } = 0.0;

    public double VelocityWindowFrames { get; init; } = 5.0;

    public double VelocityWindowMinMs { get; init; } = 60.0;

    public double LostAfterFrames { get; init; } = 6.0;

    public double LostAfterMinMs { get; init; } = 100.0;

    public double LostAfterMaxMs { get; init; } = 250.0;

    public double LookAheadFrames { get; init; } = 6.0;

    public double LookAheadMinMs { get; init; } = 100.0;

    public double PredictionErrorGain { get; init; } = 2.0;

    public double PredictionErrorSmoothing { get; init; } = 0.5;

    public int RenderMissFrames { get; init; } = 1;

    public double RenderLookAheadMaxMs { get; init; } = 120.0;

    public double PartialAnchorMinPx { get; init; } = 2.0;

    public PipelineSettings Adapt(double frameIntervalMs)
    {
        if (!(frameIntervalMs > 0.0) || !double.IsFinite(frameIntervalMs))
            return this;
        return this with
        {
            VelocityWindowMs = Math.Max(VelocityWindowMinMs, VelocityWindowFrames * frameIntervalMs),
            LostAfterMs = Math.Clamp(LostAfterFrames * frameIntervalMs, LostAfterMinMs, Math.Max(LostAfterMinMs, LostAfterMaxMs)),
            MaxLookAheadMs = Math.Clamp(LookAheadFrames * frameIntervalMs, Math.Min(LookAheadMinMs, MaxLookAheadMs), MaxLookAheadMs),
        };
    }

    public double MaxLoopGapMs { get; init; } = 250.0;

    public double CheckMarginLines { get; init; } = 3.0;

    public int ReadMarginPx { get; init; } = 8;

    public double TemplateThreshold { get; init; } = 0.65;

    public double LostTemplateSpeedPxPerSec { get; init; } = 1500.0;

    public double LostTemplateReachPx { get; init; } = 450.0;

    public double LineHeightTolerance { get; init; } = 0.3;

    public double ClaimedOverlap { get; init; } = 0.3;

    public double TrackWidthGateLines { get; init; } = 0.8;

    public double SplitGapLines { get; init; } = 0.3;

    public double PartialLineMinFraction { get; init; } = 0.4;

    public double PartialEdgeToleranceLines { get; init; } = 0.25;

    public double PartialEdgeMinPx { get; init; } = 3.0;

    public double NameFingerprintMin { get; init; } = 0.6;

    public double ClaimedOverlapTemplate { get; init; } = 0.5;

    public double ScanCenterX { get; init; } = 0.5;

    public double ScanCenterY { get; init; } = 0.40;

    public int MaxTrackedMarkers { get; init; } = 48;

    public int MaxPlateReadHistory { get; init; } = 8;

    public int DuplicateWindowTicks { get; init; } = 3;

    public int IgnoredReadsLimit { get; init; } = 20;

    public double RequestMinMs { get; init; } = 10.0;

    public double RequestMaxMs { get; init; } = 100.0;

    public double FirstRequestMs { get; init; } = 100.0;

    public double ExpiryIntervalFactor { get; init; } = 2.5;

    public double RequestCapMs { get; init; } = 250.0;

    public double FirstRegionMarginLines { get; init; } = 5.0;

    public double ReadRequestMs { get; init; } = 83.0;

    public double ScanRequestMs { get; init; } = 125.0;

    public double FreshnessMinMs { get; init; } = 60.0;

    public double FreshnessMaxMs { get; init; } = 150.0;

    public double PanelStaleMs { get; init; } = 5000.0;

    public double PanelKeepMaxMs { get; init; } = 30000.0;

    public double FreshnessIntervalFactor { get; init; } = 3.0;

    public int TrackingTimesKept { get; init; } = 64;

    public int MaxSessionMemory { get; init; } = 64;

    public double NewMarkerOverlap { get; init; } = 0.2;

    public double DuplicateNameReachLines { get; init; } = 16.0;

    public double LostThrottleAfterMs { get; init; } = 1000.0;

    public int LostThrottleEveryFrames { get; init; } = 4;

    public double TallNameRowMaxExtraLines { get; init; } = 1.0;

    public double TallPartMinExtraPx { get; init; } = 2.0;

    public double MinPatchHeightPx { get; init; } = 12.0;

    public double MinPatchWidthPx { get; init; } = 30.0;

    public double PatchPadPx { get; init; } = 8.0;

    public double PatchMinWidthPx { get; init; } = 40.0;

    public double PatchSpeedGrowLines { get; init; } = 1.5;

    public double CropHalfWidthPx { get; init; } = 110.0;

    public double CropHalfWidthLines { get; init; } = 3.0;

    public double CropSlackPx { get; init; } = 12.0;

    public double CropAboveLines { get; init; } = 7.0;

    public double CropBelowLines { get; init; } = 2.0;

    public double LowerLineMinLines { get; init; } = 0.5;

    public double LowerLineMaxLines { get; init; } = 3.0;

    public double LowerLineSharedFraction { get; init; } = 0.5;

    public double TrackedLineOverlap { get; init; } = 0.3;

    public double TrackedLineOffsetLines { get; init; } = 0.5;

    public const double TickSlackMs = 1.0;

    public const double TickSlackFraction = 0.15;

    public static double SlackMs(double periodMs) => Math.Max(TickSlackMs, periodMs * TickSlackFraction);

    public const double SubPixelMinContrast = 40.0;

    public const double SubPixelFullCoverage = 0.9;

    public const double SubPixelMinWidthFraction = 0.5;

    public const double SubPixelMaxShiftPx = 2.0;

    public const double SubPixelMaxShiftLines = 0.3;

    public double MatchSlackPx { get; init; } = 2.0;

    public double LineHeightMinTolerancePx { get; init; } = 2.0;

    public double MissCost { get; init; } = 3.0;

    public double WidthWeight { get; init; } = 0.5;

    public double ColourWeight { get; init; } = 0.2;

    public double FingerprintWeight { get; init; } = 1.0;

    public double PartialNameMinFraction { get; init; } = 0.3;

    public double PartialNameMin { get; init; } = 0.8;

    public double PartialNameMargin { get; init; } = 0.1;

    public double PartialNameMinWidthLines { get; init; } = 3.0;

    public double PartialNameCost { get; init; } = 0.2;

    public int KnownNamesMax { get; init; } = 32;

    public double NameAnchorCost { get; init; } = 0.5;

    public double NameAnchorMin { get; init; } = 0.9;

    public double NameUnverifiedMaxMs { get; init; } = 1000.0;

    public double NameUnverifiedWidthLines { get; init; } = 0.3;

    public double NameUnverifiedCost { get; init; } = 0.8;

    public int NameUnverifiedMaxMissed { get; init; } = 1;

    public double NameUnverifiedReachLines { get; init; } = 2.0;

    public double TrackCropEveryMs { get; init; } = 500.0;

    public double TrackCropSampleEveryMs { get; init; } = 5000.0;

    public int TrackCropsPerRun { get; init; } = 600;

    public int TrackCropSamplesPerName { get; init; } = 2;

    public int ReadCropsPerPlate { get; init; } = 6;

    public int ReadCropsPerRun { get; init; } = 600;

    public int ReadCropsUnknownPerRun { get; init; } = 80;

    public int UnknownReadDistance { get; init; } = 2;

    public double LossCentreShare { get; init; } = 0.25;

    public double LossMinLineHeightPx { get; init; } = 10.0;

    public double LossMaxSpeedLinesPerMs { get; init; } = 0.05;

    public double LossNeighbourLines { get; init; } = 2.0;

    public int LossAfterFrames { get; init; } = 2;

    public double LossCropMarginLines { get; init; } = 1.0;

    public double LossReacquireMs { get; init; } = 1500.0;

    public double LossReacquireReachLines { get; init; } = 2.0;

    public double LossCooldownMs { get; init; } = 10000.0;

    public int LossGroupsPerFolder { get; init; } = 1000;

    public double NameCentreTolerancePx { get; init; } = 3.0;

    public double NameGapTolerancePx { get; init; } = 2.0;

    public double NameReachSlackPx { get; init; } = 3.0;

    public int TemplatePad { get; init; } = 2;

    public int MinTemplatePx { get; init; } = 4;

    public double MinTemplateSpread { get; init; } = 6.0;

    public double MinSearchReachPx { get; init; } = 6.0;

    public int ScanReadWaitMs { get; init; } = 500;

    public const double TemplateFlatSpread = 1.0;

    public string? Validate()
    {
        if (!(ScanHz > 0 && PlateHz >= ScanHz))
            return "ScanHz must be above 0 and at most PlateHz.";
        if (!(PlateCheckHz > 0) || KnownConfirmReads < 1 || KnownConfirmReads > ConfirmReads || KnownConfirmSpanMs < 0)
            return "PlateCheckHz must be above 0, KnownConfirmReads between 1 and ConfirmReads, and KnownConfirmSpanMs must not be negative.";
        if (MaxReadsPerTick < 1 || ConfirmReads < 1 || StopReads < 1 || UnconfirmedCheckFails < 1 || ConfirmedCheckFails < 1 || ConfirmedNoTrackChecks < 1 || IgnoredReadsLimit < 1)
            return "The read and check counts must be at least 1.";
        if (ConfirmSpanMs < 0 || StableWidthLines < 0 || !(StoppedTrackHz > 0))
            return "ConfirmSpanMs and StableWidthLines must not be negative and StoppedTrackHz must be above 0.";
        if (!(LostAfterMs >= 0 && MaxPatchPixels >= 1 && MaxTickPixels >= 1 && MaxPatchWindowShare > 0 && MaxPatchWindowShare <= 1))
            return "LostAfterMs must not be negative, the pixel caps must be at least 1 and the window share of a patch in (0, 1].";
        if (!(LostSearchGrowLinesPerSec >= 0 && LostSearchMaxLines >= 0 && LostTemplateSpeedPxPerSec >= 0 && LostTemplateReachPx >= 0
              && PartialLineMinFraction >= 0 && PartialEdgeToleranceLines >= 0 && PartialEdgeMinPx >= 0))
            return "The lost search, template reach and partial line settings must not be negative.";
        if (!(TemplateThreshold > 0 && TemplateThreshold <= 1))
            return "TemplateThreshold must be above 0 and at most 1.";
        if (MaxTrackedMarkers < 1 || MaxPlateReadHistory < ConfirmReads || MaxPlateReadHistory < StopReads)
            return "MaxTrackedMarkers must be at least 1, and the read history must hold the reads of a decision.";
        if (!(VelocityWindowMs >= 0 && VelocityWindowFrames > 0 && VelocityWindowMinMs > 0 && LostAfterFrames > 0 && LostAfterMinMs >= 0 && LostAfterMaxMs >= LostAfterMinMs
              && LookAheadFrames > 0 && LookAheadMinMs >= 0 && PredictionErrorGain >= 0 && PredictionErrorSmoothing > 0 && PredictionErrorSmoothing <= 1
              && RenderMissFrames >= 0 && RenderLookAheadMaxMs >= 0 && PartialAnchorMinPx >= 0))
            return "The adaptive tracking settings must be positive (the error smoothing at most 1) and the lost-time limits in order.";
        if (!(PartialNameMinFraction > 0 && PartialNameMinFraction <= 1 && PartialNameMin > 0 && PartialNameMin <= 1 && PartialNameMargin >= 0 && PartialNameMinWidthLines > 0
              && KnownNamesMax >= 0 && NameUnverifiedMaxMs >= 0 && NameUnverifiedWidthLines >= 0 && NameUnverifiedMaxMissed >= 0 && NameUnverifiedReachLines >= 0 && MaxSpeedLinesPerMs > 0 && TallNameRowMaxExtraLines >= 0 && TallPartMinExtraPx >= 1 && LostThrottleAfterMs >= 0 && LostThrottleEveryFrames >= 1
              && 1 + WidthWeight * TrackWidthGateLines + ColourWeight + FingerprintWeight * (1 - PartialNameMin) + PartialNameCost < MissCost
              && NameAnchorMin >= PartialNameMin && NameAnchorMin <= 1
              && 1 + NameAnchorCost + FingerprintWeight * (1 - NameAnchorMin) < MissCost
              && 1 + WidthWeight * NameUnverifiedWidthLines + NameUnverifiedCost < MissCost))
            return "The partial-name and covered-plate settings must be in range, and every pair cost must stay below the miss cost.";
        if (!(LossCentreShare > 0 && LossCentreShare <= 0.5 && LossMinLineHeightPx >= 0 && LossMaxSpeedLinesPerMs >= 0 && LossNeighbourLines >= 0 && LossAfterFrames >= 1
              && LossCropMarginLines >= 0 && LossReacquireMs >= 0 && LossReacquireReachLines >= 0 && LossCooldownMs >= 0 && LossGroupsPerFolder >= 0))
            return "The loss crop settings must not be negative, LossAfterFrames must be at least 1 and LossCentreShare in (0, 0.5].";
        return null;
    }
}
