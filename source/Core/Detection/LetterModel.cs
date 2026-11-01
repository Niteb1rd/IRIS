using System.Text;

namespace Iris.Core.Detection;

public sealed class LetterModel
{
    public const int FormatVersion = 1;

    public const double MinProbability = 1e-6;

    private const string ResourceName = "Iris.Core.Detection.Letters.letters-model.bin";
    private const int StackLimit = 1024;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("IRISLM01");

    private readonly Layer[] _layers;
    private readonly int _maxWidth;

    private sealed class Layer
    {
        public Layer(int rows, int cols, float[] weights, float[] biases)
        {
            Rows = rows;
            Cols = cols;
            Weights = weights;
            Biases = biases;
        }

        public int Rows { get; }

        public int Cols { get; }

        public float[] Weights { get; }

        public float[] Biases { get; }
    }

    private LetterModel(string charset, int inputSize, Layer[] layers)
    {
        Charset = charset;
        InputSize = inputSize;
        _layers = layers;
        _maxWidth = layers.Max(l => l.Cols);
    }

    public string Charset { get; }

    public int InputSize { get; }

    public int ClassCount => Charset.Length;

    public static LetterModel LoadEmbedded()
    {
        using var stream = typeof(LetterModel).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        return Load(stream);
    }

    public static LetterModel LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static LetterModel Load(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            var magic = reader.ReadBytes(Magic.Length);
            if (!magic.AsSpan().SequenceEqual(Magic))
                throw new InvalidDataException("Not an IRIS letter model (bad magic).");
            var version = reader.ReadUInt32();
            if (version != FormatVersion)
                throw new InvalidDataException($"Unsupported letter model version {version}.");

            var charsetLength = checked((int)reader.ReadUInt32());
            if (charsetLength > 4096)
                throw new InvalidDataException("Charset is implausibly long.");
            var charsetBytes = reader.ReadBytes(charsetLength);
            if (charsetBytes.Length != charsetLength)
                throw new InvalidDataException("Unexpected end of file.");
            var charset = Encoding.UTF8.GetString(charsetBytes);

            var inputSize = checked((int)reader.ReadUInt32());
            var layerCount = checked((int)reader.ReadUInt32());
            if (inputSize <= 0 || layerCount <= 0 || layerCount > 16)
                throw new InvalidDataException("Bad model header.");

            var layers = new Layer[layerCount];
            var expectedRows = inputSize;
            for (var l = 0; l < layerCount; l++)
            {
                var rows = checked((int)reader.ReadUInt32());
                var cols = checked((int)reader.ReadUInt32());
                if (rows != expectedRows || cols <= 0 || (long)rows * cols > 50_000_000)
                    throw new InvalidDataException($"Layer {l} has an unexpected shape {rows}x{cols}.");
                var weights = new float[rows * cols];
                for (var i = 0; i < weights.Length; i++)
                    weights[i] = reader.ReadSingle();
                var biases = new float[cols];
                for (var i = 0; i < biases.Length; i++)
                    biases[i] = reader.ReadSingle();
                layers[l] = new Layer(rows, cols, weights, biases);
                expectedRows = cols;
            }

            if (expectedRows != charset.Length)
                throw new InvalidDataException("The output size does not match the charset.");
            if (stream.CanSeek && stream.Position != stream.Length)
                throw new InvalidDataException("Unexpected data after the last layer.");
            return new LetterModel(charset, inputSize, layers);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("Unexpected end of file.", e);
        }
    }

    public int ScratchSize => 2 * _maxWidth;

    public void PredictLogProbs(ReadOnlySpan<double> input, Span<double> logProbs)
    {
        Span<double> scratch = ScratchSize <= StackLimit ? stackalloc double[ScratchSize] : new double[ScratchSize];
        PredictLogProbs(input, logProbs, scratch);
    }

    public void PredictLogProbs(ReadOnlySpan<double> input, Span<double> logProbs, Span<double> scratch)
    {
        if (input.Length != InputSize)
            throw new ArgumentException($"Expected {InputSize} input values, got {input.Length}.", nameof(input));
        if (logProbs.Length < ClassCount)
            throw new ArgumentException($"Output needs room for {ClassCount} values.", nameof(logProbs));
        if (scratch.Length < ScratchSize)
            throw new ArgumentException($"Scratch needs room for {ScratchSize} values.", nameof(scratch));

        var bufferA = scratch.Slice(0, _maxWidth);
        var bufferB = scratch.Slice(_maxWidth, _maxWidth);

        ReadOnlySpan<double> current = input;
        for (var l = 0; l < _layers.Length; l++)
        {
            var layer = _layers[l];
            var cols = layer.Cols;
            var next = ((l & 1) == 0 ? bufferA : bufferB).Slice(0, cols);

            for (var j = 0; j < cols; j++)
                next[j] = layer.Biases[j];
            var weights = layer.Weights;
            for (var i = 0; i < layer.Rows; i++)
            {
                var x = current[i];
                if (x == 0.0)
                    continue;
                var offset = i * cols;
                for (var j = 0; j < cols; j++)
                    next[j] += x * weights[offset + j];
            }

            if (l < _layers.Length - 1)
            {
                for (var j = 0; j < cols; j++)
                    if (next[j] < 0.0)
                        next[j] = 0.0;
                current = next;
            }
            else
            {
                var max = next[0];
                for (var j = 1; j < cols; j++)
                    if (next[j] > max)
                        max = next[j];
                var sum = 0.0;
                for (var j = 0; j < cols; j++)
                {
                    next[j] = Math.Exp(next[j] - max);
                    sum += next[j];
                }

                for (var j = 0; j < cols; j++)
                    logProbs[j] = Math.Log(Math.Clamp(next[j] / sum, MinProbability, 1.0));
            }
        }
    }

    public double[] PredictLogProbs(ReadOnlySpan<double> input)
    {
        var result = new double[ClassCount];
        PredictLogProbs(input, result);
        return result;
    }
}
