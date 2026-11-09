
namespace Iris.Core.Pipeline;

public sealed partial class ScanWorker
{
    private void StartLayerRule(IndexSnapshot snapshot)
    {
        if (Ops.Layers is not { } layers)
            return;
        layers.Rule = AberrationWatch.RuleFor(snapshot.Aberration, measures: true);
        layers.TakeVerdicts();
    }

    private void WatchAberration(IndexSnapshot snapshot, CapturedFrame frame, List<IndexUpdate> updates)
    {
        if (Ops.Layers is not { } layers)
            return;
        var nowMs = _clock.NowMs;
        var verdicts = layers.TakeVerdicts();
        if (_aberration.Add(snapshot.Aberration, verdicts) is { } state)
        {
            var note = state == AberrationState.On;
            _aberrationNoteSinceMs = note ? nowMs : double.NaN;
            updates.Add(new AberrationUpdate(PipelineGroup.Scan, frame.FrameMs, snapshot.Epoch, state, note));
            _log.Write(_wall.UtcNow, state == AberrationState.On
                ? "Live detection: chromatic aberration is on in the game; names are read where two colour layers agree"
                : "Live detection: chromatic aberration was turned off; the colour-layer rule stays off until IRIS starts again");
            if (Trace.Line(nowMs, "scn", "aberration") is { } traced)
                Trace!.Write(traced.Int("seq", frame.Sequence).Num("fms", frame.FrameMs).Str("state", state.ToString()).Int("verdicts", verdicts.Count).Text());
            return;
        }

        if (!snapshot.AberrationNote)
            return;
        if (double.IsNaN(_aberrationNoteSinceMs))
            _aberrationNoteSinceMs = nowMs;
        else if (double.IsFinite(_aberrationNoteSinceMs) && nowMs - _aberrationNoteSinceMs >= LiveSettings.AberrationNoteMs)
        {
            updates.Add(new AberrationUpdate(PipelineGroup.Scan, frame.FrameMs, snapshot.Epoch, snapshot.Aberration, false));
            _aberrationNoteSinceMs = double.NegativeInfinity;
        }
    }
}
