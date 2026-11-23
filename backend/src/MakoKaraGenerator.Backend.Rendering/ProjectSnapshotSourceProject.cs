using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering;

/// <summary>将 Runtime 已投影快照还原为领域工程源，避免再次投影预设实例。</summary>
public static class ProjectSnapshotSourceProject
{
    /// <summary>验证固定预设投影实例完全一致后，只将直接实例放回源工程。</summary>
    public static Project Restore(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var sourceIds = snapshot.PresetApplications.Items.SelectMany(item => item.Identities).Select(item => item.InstanceId).ToHashSet(StringComparer.Ordinal);
        var projected = snapshot.PresetApplications.Items
            .Where(item => PresetApplicationTargets.Exists(snapshot.Lyrics, snapshot.LyricPresentation, item.Target))
            .SelectMany(item => item.ProjectInstances()).ToDictionary(item => item.Id, StringComparer.Ordinal);
        var actual = snapshot.Effects.Instances.Where(item => sourceIds.Contains(item.Id)).ToArray();
        if (actual.Length != projected.Count || actual.Any(item => !projected.TryGetValue(item.Id, out var expected) || !item.Equals(expected)))
            throw new SnapshotContractException("SnapshotPresetInvalid", "固定快照的预设投影实例与源身份不一致。");
        return new()
        {
            Name = snapshot.Name, SchemaVersion = snapshot.SchemaVersion, Composition = snapshot.Composition, Lyrics = snapshot.Lyrics,
            LyricStyle = snapshot.LyricStyle, LyricStylePresetId = snapshot.LyricStylePresetId,
            LyricPresentation = snapshot.LyricPresentation, Background = snapshot.Background, AudioPath = snapshot.AudioPath,
            AudioMedia = snapshot.AudioMedia, AudioAnalysis = snapshot.AudioAnalysis,
            Effects = actual.Length == 0 ? snapshot.Effects : new(snapshot.Effects.Packages,
                snapshot.Effects.Instances.Where(item => !sourceIds.Contains(item.Id)), snapshot.Effects.Budgets),
            PresetApplications = snapshot.PresetApplications,
        };
    }
}
