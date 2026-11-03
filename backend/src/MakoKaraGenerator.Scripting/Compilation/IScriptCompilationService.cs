namespace MakoKaraGenerator.Scripting.Compilation;

/// <summary>固定源码到不可变产物的边界；实现可编译源码或严格消费预先编译的产物。</summary>
public interface IScriptCompilationService
{
    /// <summary>读取固定源码、完整入口集合的结果；取消不转换为作者诊断。</summary>
    Task<ScriptCompilationResult> CompileAsync(IEnumerable<ScriptSource> sources, IEnumerable<string> entryTypes,
        CancellationToken cancellationToken = default);
}
