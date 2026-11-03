"""Recognize supported factory contracts and adapt only isolated runner copies."""

from pathlib import Path

FACTORY = Path("src/MakoKaraGenerator.Runtime.Hosting/ProjectRenderSessionFactory.cs")
OLD_SIGNATURE = "public sealed class ProjectRenderSessionFactory(string referenceDirectory, string artifactRoot, IVideoFrameProvider videos, ReplayReason reason,"
NEW_SIGNATURE = "public sealed class ProjectRenderSessionFactory(IScriptCompilationService compiler, IVideoFrameProvider videos, ReplayReason reason,"
OLD_CALL = "CompiledProjectEffects.CompileAsync(snapshot, _compiler.Value,"
NEW_CALL = "CompiledProjectEffects.CompileAsync(snapshot, compiler,"
OLD_RUNNER = '''var factory = new ProjectRenderSessionFactory(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"),
    options.CacheRoot, videos, ReplayReason.Initial, renderBackend: options.Backend);'''
NEW_RUNNER = '''var factory = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), options.CacheRoot), videos, ReplayReason.Initial, renderBackend: options.Backend);'''
REFERENCE = '    <ProjectReference Include="../../../src/MakoKaraGenerator.Runtime.Hosting/MakoKaraGenerator.Runtime.Hosting.csproj" />'
COMPILER_REFERENCE = '    <ProjectReference Include="../../../src/MakoKaraGenerator.Scripting.Compiler/MakoKaraGenerator.Scripting.Compiler.csproj" />'
COMPILER_USING = "using MakoKaraGenerator.Scripting.Compilation;\n"


def detect(source):
    counts = tuple(source.count(item) for item in (OLD_SIGNATURE, NEW_SIGNATURE, OLD_CALL, NEW_CALL))
    if counts == (1, 0, 1, 0):
        return "legacy"
    if counts == (0, 1, 0, 1):
        return "injected"
    raise ValueError(f"unknown or mixed compilation factory layout: {counts}")


def adapt(root):
    root = Path(root)
    layout = detect((root / FACTORY).read_text(encoding="utf-8"))
    runner = root / "tools/backend-render-performance/Runner/Program.cs"
    project = runner.with_name("Runner.csproj")
    text = runner.read_text(encoding="utf-8")
    xml = project.read_text(encoding="utf-8")
    if (text.count(NEW_RUNNER) != 1 or OLD_RUNNER in text or text.count(COMPILER_USING) != 1
            or xml.count(REFERENCE) != 1 or xml.count(COMPILER_REFERENCE) != 1):
        raise ValueError("unknown or already adapted runner template")
    if layout == "legacy":
        runner.write_text(text.replace(NEW_RUNNER, OLD_RUNNER).replace(COMPILER_USING, ""), encoding="utf-8")
        project.write_text(xml.replace(COMPILER_REFERENCE + "\n", ""), encoding="utf-8")
    return layout
