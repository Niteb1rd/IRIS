using Iris.Core.Detection;

namespace Iris.Core.Pipeline;

public static class IdentityEvidence
{
    public static int StrongDifferences(string confirmed, string read, IReadOnlyList<double> margins, double minMarginNats)
    {
        if (margins.Count != read.Length || read.Length == 0)
            return 0;
        var a = LookAlikes.FoldIdentity(confirmed);
        var b = LookAlikes.FoldIdentity(read);
        if (a.Length != confirmed.Length || b.Length != read.Length)
            return 0;

        var cost = new int[a.Length + 1, b.Length + 1];
        var strong = new int[a.Length + 1, b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
            cost[i, 0] = i;
        for (var j = 1; j <= b.Length; j++)
        {
            cost[0, j] = j;
            strong[0, j] = strong[0, j - 1] + (margins[j - 1] >= minMarginNats ? 1 : 0);
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var sure = margins[j - 1] >= minMarginNats ? 1 : 0;
                var same = Same(a[i - 1], b[j - 1]);
                var bestCost = cost[i - 1, j - 1] + (same ? 0 : 1);
                var bestStrong = strong[i - 1, j - 1] + (same ? 0 : sure);
                Keep(cost[i - 1, j] + 1, strong[i - 1, j], ref bestCost, ref bestStrong);
                Keep(cost[i, j - 1] + 1, strong[i, j - 1] + sure, ref bestCost, ref bestStrong);
                cost[i, j] = bestCost;
                strong[i, j] = bestStrong;
            }
        }

        return strong[a.Length, b.Length];
    }

    private static void Keep(int cost, int strong, ref int bestCost, ref int bestStrong)
    {
        if (cost < bestCost || (cost == bestCost && strong < bestStrong))
        {
            bestCost = cost;
            bestStrong = strong;
        }
    }

    private static bool Same(char a, char b)
    {
        if (a == b)
            return true;
        foreach (var group in LookAlikes.EfficiencyGroups)
        {
            if (group.Contains(a) && group.Contains(b))
                return true;
        }

        return false;
    }
}
