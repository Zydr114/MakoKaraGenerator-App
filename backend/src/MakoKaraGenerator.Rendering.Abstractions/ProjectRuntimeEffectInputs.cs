using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering;

/// <summary>The sole runtime effect identity; source-material identity retains its original source-only responsibility.</summary>
public static class ProjectRuntimeEffectInputs
{
    private sealed record RuntimeSources(bool OrphanOnly);
    private static readonly ConditionalWeakTable<ProjectSnapshot, RuntimeSources> Sources = new();

    /// <summary>Preserves the exact old identity without media, otherwise binds the complete validated media input revision.</summary>
    public static string? Fingerprint(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.MediaFeatureInput is not { } input)
        {
            if (snapshot.EffectInputsFingerprint is null) return null;
            // 快照不可变，目录检查只做一次；孤儿源仍参与源指纹，但没有运行输入时不要求空准备会话。
            bool orphanOnly = Sources.GetValue(snapshot, static source => new(
                !source.Effects.Instances.Any(instance => instance.Enabled)
                && source.PresetApplications.Items.Any(item => !PresetApplicationTargets.Exists(source.Lyrics, source.LyricPresentation, item.Target))
                && SourceMaterialCatalog.CollectForRuntime(source.LyricStyle, source.Lyrics, source.LyricPresentation, source.PresetApplications).IsEmpty)).OrphanOnly;
            return orphanOnly ? null : snapshot.EffectInputsFingerprint;
        }
        MediaFeatureInputValidator.Validate(input);
        if (string.IsNullOrWhiteSpace(snapshot.EffectInputsFingerprint))
            throw new PreparedFrameException("EffectInputsMismatch", "固定媒体运行输入需要完整源指纹。");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("mako-runtime-effect-inputs-media-v1"u8);
        Append(snapshot.EffectInputsFingerprint); Append(input.InputRevision.Digest.LowerHex);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
        void Append(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)bytes.Length));
            hash.AppendData(length); hash.AppendData(bytes);
        }
    }

    /// <summary>Checks complete independently validated input values, including safe failure detail omitted from canonical identity.</summary>
    public static bool SameMediaInput(MediaFeatureInput left, MediaFeatureInput right)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        MediaFeatureInputValidator.Validate(left); MediaFeatureInputValidator.Validate(right);
        if (left.GetType() != right.GetType() || left.State != right.State || left.InputRevision != right.InputRevision) return false;
        var a = left.Context; var b = right.Context; var x = a.Mapping; var y = b.Mapping;
        if (a.RequestedProfile != b.RequestedProfile || a.QueryCatalogDigest != b.QueryCatalogDigest
            || a.FixedDependencyDigest != b.FixedDependencyDigest || x.Key != y.Key || x.CanvasWidth != y.CanvasWidth
            || x.CanvasHeight != y.CanvasHeight || x.Fit != y.Fit || x.Playback != y.Playback || x.BackColor != y.BackColor
            || x.OutputMode != y.OutputMode || x.GeometryProfile != y.GeometryProfile || x.CompositeProfile != y.CompositeProfile
            || x.SamplingProfile != y.SamplingProfile) return false;
        return (left, right) switch
        {
            (MediaFeatureInput.Pending l, MediaFeatureInput.Pending r) => l.Source == r.Source && l.Request == r.Request,
            (MediaFeatureInput.AvailableFile l, MediaFeatureInput.AvailableFile r) => l.Source == r.Source && l.Artifact == r.Artifact,
            (MediaFeatureInput.AvailableSolid l, MediaFeatureInput.AvailableSolid r) => l.Source.Key == r.Source.Key && l.Source.Profile == r.Source.Profile && l.Source.Color == r.Source.Color,
            (MediaFeatureInput.Unavailable l, MediaFeatureInput.Unavailable r) => l.Source == r.Source && l.Request == r.Request
                && l.Failure.Id == r.Failure.Id && l.Failure.Code == r.Failure.Code && l.Failure.SafeDetail == r.Failure.SafeDetail,
            (MediaFeatureInput.NoMedia l, MediaFeatureInput.NoMedia r) => l.Reason == r.Reason && l.CapturedBackgroundDeclaration == r.CapturedBackgroundDeclaration,
            _ => false,
        };
    }
}
