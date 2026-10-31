using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using static MakoKaraGenerator.Core.Diagnostics.DiagnosticList;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>读取并解析同一份固定工程文件字节；不迁移或修改源文件。</summary>
internal static class ProjectJsonReader
{
    internal static byte[]? ReadBytes(string path, List<ProjectIssue> issues)
    {
        try { return File.ReadAllBytes(path); }
        catch (IOException exception)
        {
            Add(issues, DiagnosticCode.MalformedJson, IssueSeverity.Error,
                $"无法读取 {path}：{exception.Message}", path);
        }
        catch (UnauthorizedAccessException exception)
        {
            Add(issues, DiagnosticCode.MalformedJson, IssueSeverity.Error,
                $"没有权限读取 {path}：{exception.Message}", path);
        }
        return null;
    }

    /// <summary>
    /// 读取当前 schema 的文档。断代版本不从旧 schema 自动迁移。
    /// </summary>
    /// <typeparam name="T">DTO 类型。</typeparam>
    /// <param name="bytes">本次固定读取的文件字节。</param>
    /// <param name="path">文件路径。</param>
    /// <param name="targetVersion">该文件当前支持的 schema 版本。</param>
    /// <param name="issues">诊断收集器。</param>
    /// <param name="migrations">可选的低版本迁移步骤；省略时低版本直接拒绝。</param>
    /// <returns>DTO；结构或版本层面失败时为 <see langword="null"/>（失败原因已进 <paramref name="issues"/>）。</returns>
    /// <remarks>两份文件各有 schema 版本；不兼容时只报告，不改写源文件。</remarks>
    internal static T? Deserialize<T>(
        byte[] bytes,
        string path,
        int targetVersion,
        List<ProjectIssue> issues,
        IReadOnlyList<IProjectSchemaMigration>? migrations = null)
        where T : class
    {
        string displayName = Path.GetFileName(path);
        string location = $"{displayName}.schemaVersion";

        try
        {
            if (JsonNode.Parse(JsonBytes(bytes).Span, nodeOptions: null, DocumentOptions()) is not JsonObject document)
            {
                Add(
                    issues,
                    DiagnosticCode.MalformedJson,
                    IssueSeverity.Error,
                    $"{displayName} 的顶层不是一个 JSON 对象。",
                    displayName);

                return null;
            }

            if (!TryReadSchemaVersion(document, targetVersion, location, issues, out int version))
            {
                return null;
            }

            if (version != targetVersion && migrations is null)
            {
                Add(issues, DiagnosticCode.UnsupportedSchemaVersion, IssueSeverity.Error,
                    $"schema 版本 {version} 与当前要求的 {targetVersion} 不一致，文件未更改。", location);
                return null;
            }

            if (version != targetVersion)
            {
                MigrationLog migrationLog;
                try
                {
                    migrationLog = ProjectSchemaMigrator.Migrate(document, version, targetVersion, migrations);
                }
                catch (InvalidOperationException exception)
                {
                    Add(issues, DiagnosticCode.UnsupportedSchemaVersion, IssueSeverity.Error,
                        $"schema 版本 {version} 无法迁移到 {targetVersion}：{exception.Message}，文件未更改。", location);
                    return null;
                }

                if (!migrationLog.IsEmpty)
                {
                    Add(issues, DiagnosticCode.ProjectSchemaMigrated, IssueSeverity.Info,
                        string.Join(" ", migrationLog.Entries), location);
                }
            }

            return document.Deserialize<T>(ProjectJson.Options);
        }
        catch (JsonException exception)
        {
            Add(
                issues,
                DiagnosticCode.MalformedJson,
                IssueSeverity.Error,
                $"{displayName} 不是合法的 JSON：{exception.Message}",
                displayName);

            return null;
        }
    }

    /// <summary>读取 <c>schemaVersion</c> 并判断当前程序能否处理它。</summary>
    /// <param name="document">文档。</param>
    /// <param name="supportedVersion">该文件当前支持的版本（两份文件各自的版本线）。</param>
    /// <param name="location">诊断位置。</param>
    /// <param name="issues">诊断收集器。</param>
    /// <param name="version">读到的版本。</param>
    /// <returns>能处理时为 <see langword="true"/>。</returns>
    private static bool TryReadSchemaVersion(
        JsonObject document,
        int supportedVersion,
        string location,
        List<ProjectIssue> issues,
        out int version)
    {
        version = 0;

        if (document["schemaVersion"] is not JsonValue value || !value.TryGetValue(out int parsed))
        {
            Add(
                issues,
                DiagnosticCode.UnsupportedSchemaVersion,
                IssueSeverity.Error,
                "缺少 schemaVersion，无法判断文件版本。",
                location);

            return false;
        }

        if (parsed > supportedVersion)
        {
            Add(
                issues,
                DiagnosticCode.UnsupportedSchemaVersion,
                IssueSeverity.Error,
                $"schema 版本 {parsed} 高于当前支持的 {supportedVersion}："
                + "该文件由更新的版本创建，当前程序无法读取。",
                location);

            return false;
        }

        version = parsed;
        return true;
    }

    /// <summary>与 <see cref="ProjectJson.Options"/> 一致的手工编辑容忍度（注释与尾随逗号）。</summary>
    internal static JsonDocumentOptions DocumentOptions() => new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // UTF-8 BOM 是编码标记，内容hash仍包含它；解析时沿用原ReadAllText的容忍度。
    internal static ReadOnlyMemory<byte> JsonBytes(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes.AsMemory(3) : bytes;

}
