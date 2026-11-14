using System.Text;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Execution;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class V2ParticleBoundaryTests
{
    [Fact]
    public async Task 真实出生预算失败保留可跨Worker传输的完整运行时发生身份()
    {
        var original = await LoadPackage("CursorTrail");
        string code = Encoding.UTF8.GetString(original.Files.Single(file => file.Path == "src/CursorTrail.cs").Content.AsSpan());
        const string rates = "double rate = p.GetNumber(\"rate\"), finalRate = p.GetNumber(\"finalRate\");";
        Assert.Contains(rates, code, StringComparison.Ordinal);
        var package = await ReplacePackageSource(original, "src/CursorTrail.cs",
            code.Replace(rates, "double rate = 60_000, finalRate = 60_000;", StringComparison.Ordinal));
        var snapshot = ProjectSnapshot.FromProject(BoundaryProject() with
        { Effects = new([package], [Instance(package, "birth-budget-owner")]) });
        var compiled = await Compile(snapshot);
        var geometry = new BudgetGeometry();
        var factory = new FrameSessionFactory(compiled.Effects, snapshot.EffectInputsFingerprint!, () => geometry, null);
        await using var session = await factory.CreateAsync(snapshot, CancellationToken.None);
        await Prepare(session, snapshot, 0);
        geometry.Calls.Clear();

        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await Prepare(session, snapshot, 1_000_000));

        Assert.Equal("EmissionRecordBudgetExceeded", error.Code);
        Assert.Equal("ReadParticleBirths", error.Stage);
        Assert.Equal(1_000_000, error.TimeUs);
        Assert.Equal("birth-budget-owner", error.InstanceId.Value);
        Assert.Equal(Assert.Single(compiled.Effects).RuntimeHash, error.ArtifactHash);
        Assert.Equal(new EffectPackageIdentity(package.PackageId, package.Revision, "cursor-trail"), error.PackageIdentity);
        Assert.Equal([1_000_000L], geometry.Calls); // The count gate fails before any birth-time anchor supplement.
        Assert.Equal(error.Code, Assert.IsType<EffectOutputException>(error.GetBaseException()).Code);

        // Worker serializes the exception's actual snapshot. An identity on the
        // exception alone becomes transportFailure and loses all these fields.
        var diagnostics = Assert.IsType<RuntimeDiagnosticSnapshot>(error.DiagnosticSnapshot);
        Assert.True(diagnostics.HasError);
        var occurrence = Assert.Single(diagnostics.ReadAll());
        Assert.NotEqual(Guid.Empty, occurrence.Epoch);
        Assert.Equal(1, occurrence.Sequence);
        var diagnostic = occurrence.Diagnostic;
        Assert.Same(error.Diagnostic, diagnostic);
        Assert.Equal(error.InstanceId, diagnostic.InstanceId);
        Assert.Equal(error.ArtifactHash, diagnostic.ArtifactHash);
        Assert.Equal(error.Stage, diagnostic.Stage);
        Assert.Equal(error.TimeUs, diagnostic.TimeUs);
        Assert.Equal(error.PackageIdentity, diagnostic.PackageIdentity);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Diagnostic.Severity);
        Assert.Equal(error.Code, diagnostic.Diagnostic.Code);
        Assert.Equal(error.GetBaseException().Message, diagnostic.Diagnostic.Message);
        Assert.Null(diagnostic.Diagnostic.Location); // A host budget has no author line to invent.
    }
}
