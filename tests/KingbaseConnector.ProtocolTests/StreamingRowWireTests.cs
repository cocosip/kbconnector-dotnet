using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class StreamingRowWireTests
{
    [Fact]
    public async Task LargeDataRowsReadOnlyTheirHeaderThenCopyFragmentedPayloadWithoutWholeFrameAllocation()
    {
        var payload = Enumerable.Range(0, 2 * 1024 * 1024 + 17).Select(i => (byte)(i % 251)).ToArray();
        using var input = new QueryStream(Join(Frame('D', payload), Ready())) { FragmentSize = 4093 }; using var wire = new PgWire(input);
        var before = GC.GetAllocatedBytesForCurrentThread(); var header = await Header(wire);
        Assert.Equal((byte)'D', header.Code); Assert.Empty(header.Payload); Assert.Equal(5, input.BytesRead); Assert.Equal(payload.Length, Remaining(wire));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 65536);
        var scratch = new byte[8192]; var offset = 0;
        while (Remaining(wire) != 0)
        {
            var received = await Payload(wire, scratch, 0, scratch.Length); Assert.InRange(received, 1, scratch.Length);
            Assert.True(payload.AsSpan(offset, received).SequenceEqual(scratch.AsSpan(0, received))); offset += received;
        }
        Assert.Equal(payload.Length, offset); Assert.Equal(0, await Payload(wire, scratch, 0, scratch.Length)); Assert.Equal(payload.Length + 5, input.BytesRead);
        var ready = await Header(wire); Assert.Equal((byte)'Z', ready.Code); Assert.Equal(new byte[] { (byte)'I' }, ready.Payload); Assert.False(wire.IsDisposed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(PgWire.MaxMessageLength + 1)]
    [InlineData(int.MaxValue)]
    public async Task InvalidRowFrameLengthsRetireBeforeReadingOrAllocatingAPayload(int length)
    {
        using var input = new QueryStream(Join(new byte[] { (byte)'D' }, Int32(length))) { FragmentSize = 2 }; using var wire = new PgWire(input);
        await Assert.ThrowsAsync<IOException>(() => Header(wire)); Assert.True(wire.IsDisposed); Assert.Equal(5, input.BytesRead);
    }

    [Fact]
    public async Task OversizedControlFramesKeepTheBufferedBudgetEvenAlongsideStreamedRows()
    {
        using var input = new QueryStream(Join(new byte[] { (byte)'T' }, Int32(PgWire.MaxBufferedPayloadLength + 5))); using var wire = new PgWire(input);
        await Assert.ThrowsAsync<IOException>(() => Header(wire)); Assert.True(wire.IsDisposed); Assert.Equal(5, input.BytesRead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task TruncatedHeadersRetireWithoutInventingAnEmptyRow(int available)
    {
        using var input = new QueryStream(new byte[] { (byte)'D', 0, 0, 0, 8 }.Take(available).ToArray()); using var wire = new PgWire(input);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Header(wire)); Assert.True(wire.IsDisposed);
    }

    [Fact]
    public async Task TruncatedRowPayloadsRetireAndNeverTreatTheNextReadAsANewMessage()
    {
        using var input = new QueryStream(Join(new byte[] { (byte)'D' }, Int32(8), new byte[] { 1, 2, 3 })) { FragmentSize = 5 }; using var wire = new PgWire(input);
        await Header(wire); var destination = new byte[8]; Assert.Equal(3, await Payload(wire, destination, 0, destination.Length)); Assert.Equal(1, Remaining(wire));
        await Assert.ThrowsAsync<EndOfStreamException>(() => Payload(wire, destination, 0, destination.Length)); Assert.True(wire.IsDisposed);
    }

    [Fact]
    public async Task LocalPreCancellationAndInvalidBuffersPreserveTheUnconsumedRow()
    {
        using var input = new QueryStream(Join(Frame('D', 1, 2), Ready())); using var wire = new PgWire(input); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Header(wire, cancel.Token)); Assert.Equal(cancel.Token, canceled.CancellationToken); Assert.Equal(0, input.BytesRead);
        await Header(wire); Assert.Equal(2, Remaining(wire));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Payload(wire, new byte[1], 0, 1, cancel.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Payload(wire, null!, 0, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Payload(wire, new byte[1], 0, 2));
        Assert.Equal(0, await Payload(wire, new byte[1], 0, 0)); Assert.Equal(5, input.BytesRead); Assert.False(wire.IsDisposed);
        var bytes = new byte[2]; while (Remaining(wire) != 0) await Payload(wire, bytes, 2 - Remaining(wire), Remaining(wire)); Assert.Equal(new byte[] { 1, 2 }, bytes);
    }

    [Fact]
    public async Task BufferedAndCopyReadsCannotConsumeAnActiveDataRow()
    {
        using var input = new QueryStream(Join(Frame('D', 1), Ready())); using var wire = new PgWire(input); await Header(wire);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wire.ReadMessageAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => wire.ReadCopyMessageAsync(new byte[1], 0, 1, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Header(wire)); Assert.False(wire.IsDisposed); Assert.Equal(5, input.BytesRead);
        Assert.Equal(1, await Payload(wire, new byte[1], 0, 1)); Assert.Equal((byte)'Z', (await wire.ReadMessageAsync()).Code);
    }

    [Fact]
    public async Task RowReadsCannotConsumeAnActiveCopyPayload()
    {
        using var input = new QueryStream(Join(Frame('d', 1, 2), Ready())); using var wire = new PgWire(input);
        Assert.Equal(1, (await wire.ReadCopyMessageAsync(new byte[1], 0, 1, CancellationToken.None)).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wire.ReadMessageAsync()); Assert.False(wire.IsDisposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Header(wire)); await Assert.ThrowsAsync<InvalidOperationException>(() => Payload(wire, new byte[1], 0, 1)); Assert.False(wire.IsDisposed);
        Assert.Equal(1, (await wire.ReadCopyMessageAsync(new byte[1], 0, 1, CancellationToken.None)).Count); Assert.Equal((byte)'Z', (await Header(wire)).Code);
    }

    [Theory]
    [InlineData("row-header")]
    [InlineData("row-payload")]
    [InlineData("copy")]
    public async Task AResumableBufferedReadRejectsOtherPayloadReadersWithoutClosingIt(string path)
    {
        using var input = new QueryStream(Join(new byte[] { (byte)'Z' }, Int32(5))) { BlockAtEnd = true, IgnoreReadCancellation = true }; using var wire = new PgWire(input);
        Assert.Null(await wire.ReadMessageWithTimeoutAsync(0, CancellationToken.None, PgWire.MaxBufferedPayloadLength)); await input.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (path == "row-header") await Assert.ThrowsAsync<InvalidOperationException>(() => Header(wire));
        else if (path == "row-payload") await Assert.ThrowsAsync<InvalidOperationException>(() => Payload(wire, new byte[1], 0, 1));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => wire.ReadCopyMessageAsync(new byte[1], 0, 1, CancellationToken.None));
        Assert.False(wire.IsDisposed); Assert.Equal(5, input.BytesRead);
    }

    [Fact]
    public async Task InflightCancellationClosesAnUnderlyingReadEvenWhenItIgnoresTheToken()
    {
        using var input = new QueryStream(Join(new byte[] { (byte)'D' }, Int32(8))) { BlockAtEnd = true, IgnoreReadCancellation = true }; using var wire = new PgWire(input); await Header(wire);
        using var cancel = new CancellationTokenSource(); var pending = Payload(wire, new byte[1], 0, 1, cancel.Token); await input.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2))); Assert.Equal(cancel.Token, error.CancellationToken); Assert.True(wire.IsDisposed);
    }

    static Task<PgMessage> Header(PgWire wire, CancellationToken token = default) => wire.ReadRowMessageAsync(token);
    static Task<int> Payload(PgWire wire, byte[] buffer, int offset, int count, CancellationToken token = default) => wire.ReadRowPayloadAsync(buffer, offset, count, token);
    static int Remaining(PgWire wire) => wire.RowPayloadRemaining;
}
