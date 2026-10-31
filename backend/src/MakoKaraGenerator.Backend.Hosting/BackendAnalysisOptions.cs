namespace MakoKaraGenerator.Backend.Hosting;

public sealed record BackendAnalysisOptions
{
    public int MaxResultBytes { get; init; } = AnalysisResultCodec.MaximumResultBytes;
    public long MaxResultStorageBytes { get; init; } = 64 * 1024 * 1024;
    internal void Validate()
    {
        if (MaxResultBytes is <= 0 or > AnalysisResultCodec.MaximumResultBytes || MaxResultStorageBytes is <= 0 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(BackendAnalysisOptions));
    }
}
