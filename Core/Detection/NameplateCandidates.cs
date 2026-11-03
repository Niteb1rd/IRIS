using OpenCvSharp;

namespace Iris.Core.Detection;

public sealed record PlateStack(IReadOnlyList<Rect> Rows, IReadOnlyList<Rect> Blockers);

public readonly record struct CrowdPair(int Top, int Bottom, IReadOnlyList<int> Neighbours);

public sealed record NameplateCandidates(
    IReadOnlyList<Rect> Boxes,
    IReadOnlyList<(int Top, int Bottom)> Pairs,
    IReadOnlyList<(int Top, int Bottom)> LoosePairs,
    IReadOnlyList<CrowdPair> CrowdPairs,
    IReadOnlyDictionary<(Rect Top, Rect Bottom), PlateStack>? Stacks = null)
{
    public PlateStack? StackOf(Rect top, Rect bottom) => Stacks is not null && Stacks.TryGetValue((top, bottom), out var stack) ? stack : null;
}
