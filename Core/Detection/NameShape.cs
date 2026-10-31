namespace Iris.Core.Detection;

public static class NameShape
{
    public static bool HasValidEdges(string? name) =>
        !string.IsNullOrEmpty(name) && name[0] != '-' && name[^1] != '-';
}
