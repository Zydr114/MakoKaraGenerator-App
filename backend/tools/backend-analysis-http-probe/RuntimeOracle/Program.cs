using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Scripting;

if (args.Length is not (2 or 3) || args.Length == 3 && args[2] != "waveform")
    throw new ArgumentException("owned-audio-file output-prefix [waveform]");
if (args.Length == 3)
{
    string source = Path.GetFullPath(args[0]);
    LoadedWaveform loaded = await AudioWaveformLoader.CreateDefault().LoadAsync(source)
        ?? throw new InvalidOperationException("Original waveform loader returned no audio stream.");
    using IncrementalHash peaksHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    byte[] range = new byte[8];
    for (int level = 0; level < loaded.Peaks.LevelCount; level++)
    {
        for (int bucket = 0; bucket < loaded.Peaks.BucketCount(level); bucket++)
        {
            PeakRange peak = loaded.Peaks.GetBucketPeak(level, bucket);
            BinaryPrimitives.WriteSingleLittleEndian(range, peak.Min);
            BinaryPrimitives.WriteSingleLittleEndian(range.AsSpan(4), peak.Max);
            peaksHash.AppendData(range);
        }
    }
    await using FileStream originalSource = File.OpenRead(source);
    string sourceHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(originalSource));
    await File.WriteAllTextAsync(args[1] + ".waveform.json", JsonSerializer.Serialize(new
    {
        loaded.Info, loaded.Peaks.SampleRate, loaded.Peaks.SampleCount, loaded.Peaks.DurationUs,
        loaded.Peaks.BaseBucketSamples, loaded.Peaks.LevelCount,
        PeakCounts = Enumerable.Range(0, loaded.Peaks.LevelCount).Select(loaded.Peaks.BucketCount).ToArray(),
        FullPyramidHash = Convert.ToHexStringLower(peaksHash.GetHashAndReset()),
        SourceHash = sourceHash,
        Oracle = "Original AudioWaveformLoader.CreateDefault; source-rate full waveform pyramid, not 48k analysis. HTTP exposes duration only; full-pyramid equality belongs to author regressions.",
    }, OracleJson.Options));
    return;
}
string input = Path.GetFullPath(args[0]);
AudioFeatureAnalysis analyzed = await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(input);
if (analyzed.Features.Availability != AudioAvailability.Available)
    throw new InvalidOperationException("Original analyzer failed: " + analyzed.DiagnosticCode);
AudioAnalysisRevision data = analyzed.ToSourceData()?.Data
    ?? throw new InvalidOperationException("Original source-data bridge did not return complete analysis.");
byte[] canonical = AudioAnalysisJson.Encode(data);
string hash = Convert.ToHexStringLower(SHA256.HashData(canonical));
if (hash != data.ContentHash || AudioAnalysisJson.Read(canonical).ContentHash != hash)
    throw new InvalidOperationException("Original canonical read/write identity mismatch.");
await File.WriteAllBytesAsync(args[1] + ".analysis.json", canonical);
await File.WriteAllTextAsync(args[1] + ".json", JsonSerializer.Serialize(new
{
    Algorithm = AudioAnalysisProfile.AlgorithmVersion, SampleRate = AudioAnalysisProfile.SampleRate,
    HopSamples = AudioAnalysisProfile.HopSamples, OriginUs = "0", data.SourceHash, data.PcmHash,
    SampleCount = data.SampleCount.ToString(CultureInfo.InvariantCulture),
    DurationUs = data.DurationUs.ToString(CultureInfo.InvariantCulture), data.Revision, data.ContentHash,
    PointCount = data.Points.Length, SizeBytes = canonical.Length,
    EndAvailability = analyzed.Features.GetAt(data.DurationUs).Availability.ToString(),
    Oracle = "Independent original AudioFeatureAnalyzer.CreateDefault -> ToSourceData -> AudioAnalysisJson complete canonical bytes. No algorithm copied.",
}, OracleJson.Options));

internal static class OracleJson
{
    internal static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
}
