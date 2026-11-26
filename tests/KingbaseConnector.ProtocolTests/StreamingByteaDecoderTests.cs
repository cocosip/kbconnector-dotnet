using System.Text;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

public sealed class StreamingByteaDecoderTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(7, 3)]
    [InlineData(4096, 4096)]
    public void HexAndLegacyRepresentationsDecodeAcrossInputAndOutputBoundaries(int inputChunk, int outputChunk)
    {
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        Assert.Equal(bytes, Decode("\\x" + Convert.ToHexString(bytes), inputChunk, outputChunk));
        var legacy = string.Concat(bytes.Select(value => "\\" + Convert.ToString(value, 8).PadLeft(3, '0')));
        Assert.Equal(bytes, Decode(legacy, inputChunk, outputChunk));
        Assert.Equal(Encoding.ASCII.GetBytes("literal\\slash").Concat(new byte[] { 0, 1, 255 }), Decode("literal\\\\slash\\000\\001\\377", inputChunk, outputChunk));
        Assert.Empty(Decode("", inputChunk, outputChunk)); Assert.Empty(Decode("\\x", inputChunk, outputChunk));
    }

    [Theory]
    [InlineData("\\x0")]
    [InlineData("\\xzz")]
    [InlineData("\\")]
    [InlineData("\\1")]
    [InlineData("\\12")]
    [InlineData("\\400")]
    [InlineData("\\3x0")]
    [InlineData("\\q")]
    [InlineData("\\X00")]
    [InlineData("文字")]
    [InlineData("nul\0text")]
    public void InvalidHexOctalUnicodeAndIncompleteEscapesFailWithoutReturningGuessedBytes(string text)
    { Assert.Throws<FormatException>(() => Decode(text, 1, 1)); }

    [Fact]
    public void MultiMegabyteEncodedValuesHaveNoBufferedScalarLimit()
    {
        var bytes = Enumerable.Range(0, 1024 * 1024 + 257).Select(i => (byte)(i % 251)).ToArray();
        Assert.Equal(bytes, Decode("\\x" + Convert.ToHexString(bytes).ToLowerInvariant(), 8191, 4093));
    }

    [Fact]
    public void LocalBufferErrorsAndZeroOutputDoNotLoseAHighNibble()
    {
        var decoder = new Decoder(); var output = new byte[2]; var prefix = Encoding.ASCII.GetBytes("\\x0");
        Assert.Equal((0, 0), decoder.Transform(prefix, 0, prefix.Length, output, 0, 0));
        Assert.Equal((0, 3), decoder.Transform(prefix, 0, prefix.Length, output, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Transform(new byte[1], 0, 2, output, 0, 1));
        Assert.Equal((1, 1), decoder.Transform([(byte)'1'], 0, 1, output, 0, 1)); Assert.Equal(1, output[0]); decoder.Complete(); decoder.Complete();
        Assert.Throws<InvalidOperationException>(() => decoder.Transform([(byte)'0'], 0, 1, output, 0, 1));
    }

    [Fact]
    public void MalformedDataPermanentlyInvalidatesTheDecoder()
    {
        var decoder = new Decoder(); Assert.Throws<FormatException>(() => decoder.Transform(Encoding.ASCII.GetBytes("\\q"), 0, 2, new byte[1], 0, 1));
        Assert.Throws<InvalidOperationException>(() => decoder.Transform([(byte)'a'], 0, 1, new byte[1], 0, 1));
    }

    static byte[] Decode(string text, int inputChunk, int outputChunk)
    {
        var input = Encoding.UTF8.GetBytes(text); var decoder = new Decoder(); var output = new byte[outputChunk]; using var result = new MemoryStream(); var position = 0;
        while (position < input.Length)
        {
            var (written, consumed) = decoder.Transform(input, position, Math.Min(inputChunk, input.Length - position), output, 0, output.Length);
            Assert.True(consumed != 0 || written != 0); result.Write(output, 0, written); position += consumed;
        }
        decoder.Complete(); return result.ToArray();
    }
    sealed class Decoder
    {
        readonly PgByteaDecoder _value = new();
        internal (int Written, int Consumed) Transform(byte[] input, int offset, int count, byte[] output, int outputOffset, int outputCount)
        {
            var written = _value.Convert(input, offset, count, output, outputOffset, outputCount, out var consumed); return (written, consumed);
        }
        internal void Complete() => _value.Complete();
    }
}
