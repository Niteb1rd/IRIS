namespace Iris.Core.Config;

public sealed class FileTooLargeException(string message) : IOException(message);

public static class BoundedFile
{
    public static string ReadText(string path, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = ReadBytes(stream, maxBytes);
        if (bytes.Length > maxBytes)
            throw new FileTooLargeException(TooLargeText(maxBytes));
        return ConfigImporter.DecodeUtf8(bytes);
    }

    public static byte[] ReadBytes(Stream stream, long maxBytes)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (buffer.Length <= maxBytes)
        {
            var want = (int)Math.Min(chunk.Length, maxBytes + 1 - buffer.Length);
            var read = stream.Read(chunk, 0, want);
            if (read == 0)
                break;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public static string TooLargeText(long maxBytes) => $"The file is larger than {SizeText(maxBytes)} and was not read.";

    private static string SizeText(long bytes) =>
        bytes % (1024 * 1024) == 0 ? $"{bytes / (1024 * 1024)} MB" : bytes % 1024 == 0 ? $"{bytes / 1024} KB" : $"{bytes} bytes";
}
