using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Effects;
using static MakoKaraGenerator.Backend.Hosting.Tests.BackendAuthorSvgWorkflowFixture;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendAuthorSvgWorkflowTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SVG作者真实编译报告绑定失败源且修复后才生成产物(bool atlas, bool asynchronous)
    {
        await using BackendAuthorSvgWorkflowFixture fixture = await CreateAsync(atlas);
        AuthorDraftResponse original = fixture.Read();
        Assert.Equal(3, original.PackageVersion);
        Assert.Equal("2.0.0", original.SdkVersion);
        Assert.True(original.CanCompile);

        AuthorDraftSourcesResponse sources = Accepted(fixture.Registry.ReadAuthorSources("client", original.DraftId,
            original.DraftRevision, original.CurrentSourceRevision));
        AuthorSourceFileDto[] editedSources = [.. sources.Files.Select(file => file with { Text = file.Text + "\n// real author save" })];
        AuthorSourceRevisionResponse sourceSaved = Accepted(await fixture.Registry.SaveAuthorSourceRevisionAsync("client", original.DraftId,
            new(original.DraftRevision, editedSources, fixture.Metadata())));
        Assert.NotEqual(original.CurrentSourceRevision, sourceSaved.SourceRevision);
        Assert.NotEqual(original.DraftRevision, sourceSaved.DraftRevision);
        var conflict = await fixture.Registry.SaveAuthorSourceRevisionAsync("client", original.DraftId,
            new(original.DraftRevision, editedSources, fixture.Metadata()));
        Assert.False(conflict.Accepted);
        Assert.Equal(BackendErrorCode.RevisionConflict, conflict.Error!.Code);
        Assert.Equal(sourceSaved.SourceRevision, fixture.Read().CurrentSourceRevision);

        AuthorPackageFilesResponse files = fixture.ReadFiles();
        AuthorPackageFileDto svg = Assert.Single(files.Files, file => file.Role == "svg");
        AuthorPackageFileDto descriptor = Assert.Single(files.Files, file => file.Role == "svgResource");
        Assert.True(svg.Editable); Assert.Equal(GoodSvg, svg.Text);
        Assert.True(descriptor.Editable); Assert.NotNull(descriptor.Text);
        AuthorPackageFileDto binary = Assert.Single(files.Files, file => file.Path == BinaryPath);
        Assert.False(binary.Editable); Assert.Null(binary.Text);

        AuthorDraftResponse badDraft = Accepted(await fixture.SaveFilesAsync(files, file => file.Path switch
        {
            SvgPath => BadSvg,
            DescriptorPath => WiderViewport(file.Text!),
            _ => file.Text!,
        }));
        Assert.NotEqual(sourceSaved.SourceRevision, badDraft.CurrentSourceRevision);
        Assert.Equal(original.CurrentSourceRevision, badDraft.Configuration!.SourceRevision);
        Assert.Null(badDraft.AdoptedSourceRevision);
        AuthorPackageFilesResponse badFiles = fixture.ReadFiles();
        Assert.Equal(BadSvg, badFiles.Files.Single(file => file.Path == SvgPath).Text);
        Assert.Equal(120, JsonNode.Parse(badFiles.Files.Single(file => file.Path == DescriptorPath).Text!)!["viewport"]!["width"]!.GetValue<int>());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(BadSvg))),
            badFiles.Files.Single(file => file.Path == SvgPath).Sha256);

        AuthorCompilationResponse failed = await CompileAsync(fixture, badDraft, asynchronous);
        Assert.False(failed.Succeeded);
        Assert.Equal(badDraft.CurrentSourceRevision, failed.SourceRevision);
        Assert.Equal(badDraft.DraftRevision, failed.DraftRevision);
        Assert.False(failed.Stale);
        Assert.Equal("none", failed.ArtifactHash);
        AuthorCompilationDiagnosticDto diagnostic = Assert.Single(failed.Diagnostics);
        Assert.True(diagnostic.IsError);
        Assert.Equal("PackageSvgReferenceMissing", diagnostic.Code);
        Assert.Equal(SvgPath, diagnostic.Path);
        Assert.Equal(0, diagnostic.Line); Assert.Equal(0, diagnostic.Column);
        AuthorCompilationResponse persisted = Accepted(fixture.Registry.ReadAuthorCompilation("client", original.DraftId));
        Assert.Equal(failed.SourceRevision, persisted.SourceRevision);
        Assert.Equal(diagnostic, Assert.Single(persisted.Diagnostics));
        Assert.Equal(original.CurrentSourceRevision, fixture.Read().Configuration!.SourceRevision);

        AuthorDraftResponse repaired = Accepted(await fixture.SaveFilesAsync(fixture.ReadFiles(),
            file => file.Path == SvgPath ? GoodSvg : file.Text!));
        Assert.NotEqual(badDraft.CurrentSourceRevision, repaired.CurrentSourceRevision);
        Assert.Equal(failed.SourceRevision, repaired.LastCompilation!.SourceRevision);
        Assert.False(repaired.LastCompilation.Succeeded);
        Assert.True(repaired.LastCompilation.Stale);
        AuthorCompilationResponse success = await CompileAsync(fixture, repaired, asynchronous);
        Assert.True(success.Succeeded, string.Join('\n', success.Diagnostics.Select(item => item.Message)));
        Assert.NotEqual("none", success.ArtifactHash);
        Assert.DoesNotContain(success.Diagnostics, item => item.IsError);
        Assert.Equal(repaired.CurrentSourceRevision, success.SourceRevision);
        Assert.Equal(repaired.DraftRevision, success.DraftRevision);
        Assert.False(success.Stale);
        AuthorDraftResponse current = fixture.Read();
        Assert.True(current.LastCompilation!.Succeeded);
        Assert.Equal(success.SourceRevision, current.LastCompilation.SourceRevision);
        Assert.Equal(original.CurrentSourceRevision, current.Configuration!.SourceRevision);
        Assert.Null(current.AdoptedSourceRevision);

        AuthorSourceRevisionsResponse history = Accepted(await fixture.Registry.ReadAuthorSourceRevisionsAsync("client",
            original.DraftId, current.DraftRevision));
        Assert.False(history.Revisions.Single(item => item.SourceRevision == failed.SourceRevision).Compilation!.Succeeded);
        Assert.True(history.Revisions.Single(item => item.SourceRevision == success.SourceRevision).Compilation!.Succeeded);
        Assert.Equal(BadSvg, fixture.ReadFiles(failed.SourceRevision).Files.Single(file => file.Path == SvgPath).Text);
        EffectPackageContents downloaded = await fixture.DownloadAsync();
        Assert.Equal(BinaryBytes, downloaded.Files.Single(file => file.Path == BinaryPath).Content.ToArray());
        AuthorPackageFileDto finalBinary = fixture.ReadFiles().Files.Single(file => file.Path == BinaryPath);
        Assert.Equal(binary.Sha256, finalBinary.Sha256); Assert.Equal(binary.SizeBytes, finalBinary.SizeBytes);
    }

    [Theory]
    [InlineData(false, SvgPath)]
    [InlineData(false, DescriptorPath)]
    [InlineData(true, SvgPath)]
    [InlineData(true, DescriptorPath)]
    public async Task SVG包编辑必须提交全部SVG文本且拒绝后不推进修订(bool atlas, string omittedPath)
    {
        await using BackendAuthorSvgWorkflowFixture fixture = await CreateAsync(atlas);
        AuthorDraftResponse before = fixture.Read();
        var rejected = await fixture.SaveFilesAsync(fixture.ReadFiles(), file => file.Text!, omittedPath);
        Assert.False(rejected.Accepted);
        Assert.Equal(BackendErrorCode.InvalidRequest, rejected.Error!.Code);
        Assert.Equal("textFiles", rejected.Error.Field);
        AuthorDraftResponse after = fixture.Read();
        Assert.Equal(before.DraftRevision, after.DraftRevision);
        Assert.Equal(before.CurrentSourceRevision, after.CurrentSourceRevision);
        Assert.Equal(before.Configuration!.SourceRevision, after.Configuration!.SourceRevision);
    }

    private static string WiderViewport(string text)
    {
        JsonObject descriptor = JsonNode.Parse(text)!.AsObject();
        descriptor["viewport"]!["width"] = 120;
        return descriptor.ToJsonString();
    }

    private static async Task<AuthorCompilationResponse> CompileAsync(BackendAuthorSvgWorkflowFixture fixture,
        AuthorDraftResponse draft, bool asynchronous)
    {
        if (!asynchronous) return Accepted(await fixture.Registry.CompileAuthorDraftAsync("client", draft.DraftId,
            new(draft.DraftRevision, fixture.Metadata(), SourceRevision: draft.CurrentSourceRevision)));
        JobResponse accepted = Accepted(await fixture.Registry.StartAuthorCompilationAsync("client", draft.DraftId,
            new(draft.DraftRevision, fixture.Metadata(), "async", draft.CurrentSourceRevision)));
        Assert.Equal(draft.CurrentSourceRevision, accepted.AuthorCompilationInput!.SourceRevision);
        Assert.Equal(draft.DraftRevision, accepted.AuthorCompilationInput.DraftRevision);
        Stopwatch elapsed = Stopwatch.StartNew();
        JobResponse job;
        do
        {
            job = Accepted(fixture.Registry.ReadJob("client", accepted.JobId));
            if (job.State is "succeeded" or "failed" or "cancelled") break;
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30), "真实SVG作者编译未在30秒内结束。");
            await Task.Delay(20);
        } while (true);
        // A produced failure diagnostic is still a successfully completed compilation job.
        Assert.Equal("succeeded", job.State);
        Assert.NotNull(job.AuthorCompilationResult);
        Assert.Null(job.Error);
        return Accepted(fixture.Registry.ReadAuthorCompilationResult("client", accepted.JobId));
    }
}
