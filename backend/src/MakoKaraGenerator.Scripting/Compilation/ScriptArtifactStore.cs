using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace MakoKaraGenerator.Scripting.Compilation;

/// <summary>将可重建 DLL／PDB 原子发布为本机内容地址代次，不修改工程源码。</summary>
public static class ScriptArtifactStore
{
    /// <summary>发布成功编译结果；已有代次校验后复用，不覆盖损坏缓存。</summary>
    /// <param name="root">宿主指定的本地产物目录。</param>
    /// <param name="result">已成功编译的不可变结果。</param>
    /// <returns>完整产物代次目录。</returns>
    public static string Publish(string root, ScriptCompilationResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded) throw new ArgumentException("失败编译不能发布为可运行代次。", nameof(result));
        ValidateHash(result.ArtifactHash);
        Directory.CreateDirectory(root);
        string final = Path.Combine(root, result.ArtifactHash);
        if (Directory.Exists(final)) { ValidateExisting(final, result); return final; }
        string staging = Path.Combine(root, ".build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            File.WriteAllBytes(Path.Combine(staging, "effect.dll"), result.PeImage.AsSpan());
            File.WriteAllBytes(Path.Combine(staging, "effect.pdb"), result.PdbImage.AsSpan());
            var manifest = new ArtifactManifest(2, result.ArtifactHash, Hash(result.PeImage), Hash(result.PdbImage), result.Diagnostics);
            File.WriteAllText(Path.Combine(staging, "build.json"), JsonSerializer.Serialize(manifest));
            _ = Load(staging);
            try { Directory.Move(staging, final); }
            catch (IOException) when (Directory.Exists(final)) { ValidateExisting(final, result); }
            return final;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>校验持久产物；无需 Roslyn、开发 SDK 或网络重新读取 DLL／PDB。</summary>
    /// <param name="directory">固定产物代次目录。</param>
    /// <returns>已校验的不可变产物。</returns>
    public static ScriptCompilationResult Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArtifactManifest manifest = JsonSerializer.Deserialize<ArtifactManifest>(File.ReadAllText(Path.Combine(directory, "build.json")))
            ?? throw new InvalidDataException("脚本产物清单为空。" );
        if (manifest.Format != 2 || manifest.Diagnostics.IsDefault || manifest.Diagnostics.Any(d => d is null || d.IsError
            || string.IsNullOrWhiteSpace(d.Code) || d.Message is null || d.Line < 0 || d.Column < 0))
            throw new InvalidDataException("脚本产物格式或诊断不一致。" );
        ValidateHash(manifest.ArtifactHash);
        ImmutableArray<byte> dll = [.. File.ReadAllBytes(Path.Combine(directory, "effect.dll"))];
        ImmutableArray<byte> pdb = [.. File.ReadAllBytes(Path.Combine(directory, "effect.pdb"))];
        if (dll.IsEmpty || pdb.IsEmpty || Hash(dll) != manifest.PeHash || Hash(pdb) != manifest.PdbHash)
            throw new InvalidDataException("脚本编译产物校验失败。" );
        return new(manifest.ArtifactHash, dll, pdb, manifest.Diagnostics);
    }

    private static void ValidateExisting(string directory, ScriptCompilationResult expected)
    {
        ScriptCompilationResult actual = Load(directory);
        if (actual.ArtifactHash != expected.ArtifactHash
            || !actual.PeImage.AsSpan().SequenceEqual(expected.PeImage.AsSpan())
            || !actual.PdbImage.AsSpan().SequenceEqual(expected.PdbImage.AsSpan()) || !actual.Diagnostics.SequenceEqual(expected.Diagnostics))
            throw new InvalidDataException("脚本缓存目录与请求的固定编译代次不一致。");
    }

    private static string Hash(ImmutableArray<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan()));
    private static void ValidateHash(string value)
    {
        if (value is null || value.Length != 64 || value.Any(static c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("脚本产物指纹不是规范 SHA-256。" );
    }
    private sealed record ArtifactManifest(int Format, string ArtifactHash, string PeHash, string PdbHash, ImmutableArray<ScriptCompileDiagnostic> Diagnostics);
}
