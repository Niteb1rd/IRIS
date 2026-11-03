namespace Iris.Core.Detection;

public static class ContactCode
{
    public static bool IsContactCode(string? name)
    {
        if (name is null || name.Length != 10 || name[2] != '-' || name[7] != '-')
            return false;
        for (var i = 0; i < name.Length; i++)
        {
            if (i is 2 or 7)
                continue;
            if (!char.IsAsciiLetterOrDigit(name[i]))
                return false;
        }

        return true;
    }
}
