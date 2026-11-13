using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>固定实例及完整音频输入源身份，不引用播放器或编译缓存。</summary>
public static class ProjectEffectInputs
{
    /// <summary>没有启用实例时为空；完整分析全部字节进入身份。</summary>
    public static string? Fingerprint(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        string? effects = PresetApplicationsProjection.Effects(project, includeMissingTargets: true).InputsFingerprint;
        var materials = SourceMaterialCatalog.Collect(project);
        if (!materials.IsEmpty)
        {
            string identity = string.Join("\n", materials.Select(r => r.Path + "\0" + r.Region + "\0" + r.Material.PackageId + "\0"
                + r.Material.Revision + "\0" + r.Material.ExportId + "\0" + r.Material.Parameters.Json));
            effects = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(effects + "\0" + identity)));
        }
        if (effects is not null && !project.PresetApplications.Items.IsEmpty)
        {
            using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            identity.AppendData(Encoding.UTF8.GetBytes(effects));
            identity.AppendData(MakoKaraGenerator.Core.Persistence.PresetApplicationsJson.Encode(project.PresetApplications));
            effects = "sha256:" + Convert.ToHexStringLower(identity.GetHashAndReset());
        }
        if (effects is null || project.AudioPath is null && project.AudioAnalysis is null) return effects;
        string audio = project.AudioAnalysis?.Data is { } data ? "available:" + data.ContentHash + ":" + data.Revision
            : project.AudioAnalysis is { } failed ? "failed:" + failed.MissingStream + ":" + failed.DiagnosticCode + ":" + failed.DiagnosticMessage : "notAnalyzed";
        byte[] bytes = Encoding.UTF8.GetBytes(effects + "\0" + (project.AudioMedia?.Hash ?? project.AudioPath) + "\0" + audio);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
