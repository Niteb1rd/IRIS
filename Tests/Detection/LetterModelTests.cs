using System.Text;
using Iris.Core.Detection;
using Xunit;

namespace Iris.Tests.Detection;

public class LetterModelTests
{
    private sealed record RawLayer(int Rows, int Cols, float[] Weights, float[] Biases);

    private const string ResourceName = "Iris.Core.Detection.Letters.letters-model.bin";

    private static List<RawLayer> ReadLayers(Stream stream, out int inputSize)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        reader.ReadBytes(8);
        reader.ReadUInt32();
        reader.ReadBytes((int)reader.ReadUInt32());
        inputSize = (int)reader.ReadUInt32();
        var count = (int)reader.ReadUInt32();
        var layers = new List<RawLayer>();
        for (var l = 0; l < count; l++)
        {
            var rows = (int)reader.ReadUInt32();
            var cols = (int)reader.ReadUInt32();
            var weights = new float[rows * cols];
            for (var i = 0; i < weights.Length; i++)
                weights[i] = reader.ReadSingle();
            var biases = new float[cols];
            for (var i = 0; i < biases.Length; i++)
                biases[i] = reader.ReadSingle();
            layers.Add(new RawLayer(rows, cols, weights, biases));
        }

        return layers;
    }

    private static double[] ReferencePredict(IReadOnlyList<RawLayer> layers, double[] input)
    {
        var current = input;
        var result = Array.Empty<double>();
        for (var l = 0; l < layers.Count; l++)
        {
            var layer = layers[l];
            var next = new double[layer.Cols];
            for (var j = 0; j < layer.Cols; j++)
                next[j] = layer.Biases[j];
            for (var i = 0; i < layer.Rows; i++)
            {
                var x = current[i];
                if (x == 0.0)
                    continue;
                var offset = i * layer.Cols;
                for (var j = 0; j < layer.Cols; j++)
                    next[j] += x * layer.Weights[offset + j];
            }

            if (l < layers.Count - 1)
            {
                for (var j = 0; j < layer.Cols; j++)
                    if (next[j] < 0.0)
                        next[j] = 0.0;
                current = next;
            }
            else
            {
                var max = next.Max();
                var sum = 0.0;
                for (var j = 0; j < layer.Cols; j++)
                {
                    next[j] = Math.Exp(next[j] - max);
                    sum += next[j];
                }

                result = next.Select(p => Math.Log(Math.Clamp(p / sum, LetterModel.MinProbability, 1.0))).ToArray();
            }
        }

        return result;
    }

    private static void AssertBitIdentical(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.True(BitConverter.DoubleToInt64Bits(expected[i]) == BitConverter.DoubleToInt64Bits(actual[i]), $"class {i}: {expected[i]:R} vs {actual[i]:R}");
    }

    private static double[] RandomInput(Random random, int size, double density)
    {
        var input = new double[size];
        for (var i = 0; i < size; i++)
            if (random.NextDouble() < density)
                input[i] = random.NextDouble();
        return input;
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.3)]
    [InlineData(1.0)]
    public void TheRealModelMatchesTheOriginalMathsBitForBit(double density)
    {
        using var stream = typeof(LetterModel).Assembly.GetManifestResourceStream(ResourceName)!;
        var layers = ReadLayers(stream, out var inputSize);
        stream.Position = 0;
        var model = LetterModel.Load(stream);
        var random = new Random(11);

        for (var round = 0; round < 300; round++)
        {
            var input = RandomInput(random, inputSize, density);

            AssertBitIdentical(ReferencePredict(layers, input), model.PredictLogProbs(input));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(17)]
    public void AnyLayerWidthMatchesTheOriginalMaths(int hiddenWidth)
    {
        const int inputs = 6;
        const string charset = "ABCDE";
        var random = new Random(hiddenWidth);
        float[] Floats(int count) => Enumerable.Range(0, count).Select(_ => (float)(random.NextDouble() * 4.0 - 2.0)).ToArray();
        var layers = new List<RawLayer>
        {
            new(inputs, hiddenWidth, Floats(inputs * hiddenWidth), Floats(hiddenWidth)),
            new(hiddenWidth, charset.Length, Floats(hiddenWidth * charset.Length), Floats(charset.Length)),
        };

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("IRISLM01"));
            writer.Write((uint)LetterModel.FormatVersion);
            writer.Write((uint)charset.Length);
            writer.Write(Encoding.UTF8.GetBytes(charset));
            writer.Write((uint)inputs);
            writer.Write((uint)layers.Count);
            foreach (var layer in layers)
            {
                writer.Write((uint)layer.Rows);
                writer.Write((uint)layer.Cols);
                foreach (var weight in layer.Weights)
                    writer.Write(weight);
                foreach (var bias in layer.Biases)
                    writer.Write(bias);
            }
        }

        stream.Position = 0;
        var model = LetterModel.Load(stream);
        for (var round = 0; round < 50; round++)
        {
            var input = RandomInput(random, inputs, 0.7);

            AssertBitIdentical(ReferencePredict(layers, input), model.PredictLogProbs(input));
        }
    }

    [Fact]
    public void ConcurrentCallsGetTheSameAnswers()
    {
        var model = LetterModel.LoadEmbedded();
        var input = RandomInput(new Random(5), model.InputSize, 0.3);
        var expected = model.PredictLogProbs(input);

        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => AssertBitIdentical(expected, model.PredictLogProbs(input)));
    }
}
