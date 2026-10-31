using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed partial class BackendProjectReadModelTests
{
    [Fact]
    public async Task G22固定参考复用核心父子矩阵并保持工程历史()
    {
        await using Fixture f = await Fixture.Create();
        Project implicitBefore = f.Workspace.Project;
        var implicitRoot = Accepted(await f.Registry.ReadGeometryReferenceAsync("client", f.Id, "0", LyricSceneIds.Root("lead")));
        Assert.Null(implicitRoot.ParentId);
        Assert.Equal(implicitBefore.Composition.CanvasWidth, implicitRoot.ParentReference.Width);
        Assert.Equal(new ProjectGeometryMatrixDto(1, 0, 0, 1, 0, 0), implicitRoot.ParentReference.WorldMatrix);
        Assert.Same(implicitBefore, f.Workspace.Project);
        SceneContainerDefinition parent = new() { Id = "geometry-parent", Placement = ScenePlacement.Create(new(.25, .3), ScenePlacementAnchor.TopLeft, 30, 1.2), ReferenceRect = new(.5, .4) };
        SceneContainerDefinition child = new() { Id = "geometry-child", ParentId = parent.Id,
            Placement = ScenePlacement.Create(new(.6, .2), ScenePlacementAnchor.BottomRight, -15, .8), ReferenceRect = new(.3, .7) };
        f.Replace(f.Workspace.Project with { LyricPresentation = f.Workspace.Project.LyricPresentation with { Nodes = [parent, child] } });
        Project before = f.Workspace.Project;
        var actual = Accepted(await f.Registry.ReadGeometryReferenceAsync("client", f.Id, "0", child.Id));
        var expected = SceneTreeGeometryResolver.Resolve([parent, child], before.Composition.CanvasWidth, before.Composition.CanvasHeight)[child.Id];
        Assert.Equal("fixedContainerReferenceV1", actual.ReferenceKind); Assert.False(actual.FinalPixelGeometry);
        Assert.Equal("0", actual.ProjectRevision); Assert.Equal(parent.Id, actual.ParentId);
        Assert.Equal(expected.Width, actual.Reference.Width); Assert.Equal(expected.Height, actual.Reference.Height);
        Assert.Equal(new ProjectGeometryMatrixDto(expected.WorldMatrix.A, expected.WorldMatrix.B, expected.WorldMatrix.C,
            expected.WorldMatrix.D, expected.WorldMatrix.Tx, expected.WorldMatrix.Ty), actual.Reference.WorldMatrix);
        Assert.Equal(new ProjectGeometryPointDto(expected.Pivot.X, expected.Pivot.Y), actual.Reference.Pivot);
        Assert.Equal(4, actual.Reference.WorldCorners.Length);
        // Runtime's fixed-container projection uses the same coordinates. Empty-node
        // editor fallback agrees; measured content geometry remains a separate layer.
        var snapshot = ProjectSnapshot.FromProject(before);
        var definition = SceneDefinitionBuilder.Build(snapshot);
        var runtime = SceneContainerGeometryResolver.Resolve(definition, before.Composition)[child.Id];
        Assert.Equal(expected.WorldMatrix, runtime.WorldMatrix); Assert.Equal(expected.Pivot, runtime.Pivot);
        var editor = SceneEditorGeometry.ForNode(snapshot, new() { Definition = definition }, child.Id)!;
        Assert.Equal(expected.WorldMatrix, editor.WorldMatrix); Assert.Equal(expected.Width, editor.BottomRight.X);
        Assert.Same(before, f.Workspace.Project); Assert.Equal(0, f.Workspace.History.UndoCount);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Registry.ReadGeometryReferenceAsync("client", f.Id, "1", child.Id)).Error!.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Registry.ReadGeometryReferenceAsync("client", f.Id, "invalid", child.Id)).Error!.Code);
        Assert.Equal(BackendErrorCode.ResourceNotFound, (await f.Registry.ReadGeometryReferenceAsync("client", f.Id, "0", "missing")).Error!.Code);
        Assert.Equal(BackendErrorCode.SessionNotFound, (await f.Registry.ReadGeometryReferenceAsync("other-client", f.Id, "0", child.Id)).Error!.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Registry.ReadGeometryReferenceAsync("client", f.Id, "0", new string('a', ProjectGeometryReferenceLimits.MaxTargetIdBytes + 1))).Error!.Code);
    }

    [Theory]
    [InlineData("nodes")]
    [InlineData("depth")]
    [InlineData("ids")]
    [InlineData("items")]
    [InlineData("slots")]
    public async Task G22几何参考输入预算整批拒绝且不改工程(string budget)
    {
        await using Fixture f = await Fixture.Create();
        SceneContainerDefinition[] nodes = budget switch
        {
            "nodes" => [.. Enumerable.Range(0, ProjectGeometryReferenceLimits.MaxNodes + 1).Select(i => new SceneContainerDefinition { Id = "node" + i.ToString(CultureInfo.InvariantCulture) })],
            "depth" => [.. Enumerable.Range(0, ProjectGeometryReferenceLimits.MaxDepth + 1).Select(i => new SceneContainerDefinition { Id = "node" + i.ToString(CultureInfo.InvariantCulture), ParentId = i == 0 ? null : "node" + (i - 1).ToString(CultureInfo.InvariantCulture) })],
            "ids" => [new() { Id = new string('汉', ProjectGeometryReferenceLimits.MaxInputIdBytes / 3 + 1) }],
            _ => [new() { Id = "node0" }],
        };
        var tree = f.Workspace.Project.LyricPresentation with { Nodes = [.. nodes] };
        if (budget == "items") tree = tree with { LineOverrides = [.. Enumerable.Range(0, ProjectGeometryReferenceLimits.MaxInputItems + 1).Select(i => new LyricLinePresentation { LineId = "line" + i.ToString(CultureInfo.InvariantCulture) })] };
        if (budget == "slots") tree = tree with { Nodes = [new() { Id = "node0", Layout = new() { SlotIds = [.. Enumerable.Repeat("node0", ProjectGeometryReferenceLimits.MaxInputItems + 1)] } }] };
        f.Replace(f.Workspace.Project with { LyricPresentation = tree });
        Project before = f.Workspace.Project;
        var result = await f.Registry.ReadGeometryReferenceAsync("client", f.Id, "0", "node0");
        Assert.Equal(BackendErrorCode.ResourceTooLarge, result.Error!.Code);
        Assert.Equal("GeometryReferenceBudgetExceeded", result.Error.FailureCode);
        Assert.Same(before, f.Workspace.Project); Assert.Equal(0, f.Workspace.History.UndoCount);
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("parent")]
    [InlineData("duplicate")]
    [InlineData("nan")]
    [InlineData("overflow")]
    [InlineData("underflow")]
    [InlineData("anchor")]
    public async Task G22非法容器形状返回稳定错误而非异常(string shape)
    {
        await using Fixture f = await Fixture.Create();
        SceneContainerDefinition node = new() { Id = "geometry" };
        node = shape switch
        {
            "cycle" => node with { ParentId = node.Id },
            "parent" => node with { ParentId = "missing" },
            "nan" => node with { ReferenceRect = new(double.NaN, 1) },
            "overflow" => node with { ReferenceRect = new(double.MaxValue, 1) },
            "underflow" => node with { Placement = ScenePlacement.Create(new(.5, .5), scale: double.Epsilon) },
            "anchor" => node with { Placement = ScenePlacement.Default with { Anchor = (ScenePlacementAnchor)999 } },
            _ => node,
        };
        f.Replace(f.Workspace.Project with { LyricPresentation = f.Workspace.Project.LyricPresentation with { Nodes = shape == "duplicate" ? [node, node] : [node] } });
        var result = await f.Registry.ReadGeometryReferenceAsync("client", f.Id, "0", node.Id);
        Assert.Equal(BackendErrorCode.CommandRejected, result.Error!.Code); Assert.Equal("GeometryReferenceInvalid", result.Error.FailureCode);
        Assert.Equal(0, f.Workspace.History.UndoCount);
    }
}
