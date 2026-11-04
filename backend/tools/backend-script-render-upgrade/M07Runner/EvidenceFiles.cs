using System.Security.Cryptography;
using System.Text.Json;

internal sealed class EvidenceFiles : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _output;
    private readonly string _pending;
    private readonly string _assets;
    private readonly FileStream _reservation;

    internal EvidenceFiles(string output)
    {
        _output = Path.GetFullPath(output);
        _pending = _output + ".pending";
        _assets = _output + ".assets";
        if (File.Exists(_output) || Directory.Exists(_output) || File.Exists(_pending) || Directory.Exists(_pending)
            || File.Exists(_assets) || Directory.Exists(_assets))
            throw new IOException("Evidence output, pending reservation or assets already exist; use a new output path.");
        Directory.CreateDirectory(Path.GetDirectoryName(_output)!);
        _reservation = new(_pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        try { Directory.CreateDirectory(_assets); }
        catch { _reservation.Dispose(); throw; }
    }

    internal async Task WriteAssetAsync(string name, ReadOnlyMemory<byte> content, CancellationToken token)
    {
        await using FileStream stream = new(Path.Combine(_assets, name), FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.Asynchronous);
        await stream.WriteAsync(content, token);
    }

    internal async Task CompleteAsync<T>(T report, CancellationToken token)
    {
        await JsonSerializer.SerializeAsync(_reservation, report, JsonOptions, token);
        await _reservation.FlushAsync(token);
        await _reservation.DisposeAsync();
        token.ThrowIfCancellationRequested();
        File.Move(_pending, _output, overwrite: false);
    }

    public ValueTask DisposeAsync() => _reservation.DisposeAsync();
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
