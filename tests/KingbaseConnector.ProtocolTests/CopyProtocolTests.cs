using System.Reflection;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class CopyProtocolTests
{
    [Theory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ExactAssetCopyProbe(KingbaseCompatibilityMode mode) => KingbaseConnector.Tests.CopyAssetProbe.RunAsync(mode);

    [Fact]
    public void PublicCopyStreamAndTextEntryPointsExist()
    {
        var type = typeof(KingbaseConnection);
        foreach (var method in new[] { "BeginRawBinaryCopy", "BeginRawBinaryCopyAsync", "BeginTextImport", "BeginTextImportAsync", "BeginTextExport", "BeginTextExportAsync" })
            Assert.Contains(type.GetMethods(), item => item.Name == method);
        var stream = type.Assembly.GetType("KingbaseConnector.KingbaseCopyStream"); Assert.NotNull(stream); Assert.True(typeof(Stream).IsAssignableFrom(stream));
        foreach (var method in new[] { "Complete", "CompleteAsync", "Cancel", "CancelAsync" }) Assert.Contains(stream.GetMethods(), item => item.Name == method);
    }

    [Fact]
    public async Task CopyDataLargerThanTheBufferedLimitReadsIntoSmallCallerBuffersAndKeepsControlsFramed()
    {
        var expected = Enumerable.Range(0, 2 * 1024 * 1024 + 13).Select(i => (byte)(i % 251)).ToArray();
        using var wire = new PgWire(new MemoryStream(Join(Frame('d', expected), Frame('c'), Ready())));
        var read = typeof(PgWire).GetMethod("ReadCopyMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance); Assert.NotNull(read);
        var buffer = new byte[7919]; var copied = 0;
        while (copied < expected.Length)
        {
            var task = (Task)read.Invoke(wire, [buffer, 2, buffer.Length - 4, CancellationToken.None])!; await task;
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            var size = (int)result.GetType().GetProperty("Count", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(result)!;
            Assert.InRange(size, 1, buffer.Length - 4); Assert.Equal(expected.AsSpan(copied, size).ToArray(), buffer.AsSpan(2, size).ToArray()); copied += size;
        }
        Assert.Equal((byte)'c', (await wire.ReadMessageAsync()).Code); Assert.Equal((byte)'Z', (await wire.ReadMessageAsync()).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(64 * 1024 * 1024 + 1)]
    public async Task InvalidCopyFrameLengthsRetireBeforeReadingPayload(int length)
    {
        using var wire = new PgWire(new MemoryStream(Join([(byte)'d'], Int32(length))));
        await Assert.ThrowsAsync<IOException>(() => wire.ReadCopyMessageAsync(new byte[9], 0, 9, CancellationToken.None)); Assert.True(wire.IsDisposed);
    }

    [Fact]
    public async Task TruncatedCopyDataCannotBeReusedAsTheNextMessage()
    {
        using var wire = new PgWire(new MemoryStream(Join([(byte)'d'], Int32(100), [1, 2, 3]))); var buffer = new byte[2];
        Assert.Equal(2, (await wire.ReadCopyMessageAsync(buffer, 0, 2, CancellationToken.None)).Count);
        Assert.Equal(1, (await wire.ReadCopyMessageAsync(buffer, 0, 2, CancellationToken.None)).Count);
        await Assert.ThrowsAsync<EndOfStreamException>(() => wire.ReadCopyMessageAsync(buffer, 0, 2, CancellationToken.None)); Assert.True(wire.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => wire.ReadMessageAsync());
    }
}
