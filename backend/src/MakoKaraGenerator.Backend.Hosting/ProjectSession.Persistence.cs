using MakoKaraGenerator.Application;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    internal long? SavedRevision { get; set; }
    internal bool ClosePending { get; set; }
    internal bool SaveOutstanding { get; set; }
    internal ProjectSaveCoordinator? Saves { get; set; }
    internal string? PersistenceStateRoot { get; set; }
    internal string? PinnedInputDirectory { get; set; }
}
