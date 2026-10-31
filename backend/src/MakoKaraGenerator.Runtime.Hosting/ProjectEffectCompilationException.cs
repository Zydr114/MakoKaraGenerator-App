using System.Collections.Immutable;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Runtime.Hosting;

/// <summary>固定包、实例及导出身份与真实逻辑源码位置；不持有作者对象。</summary>
public sealed record ProjectEffectSourceDiagnostic(string PackageId, string Revision, string? InstanceId, string? ExportId, EffectSourceDiagnostic Source);

/// <summary>正式编译失败的不可变来源和逻辑行列；不持有编译器或作者对象。</summary>
public sealed class ProjectEffectCompilationException : InvalidOperationException
{
    /// <summary>从当前失败编译收集完整不可变来源，不回退旧修订。</summary>
    public ProjectEffectCompilationException(CompiledProjectEffects compiled) : this(Collect(compiled)) { }
    private ProjectEffectCompilationException(ImmutableArray<ProjectEffectSourceDiagnostic> diagnostics)
        : base("效果编译失败：\n" + string.Join('\n', diagnostics.Select(value =>
            $"{value.PackageId} {value.Revision} {value.InstanceId} {value.ExportId} {value.Source.DisplayLabel}"))) => Diagnostics = diagnostics;
    /// <summary>每个失败源码诊断及引用它的启用实例。</summary>
    public ImmutableArray<ProjectEffectSourceDiagnostic> Diagnostics { get; }

    private static ImmutableArray<ProjectEffectSourceDiagnostic> Collect(CompiledProjectEffects compiled)
    {
        List<ProjectEffectSourceDiagnostic> result = [];
        foreach (var package in compiled.Packages.Where(package => !package.Succeeded))
        {
            var source = package.Definition.Contents;
            var instances = compiled.Source.Instances.Where(instance => instance.Enabled && instance.PackageId == source.PackageId && instance.Revision == source.Revision).ToArray();
            foreach (var diagnostic in package.ControllerArtifact?.Diagnostics ?? [])
            {
                var value = new EffectSourceDiagnostic(diagnostic.Code, diagnostic.Message, diagnostic.IsError, diagnostic.Path, diagnostic.Line, diagnostic.Column);
                if (instances.Length == 0) result.Add(new(source.PackageId, source.Revision, null, null, value));
                foreach (var instance in instances) result.Add(new(source.PackageId, source.Revision, instance.Id, instance.ExportId, value));
            }
        }
        return [.. result];
    }
}
