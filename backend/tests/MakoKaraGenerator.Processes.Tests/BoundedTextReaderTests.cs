namespace MakoKaraGenerator.Processes.Tests;

public sealed class BoundedTextReaderTests
{
    [Fact]
    public async Task 无换行大量诊断在固定行预算内完整排水()
    {
        using GeneratedReader reader = new(4 * 1024 * 1024);
        List<string> lines = [];
        await foreach (string line in BoundedTextReader.ReadLinesAsync(reader, 64)) lines.Add(line);
        string retained = Assert.Single(lines);
        Assert.Equal(64, retained.Length);
        Assert.EndsWith(" [truncated]", retained, StringComparison.Ordinal);
        Assert.Equal(4 * 1024 * 1024, reader.ReadCharacters);
    }

    [Fact]
    public async Task 元数据预算在增长前拒绝而诊断前缀有界排水()
    {
        using GeneratedReader rejected = new(100_000);
        await Assert.ThrowsAsync<IOException>(() => BoundedTextReader.ReadToEndAsync(rejected, 32, true));
        Assert.True(rejected.ReadCharacters < 100_000);
        using GeneratedReader drained = new(100_000);
        string text = await BoundedTextReader.ReadToEndAsync(drained, 32, false);
        Assert.Equal(32, text.Length);
        Assert.EndsWith(" [truncated]", text, StringComparison.Ordinal);
        Assert.Equal(100_000, drained.ReadCharacters);
    }

    [Fact]
    public async Task 混合换行与过长进度合同保持清楚()
    {
        using StringReader reader = new("one\r\ntwo\rthree\nfour");
        List<string> values = [];
        await foreach (string line in BoundedTextReader.ReadLinesAsync(reader, 32)) values.Add(line);
        Assert.Equal(["one", "two", "three", "four"], values);
        using GeneratedReader oversized = new(1000);
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (string _ in BoundedTextReader.ReadLinesAsync(oversized, 32, true)) { }
        });
    }

    private sealed class GeneratedReader(int length) : TextReader
    {
        public int ReadCharacters { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(buffer.Length, length - ReadCharacters);
            buffer.Span[..count].Fill('x'); ReadCharacters += count;
            return ValueTask.FromResult(count);
        }
    }
}
