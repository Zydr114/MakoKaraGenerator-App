using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Backend.Worker;

/// <summary>Worker 只读实际部署身份和 Host 固定输入；此类型没有编译实现。</summary>
internal static class WorkerScriptInputs
{
    private static readonly Lazy<ScriptCompilationIdentity> Deployment = new(() => ScriptCompilationIdentity.ForDeployment(
        Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));
    internal static ScriptCompilationIdentity Identity => Deployment.Value;
    internal static FixedScriptArtifactProvider Empty => new(Identity, []);
    internal static (ReadOnlyMemory<byte> Source, IScriptCompilationService Compiler) Read(ReadOnlyMemory<byte> source, CancellationToken token)
    {
        if (!ScriptPreparedInputCodec.IsPrepared(source.Span)) return (source, Empty);
        var prepared = ScriptPreparedInputCodec.Read(source, Identity, token);
        return (prepared.Source, prepared.Compiler);
    }
}
