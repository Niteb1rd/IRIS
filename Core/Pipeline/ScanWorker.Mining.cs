
using System.Diagnostics;
using Iris.Core.Mining;

namespace Iris.Core.Pipeline;

public sealed partial class ScanWorker
{
    public MiningSignalReader? Mining { get; set; }

    public MiningTableSource? MiningTables { get; set; }

    public bool MiningEnabled { get; set; }

    public Action<SignalRead, string?>? MiningLocked { get; set; }

    private void RequestMiningSearch(IndexSnapshot snapshot, PipelineEnvironment env, double madeFrom, double nowMs, double expires, List<IndexUpdate> updates)
    {
        if (!MiningEnabled || Mining is null || snapshot.Mining is not null)
            return;
        var area = MiningSignalReader.SearchArea(env.WindowWidth, env.WindowHeight, Mining.Settings);
        if (area.IsEmpty)
            return;
        updates.Add(new RequestUpdate(
            PipelineGroup.Scan, madeFrom, snapshot.Epoch, 0, new RequestSpec(PipelineGroup.Scan, RequestKind.MiningSearch, area, _tick, madeFrom, nowMs, expires, true)));
    }

    private void SearchMining(IndexSnapshot snapshot, FrameRegion region, CapturedFrame frame, List<IndexUpdate> updates)
    {
        if (Mining is null || snapshot.Mining is not null || region.Image.Empty())
            return;
        var started = Stopwatch.GetTimestamp();
        var read = Mining.Search(region.Image, region.Region, frame.WindowWidth, frame.WindowHeight);
        var ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        _stats?.AddMiningSearch(ms);
        if (Trace.Line(_clock.NowMs, "scn", "mining") is { } traced)
            Trace!.Write(traced.Int("seq", frame.Sequence).Num("fms", frame.FrameMs).Num("ms", ms, 2).Str("state", read.State.ToString()).Str("text", read.Text)
                .Num("off", read.OffsetPx, 1).Num("score", read.MinScore, 3).Num("dh", read.DigitHeight, 1).Num("rowTop", read.RowTop, 1).Text());
        if (!read.Found)
            return;
        var table = MiningTables?.Current(_clock.NowMs) ?? MiningTable.Embedded;
        read = TableSnap.Apply(read, table, Mining.Settings);
        var state = MiningState.FromLock(read, frame.FrameMs, table);
        updates.Add(new MiningUpdate(PipelineGroup.Scan, frame.FrameMs, snapshot.Epoch, state));
        _stats?.AddMiningLock();
        MiningLocked?.Invoke(read, state.Label);
    }
}
