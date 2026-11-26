namespace KingbaseConnector.Protocol;

internal sealed class PgByteaDecoder
{
    int _mode;
    int _escape;
    int _octal;
    int _high = -1;
    bool _complete;
    bool _failed;

    internal int Convert(byte[] input, int offset, int count, byte[] output, int outputOffset, int outputCount, out int consumed)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (output is null) throw new ArgumentNullException(nameof(output));
        if (offset < 0 || count < 0 || offset > input.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        if (outputOffset < 0 || outputCount < 0 || outputOffset > output.Length - outputCount) throw new ArgumentOutOfRangeException(nameof(outputCount));
        EnsureActive(); consumed = 0; var written = 0;
        while (consumed < count && written < outputCount)
        {
            var value = input[offset + consumed];
            if (_mode == 0)
            {
                if (value == 92) { _mode = 1; consumed++; continue; }
                _mode = 3;
            }
            if (_mode == 1)
            {
                if (value == (byte)'x') { _mode = 2; consumed++; continue; }
                _mode = 3; _escape = 1;
            }
            if (_mode == 2)
            {
                var digit = Hex(value); if (digit < 0) throw Invalid(); consumed++;
                if (_high < 0) _high = digit;
                else { output[outputOffset + written++] = (byte)((_high << 4) | digit); _high = -1; }
                continue;
            }
            if (_escape == 0)
            {
                if (value == 92) { _escape = 1; consumed++; continue; }
                if (value == 0 || value > 127) throw Invalid();
                output[outputOffset + written++] = value; consumed++; continue;
            }
            if (_escape == 1)
            {
                if (value == 92) { output[outputOffset + written++] = 92; _escape = 0; consumed++; continue; }
                if (value < (byte)'0' || value > (byte)'3') throw Invalid();
                _octal = (value - (byte)'0') * 64; _escape = 2; consumed++; continue;
            }
            if (value < (byte)'0' || value > (byte)'7') throw Invalid();
            if (_escape == 2) { _octal += (value - (byte)'0') * 8; _escape = 3; }
            else { output[outputOffset + written++] = (byte)(_octal + value - (byte)'0'); _escape = 0; }
            consumed++;
        }
        return written;
    }

    internal void Complete()
    {
        if (_complete) return;
        EnsureActive();
        if (_mode == 1 || (_mode == 2 && _high >= 0) || (_mode == 3 && _escape != 0)) throw Invalid();
        _complete = true; _octal = 0;
    }
    void EnsureActive()
    { if (_complete || _failed) throw new InvalidOperationException("The bytea decoder is no longer active."); }
    FormatException Invalid() { _failed = true; return new FormatException("Invalid streamed PG bytea text."); }
    static int Hex(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
        _ => -1
    };
}
