using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using SkiaSharp;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task G20G24真实HTTP完整编排编辑实际Worker动画几何像素及保存重开闭环()
    {
        await using Fixture f = await Fixture.Create(
            injectedFactory: new WorkerRenderFrameSessionFactory(new(FindWorkerApphost())), presetProfile: true);
        f.Projects.ConfigureSceneMeasurements(new(FindWorkerApphost()));
        ProjectSession project = await f.OpenNoAudioProject();
        NodeHttpHarness http = new(f, project.ProjectSessionId);
        await http.Send(new SetCanvasSizeCommandDto(320, 180));
        await http.Send(new SetOutputDurationCommandDto("8000000"));
        string first = await WorkflowAddLine(f, http, "KARAOKE LONG LAYOUT REFERENCE", "2000000", "4000000");
        string second = await WorkflowAddLine(f, http, "SECOND", "5000000", "7000000");
        var originalLyrics = await WorkflowLyrics(f, http);
        var program = (await http.Read()).Programs.Single(p => p.BranchId == "primary");
        SceneMeasurementDataDto plain = await WorkflowMeasure(f, http, "time", "2100000");
        ProjectLineEnterAnimationDto projectAnimation = new("400000", "Linear", .2f, .8f, 12);
        await http.Send(new SetProjectEnterAnimationCommandDto(projectAnimation));
        SceneMeasurementDataDto inherited = await WorkflowMeasure(f, http, "time", "2100000");
        Assert.InRange(WorkflowLine(inherited, first).Opacity, .3999, .4001);
        AssertWorkflowPixelsDiffer(plain.Png, inherited.Png);
        await http.Send(new SetLineEnterAnimationCommandDto("primary", first, new("0", "Linear", 1, 1, 0)));
        SceneMeasurementDataDto neutral = await WorkflowMeasure(f, http, "time", "2100000");
        Assert.InRange(WorkflowLine(neutral, first).Opacity, .9999, 1.0001);
        AssertWorkflowPixelsDiffer(inherited.Png, neutral.Png);
        await http.Send(new SetLineEnterAnimationCommandDto("primary", first, null));
        AssertWorkflowPixelsEqual(inherited.Png, (await WorkflowMeasure(f, http, "time", "2100000")).Png);

        var group = NodeResult(await http.Send(new CreateOrchestrationNodeCommandDto("group", program.RootContainerId, "工作流组", null)), "createNode", 1);
        var sequence = NodeResult(await http.Send(new CreateOrchestrationNodeCommandDto("slotSequence", group.NodeId, "工作流槽位", 2)), "createNode", 3);
        await http.Send(new AddOrchestrationSlotCommandDto(sequence.NodeId, "slot/workflow/extra", "临时槽位"));
        string[] expanded = (await http.Read()).Nodes.Single(n => n.Id == sequence.NodeId).Layout!.SlotIds.Reverse().ToArray();
        await http.Send(new ReorderOrchestrationSlotsCommandDto(sequence.NodeId, expanded));
        await http.Send(new RemoveOrchestrationNodeCommandDto("slot/workflow/extra"));
        string[] slots = (await http.Read()).Nodes.Single(n => n.Id == sequence.NodeId).Layout!.SlotIds;
        Assert.Equal(sequence.CreatedNodeIds!.Skip(1).Reverse(), slots);
        var copy = NodeResult(await http.Send(new CopyOrchestrationSubtreeCommandDto(group.NodeId, "临时副本")), "copySubtree", 4);
        await http.Send(new RemoveOrchestrationNodeCommandDto(copy.NodeId));
        await http.Send(new SetOrchestrationDefaultContainerCommandDto("primary", sequence.NodeId));
        var slotAnimation = (await http.Read()).Nodes.Single(n => n.Id == sequence.NodeId).Layout!.SlotAnimation! with
        {
            RevealMode = "LeadBeforeNextStart", RevealLeadUs = "0", MaxVisibleLines = 2,
            RetentionPolicy = "UntilLineEnd", EnterDurationUs = "400000", ExitDurationUs = "400000",
            Easing = "Linear", ExitEasing = "Linear", EnterFrom = new(0, .75, -12, 8, -8),
            Hold = new(1, 1, 0, 0, 0), ExitTo = new(0, .8, 16, -8, 8),
        };
        await http.Send(new SetOrchestrationSlotAnimationCommandDto(sequence.NodeId, slotAnimation));
        SceneMeasurementDataDto enter = await WorkflowMeasure(f, http, "time", "2100000");
        SceneMeasurementDataDto hold = await WorkflowMeasure(f, http, "time", "3000000");
        SceneMeasurementDataDto exit = await WorkflowMeasure(f, http, "time", "3900000");
        Assert.Equal(slots[0], WorkflowLine(enter, first).SlotId);
        Assert.InRange(WorkflowLine(enter, first).Opacity, .2499, .2501);
        Assert.InRange(WorkflowLine(hold, first).Opacity, .9999, 1.0001);
        Assert.InRange(WorkflowLine(exit, first).Opacity, .2499, .2501);
        AssertWorkflowPixelsDiffer(enter.Png, hold.Png); AssertWorkflowPixelsDiffer(hold.Png, exit.Png);
        Assert.DoesNotContain((await WorkflowMeasure(f, http, "time", "4100000")).Elements,
            item => item.Kind == "line" && item.LineId == first && item.Visible);

        await http.Send(new SetOrchestrationNodeGeometryCommandDto(sequence.NodeId, new(.5, .5, "Center", 7, .9), new(.9, .9)));
        await http.Send(new SetOrchestrationNodeGeometryCommandDto(slots[0], new(.3, .45, "Center", -9, 1), new(.45, .7)));
        await http.Send(new SetOrchestrationNodeGeometryCommandDto(slots[1], new(.7, .55, "Center", 9, 1), new(.45, .7)));
        SceneMeasurementDataDto positioned = await WorkflowMeasure(f, http, "time", "3000000");
        AssertWorkflowPixelsDiffer(hold.Png, positioned.Png);
        Assert.NotEqual(JsonSerializer.Serialize(WorkflowLine(hold, first).WorldCorners, Json), JsonSerializer.Serialize(WorkflowLine(positioned, first).WorldCorners, Json));
        await http.Send(new SetOrchestrationLayoutSettingsCommandDto(sequence.NodeId, new(.05, .1, .9, .8), .45));
        SceneMeasurementDataDto limited = await WorkflowMeasure(f, http, "time", "3000000");
        AssertWorkflowPixelsDiffer(positioned.Png, limited.Png);
        Assert.True(WorkflowQuadArea(WorkflowLine(limited, first).WorldCorners) < WorkflowQuadArea(WorkflowLine(positioned, first).WorldCorners));
        ProjectLineLocalTransformDto local = new(.08, -.05, 13, 1.15);
        await http.Send(new SetLineLocalTransformCommandDto("primary", first, local));
        SceneMeasurementDataDto transformed = await WorkflowMeasure(f, http, "time", "3000000");
        AssertWorkflowPixelsDiffer(limited.Png, transformed.Png);
        Assert.NotEqual(WorkflowLine(limited, first).Pivot, WorkflowLine(transformed, first).Pivot);
        AssertWorkflowQuad(WorkflowLine(transformed, first).WorldCorners);
        AssertWorkflowInkBounds(transformed.Png, WorkflowLine(transformed, first).WorldCorners);
        ProjectGeometryReferenceDto reference = transformed.Elements.Single(item => item.Id == sequence.NodeId).FixedReference!;
        AssertWorkflowReference(reference);

        var countdown = await http.Send(new CreateOrchestrationCountdownCommandDto(group.NodeId, "生成前奏", 3));
        string indicator = countdown.LeadInResult!.IndicatorContainerId!;
        string[] marks = countdown.LeadInResult.MarkNodeIds!;
        LeadInGenerationSettingsDto settings = new(true, false, "8000000", "1000000", indicator, []);
        string beforePreview = http.Revision;
        ProjectCommandResponse analysis = await http.Send(new PreviewLeadInGenerationCommandDto("primary", settings));
        Assert.False(analysis.Changed); Assert.Equal(beforePreview, http.Revision);
        Assert.Contains(analysis.LeadInGenerationResult!.Candidates, candidate => candidate.TargetLineId == first && candidate.Status == "New");
        await http.Send(new ApplyLeadInGenerationCommandDto("primary", settings, [first]));
        ProjectOrchestrationLeadInDto cue = Assert.Single((await http.Read()).Programs.Single(p => p.BranchId == "primary").LeadIns!);
        Assert.Equal(first, cue.TargetLineId); Assert.Equal("1000000", cue.StartUs); Assert.Equal("2000000", cue.EndUs);
        Assert.False((await http.Send(new ApplyLeadInGenerationCommandDto("primary", settings, [first]))).Changed);
        await http.Send(new SetOrchestrationMarkTextCommandDto(marks[0], "GO"));
        await http.Send(new AddOrchestrationMarkCommandDto(indicator, "!"));
        marks = (await http.Read()).Nodes.Single(n => n.Id == indicator).Content!.MarkNodeIds!.Reverse().ToArray();
        await http.Send(new ReorderOrchestrationMarksCommandDto(indicator, marks));
        await http.Send(new SpaceOrchestrationMarksCommandDto(indicator));
        await http.Send(new SetLeadInAttachmentCommandDto("primary", cue.Id, indicator, "FixedParent"));
        await http.Send(new SetOrchestrationNodeGeometryCommandDto(indicator, new(.5, .2, "Center", -5, 1), new(.5, .25)));
        SceneMeasurementDataDto markBaseline = await WorkflowMeasure(f, http, "time", "1500000");
        await http.Send(new SetOrchestrationMarkTextCommandDto(marks[0], "READY"));
        SceneMeasurementDataDto editedMark = await WorkflowMeasure(f, http, "time", "1500000");
        AssertWorkflowPixelsDiffer(markBaseline.Png, editedMark.Png);
        Assert.Contains(editedMark.Elements, item => item.Id == marks[0] && item.Visible);
        f.Projects.ConfigureTemplateLibrary();
        string definition = TemplateLibraryWireTests.Definition("跨任务静态标记模板")
            .Replace("\"opacity\":1", "\"opacity\":0.3", StringComparison.Ordinal)
            .Replace("\"properties\":[\"typography.fontSizePx\"]", "\"properties\":[\"opacity\"]", StringComparison.Ordinal);
        using (HttpResponseMessage templateCreated = await TemplatePost(f, "missing", "{\"type\":\"create\",\"definition\":" + definition + "}"))
            Assert.Equal(HttpStatusCode.OK, templateCreated.StatusCode);
        var library = (await f.Http.GetFromJsonAsync<TemplateListResponse>("/api/v1/template-libraries/personal/templates", Json))!;
        var template = Assert.Single(library.Templates);
        await http.Send(new ApplyPresetApplicationCommandDto("personal", template.TemplateId, library.Revision,
            [new("node", NodeId: indicator, IncludeDescendants: true)], null));
        SceneMeasurementDataDto final = await WorkflowMeasure(f, http, "time", "1500000");
        AssertWorkflowPixelsDiffer(editedMark.Png, final.Png);
        Assert.Equal(BackendSnapshotProfiles.StaticLyricsScenePresets, final.SourceProfile);
        AssertWorkflowPixelsEqual(final.Png, await WorkflowPreviewPng(f, project.ProjectSessionId, http.Revision, "1500000"));
        await http.Send(new ProjectCommandDto("history.undo"));
        AssertWorkflowPixelsEqual(editedMark.Png, (await WorkflowMeasure(f, http, "time", "1500000")).Png);
        var noop = await http.Send(new SetOrchestrationMarkTextCommandDto(marks[0], "READY"));
        Assert.False(noop.Changed); Assert.True(noop.CanRedo);
        await http.Send(new ProjectCommandDto("history.redo"));
        AssertWorkflowPixelsEqual(final.Png, (await WorkflowMeasure(f, http, "time", "1500000")).Png);

        var beforeRead = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(http.Path, Json))!;
        SceneMeasurementDataDto layout = await WorkflowMeasure(f, http, "layout", null);
        SceneMeasurementDataDto layoutAgain = await WorkflowMeasure(f, http, "layout", null);
        Assert.Equal(final.SourceSha256, layout.SourceSha256);
        Assert.NotEqual(final.SampleInputFingerprint, layout.SampleInputFingerprint);
        Assert.Equal(layout.SampleInputFingerprint, layoutAgain.SampleInputFingerprint);
        AssertWorkflowPixelsEqual(layout.Png, layoutAgain.Png);
        Assert.Equal(SceneMeasurementContract.LayoutProfile, layout.SamplingProfile);
        Assert.All(slots, slot => Assert.Contains(layout.Elements, item => item.Kind == "line" && item.SlotId == slot && item.Representative));
        Assert.All(layout.Elements.Where(item => item.Kind == "line" && item.Representative), item => Assert.DoesNotContain(item.LineId, new[] { first, second }));
        var afterRead = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(http.Path, Json))!;
        Assert.Equal(beforeRead.ProjectRevision, afterRead.ProjectRevision); Assert.Equal(beforeRead.IsDirty, afterRead.IsDirty);
        Assert.Equal(beforeRead.CanUndo, afterRead.CanUndo); Assert.Equal(beforeRead.CanRedo, afterRead.CanRedo);
        var finalLyrics = await WorkflowLyrics(f, http);
        Assert.Equal(originalLyrics.Lines.Select(line => line.Time), finalLyrics.Lines.Select(line => line.Time));
        var finalModel = await http.Read();
        string reopenedId = await WorkflowSaveReopen(f, http);
        NodeHttpHarness reopened = new(f, reopenedId);
        var restored = await reopened.Read();
        Assert.Equal(JsonSerializer.Serialize(finalModel.Nodes, Json), JsonSerializer.Serialize(restored.Nodes, Json));
        Assert.Equal(JsonSerializer.Serialize(finalModel.Programs, Json), JsonSerializer.Serialize(restored.Programs, Json));
        Assert.Equal(finalModel.ProjectEnterAnimation, restored.ProjectEnterAnimation);
        SceneMeasurementDataDto restoredTime = await WorkflowMeasure(f, reopened, "time", "1500000");
        Assert.Equal(final.SourceSha256, restoredTime.SourceSha256);
        Assert.Equal(final.SampleInputFingerprint, restoredTime.SampleInputFingerprint);
        AssertWorkflowPixelsEqual(final.Png, restoredTime.Png);
        Assert.Equal(layout.SampleInputFingerprint, (await WorkflowMeasure(f, reopened, "layout", null)).SampleInputFingerprint);
    }

    [Fact]
    public async Task G23真实HTTP十帧透明纯视频导出完整解码与同帧Worker像素对照()
    {
        WorkerBackendExportExecutor exporter = new(FindWorkerApphost(), TestMediaToolPaths.Ffmpeg, TestMediaToolPaths.Ffprobe,
            exportOptions: new BackendExportOptions { MaxMediaBytes = WorkerExportContract.MaximumMediaBytes, MaxAudioBytes = WorkerExportContract.MaximumAudioBytes });
        BackendExportCapabilities capabilities = await exporter.ProbeAsync();
        Assert.True(capabilities.GuardAvailable, capabilities.FailureCode); Assert.True(capabilities.TransparentProRes4444);
        await using Fixture f = await Fixture.Create(injectedFactory: new WorkerRenderFrameSessionFactory(new(FindWorkerApphost())),
            injectedExportExecutor: exporter, exportCapabilities: capabilities);
        f.Http.Timeout = TimeSpan.FromSeconds(30);
        f.Projects.ConfigureSceneMeasurements(new(FindWorkerApphost()));
        ProjectSession project = await f.OpenNoAudioProject();
        NodeHttpHarness http = new(f, project.ProjectSessionId);
        await http.Send(new SetCanvasSizeCommandDto(320, 180));
        await http.Send(new SetFrameRateCommandDto(5, 1));
        await http.Send(new SetOutputDurationCommandDto("2000000"));
        string line = await WorkflowAddLine(f, http, "GO", "1000000", "2000000");
        var program = (await http.Read()).Programs.Single(p => p.BranchId == "primary");
        await http.Send(new SetProjectEnterAnimationCommandDto(new("200000", "Linear", .2f, .7f, 10)));
        await http.Send(new SetLineLocalTransformCommandDto("primary", line, new(.05, .05, 8, 1.1)));
        var countdown = await http.Send(new CreateOrchestrationCountdownCommandDto(program.RootContainerId, "导出三点", 3));
        string indicator = countdown.LeadInResult!.IndicatorContainerId!;
        string mark = countdown.LeadInResult.MarkNodeIds![0];
        await http.Send(new ApplyLeadInGenerationCommandDto("primary", new(true, false, "8000000", "1000000", indicator, []), [line]));
        var cue = Assert.Single((await http.Read()).Programs.Single(p => p.BranchId == "primary").LeadIns!);
        await http.Send(new SetLeadInAttachmentCommandDto("primary", cue.Id, indicator, "FixedParent"));
        await http.Send(new SetOrchestrationNodeGeometryCommandDto(indicator, new(.5, .2, "Center", 0, 1), new(.5, .25)));
        await http.Send(new SetOrchestrationMarkTextCommandDto(mark, "!"));
        await http.Send(new SetMarkAppearanceCommandDto(mark, [new StyleOpacityChangeDto(.45f)]));
        SceneMeasurementDataDto cueFrame = await WorkflowMeasure(f, http, "time", "600000");
        SceneMeasurementDataDto lineFrame = await WorkflowMeasure(f, http, "time", "1400000");
        Assert.Contains(cueFrame.Elements, element => element.Id == mark && element.Visible);
        AssertWorkflowPixelsDiffer(cueFrame.Png, lineFrame.Png);
        AssertWorkflowPixelsEqual(cueFrame.Png, await WorkflowPreviewPng(f, project.ProjectSessionId, http.Revision, "600000"));
        using HttpResponseMessage accepted = await f.Http.PostAsJsonAsync(http.Path + "/export-jobs",
            new ExportJobRequest(http.Revision, "transparentProRes4444", false, f.Metadata()), Json);
        JobResponse job = await NodeTerminal(f, accepted);
        using HttpResponseMessage result = await f.Http.GetAsync($"/api/v1/jobs/{job.JobId}/export-result");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        ExportReportDto report = (await result.Content.ReadFromJsonAsync<ExportReportDto>(Json))!;
        Assert.True(report.StrictlyVerified); Assert.True(report.IsVerified); Assert.True(report.HasAlpha); Assert.False(report.AudioIncluded);
        Assert.Equal(10, report.ExpectedFrames); Assert.Equal(10, report.FramesWritten); Assert.Equal(10, report.DecodedFrames);
        Assert.Equal(0, report.DroppedFrames); Assert.Equal(0, report.FailedFrames); Assert.Equal(5, report.FrameRateNumerator); Assert.Equal(1, report.FrameRateDenominator);
        using HttpResponseMessage content = await f.Http.GetAsync($"/api/v1/jobs/{job.JobId}/export-content");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        byte[] media = await content.Content.ReadAsByteArrayAsync();
        Assert.Equal(report.MediaSize, media.LongLength); Assert.Equal(report.Sha256, Convert.ToHexStringLower(SHA256.HashData(media)));
        using TemporaryDirectory directory = new();
        string path = directory.FilePath("workflow.mov"); await File.WriteAllBytesAsync(path, media);
        byte[] decoded = await WorkflowDecodeVideo(path);
        const int frameBytes = 320 * 180 * 4;
        Assert.Equal(10 * frameBytes, decoded.Length);
        // At 5/1 fps, complete decoded frame indices 3 and 7 are exactly 600000us and 1400000us.
        AssertWorkflowExportOracle(cueFrame.Png, decoded.AsSpan(3 * frameBytes, frameBytes));
        AssertWorkflowExportOracle(lineFrame.Png, decoded.AsSpan(7 * frameBytes, frameBytes));
    }

    private static async Task<byte[]> WorkflowDecodeVideo(string path)
    {
        ProcessStartInfo start = new(TestMediaToolPaths.Ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in new[] { "-v", "error", "-threads", "1", "-filter_threads", "1", "-i", path,
            "-map", "0:v:0", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1" }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using MemoryStream output = new();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
        Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await Task.WhenAll(copy, process.WaitForExitAsync(timeout.Token));
            Assert.True(process.ExitCode == 0, await errors);
            return output.ToArray();
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    private static void AssertWorkflowExportOracle(byte[] runtimePng, ReadOnlySpan<byte> rgba)
    {
        using SKBitmap runtime = SKBitmap.Decode(runtimePng);
        SKColor[] pixels = runtime.Pixels;
        Assert.Equal(pixels.Length * 4, rgba.Length);
        int glyph = 0, matchingAlpha = 0, unexpected = 0, opaque = 0, matchingColor = 0;
        for (int index = 0; index < pixels.Length; index++)
        {
            SKColor expected = pixels[index]; int offset = index * 4;
            if (expected.Alpha > 50)
            {
                glyph++;
                if (Math.Abs(expected.Alpha - rgba[offset + 3]) <= 8) matchingAlpha++;
            }
            else if (expected.Alpha == 0 && rgba[offset + 3] > 16) unexpected++;
            if (expected.Alpha > 240)
            {
                opaque++;
                if (Math.Abs(expected.Red - rgba[offset]) <= 24 && Math.Abs(expected.Green - rgba[offset + 1]) <= 24
                    && Math.Abs(expected.Blue - rgba[offset + 2]) <= 24) matchingColor++;
            }
        }
        Assert.True(glyph > 20); Assert.True(matchingAlpha >= glyph * .95, $"alpha content oracle {matchingAlpha}/{glyph}");
        Assert.True(unexpected < glyph * .1 + 20, $"unexpected ink {unexpected}/{glyph}");
        Assert.True(opaque > 20); Assert.True(matchingColor >= opaque * .9, $"RGB content oracle {matchingColor}/{opaque}");
    }

    private static async Task<string> WorkflowAddLine(Fixture f, NodeHttpHarness http, string text, string start, string end)
    {
        await http.Send(new AddLineCommandDto("primary", (await WorkflowLyrics(f, http)).Lines.LastOrDefault()?.Id));
        string id = (await WorkflowLyrics(f, http)).Lines.Last().Id;
        await http.Send(new SetLineTextCommandDto(new("primary", id), text));
        await http.Send(new ProjectCommandDto("lyrics.setTimeRange", new("primary", id), start, end));
        var line = (await WorkflowLyrics(f, http)).Lines.Single(item => item.Id == id);
        if (line.Tokens.Length > 0)
        {
            long startUs = long.Parse(start, CultureInfo.InvariantCulture), durationUs = long.Parse(end, CultureInfo.InvariantCulture) - startUs;
            await http.Send(new SetTokenTimesCommandDto(line.Tokens.Select((token, index) =>
                new TokenTimeChangeDto(new("primary", id, token.Id),
                    (startUs + durationUs * index / line.Tokens.Length).ToString(CultureInfo.InvariantCulture),
                    (startUs + durationUs * (index + 1) / line.Tokens.Length).ToString(CultureInfo.InvariantCulture))).ToArray()));
        }
        return id;
    }

    private static async Task<ProjectLyricsWindowResponse> WorkflowLyrics(Fixture f, NodeHttpHarness http) =>
        (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(http.Path + "/lyrics/window?branchId=primary&expectedRevision=" + http.Revision, Json))!;

    private static async Task<SceneMeasurementDataDto> WorkflowMeasure(Fixture f, NodeHttpHarness http, string mode, string? time)
    {
        using HttpResponseMessage response = await f.Http.PostAsJsonAsync(http.Path + "/orchestration/measurements",
            new SceneMeasurementRequest(http.Revision, mode, time, 320, 180, "transparent"), Json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        SceneMeasurementResponse result = (await response.Content.ReadFromJsonAsync<SceneMeasurementResponse>(Json))!;
        Assert.Equal(http.Revision, result.ProjectRevision);
        SceneMeasurementDataDto data = result.Measurement;
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data.Png)), data.PngSha256);
        Assert.Equal(mode, data.Mode); Assert.Equal(time, data.TimeUs); Assert.Equal(320, data.Width); Assert.Equal(180, data.Height);
        return data;
    }

    private static SceneMeasurementElementDto WorkflowLine(SceneMeasurementDataDto data, string id) =>
        Assert.Single(data.Elements, item => item.Kind == "line" && item.LineId == id);

    private static void AssertWorkflowPixelsEqual(byte[] expected, byte[] actual)
    {
        using SKBitmap reference = SKBitmap.Decode(expected); using SKBitmap measured = SKBitmap.Decode(actual);
        Assert.NotNull(reference); Assert.NotNull(measured); Assert.Equal(reference.Info.Size, measured.Info.Size);
        Assert.Equal(reference.Pixels, measured.Pixels);
    }

    private static void AssertWorkflowPixelsDiffer(byte[] before, byte[] after)
    {
        using SKBitmap reference = SKBitmap.Decode(before); using SKBitmap measured = SKBitmap.Decode(after);
        Assert.NotNull(reference); Assert.NotNull(measured); Assert.Equal(reference.Info.Size, measured.Info.Size);
        Assert.False(reference.Pixels.AsSpan().SequenceEqual(measured.Pixels));
    }

    private static void AssertWorkflowInkBounds(byte[] png, ProjectGeometryPointDto[] corners)
    {
        using SKBitmap bitmap = SKBitmap.Decode(png);
        SKColor[] pixels = bitmap.Pixels;
        int ink = 0, left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
            if (pixels[y * bitmap.Width + x].Alpha > 32)
            { ink++; left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        // This isolated sample has one text line on a transparent canvas. Decorated glyphs may extend outside shaping ink.
        Assert.True(ink > 20);
        Assert.True(left >= corners.Min(point => point.X) - 12 && right <= corners.Max(point => point.X) + 12);
        Assert.True(top >= corners.Min(point => point.Y) - 12 && bottom <= corners.Max(point => point.Y) + 12);
    }

    private static double WorkflowQuadArea(ProjectGeometryPointDto[] corners) => Math.Abs(
        (corners[1].X - corners[0].X) * (corners[3].Y - corners[0].Y) - (corners[1].Y - corners[0].Y) * (corners[3].X - corners[0].X));

    private static void AssertWorkflowQuad(ProjectGeometryPointDto[] corners)
    {
        Assert.Equal(4, corners.Length); Assert.All(corners, point => Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y)));
        Assert.True(WorkflowQuadArea(corners) > .01);
        Assert.Equal(corners[0].X + corners[2].X, corners[1].X + corners[3].X, 5);
        Assert.Equal(corners[0].Y + corners[2].Y, corners[1].Y + corners[3].Y, 5);
    }

    private static void AssertWorkflowReference(ProjectGeometryReferenceDto reference)
    {
        Assert.NotNull(reference); AssertWorkflowQuad(reference.WorldCorners);
        (double X, double Y)[] local = [(0, 0), (reference.Width, 0), (reference.Width, reference.Height), (0, reference.Height)];
        var matrix = reference.WorldMatrix;
        for (int index = 0; index < 4; index++)
        {
            Assert.Equal(matrix.A * local[index].X + matrix.C * local[index].Y + matrix.Tx, reference.WorldCorners[index].X, 5);
            Assert.Equal(matrix.B * local[index].X + matrix.D * local[index].Y + matrix.Ty, reference.WorldCorners[index].Y, 5);
        }
    }

    private static async Task<byte[]> WorkflowPreviewPng(Fixture f, string sessionId, string revision, string time)
    {
        using HttpResponseMessage created = await f.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            new PreviewSessionRequest(new("project", sessionId, revision), new(320, 180), PreviewAlphaMode.Straight, Metadata: f.Metadata()), Json);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        PreviewSessionResponse session = (await created.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        using HttpResponseMessage accepted = await f.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{session.PreviewId}/frame-requests",
            f.FrameRequest(session, 1, long.Parse(time, CultureInfo.InvariantCulture)), Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var ready = await f.WaitReady(session.PreviewId);
        using HttpResponseMessage content = await f.Http.GetAsync(ready.Session.LatestFrame!.Content.ContentUrl);
        byte[] png = await content.Content.ReadAsByteArrayAsync(); Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        using HttpResponseMessage closed = await f.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{session.PreviewId}/close",
            new ClosePreviewSessionRequest(session.PreviewGeneration, session.Source.SourceRevision, f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        return png;
    }

    private static async Task<string> WorkflowSaveReopen(Fixture f, NodeHttpHarness http)
    {
        using HttpResponseMessage handleResponse = await f.Http.PostAsJsonAsync("/api/v1/file-handles", new FileHandleRequest("preview-test-root", "no-audio", Metadata: f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Created, handleResponse.StatusCode);
        var handle = (await handleResponse.Content.ReadFromJsonAsync<FileHandleResponse>(Json))!;
        using (HttpResponseMessage saved = await f.Http.PostAsJsonAsync(http.Path + "/save-jobs", new SaveProjectRequest(http.Revision, handle.HandleId, f.Metadata()), Json))
            await NodeTerminal(f, saved);
        using (HttpResponseMessage closed = await f.Http.PostAsJsonAsync(http.Path + "/close", new CloseProjectRequest(http.Revision, ActiveJobs: "wait", Metadata: f.Metadata()), Json))
            await NodeTerminal(f, closed);
        using HttpResponseMessage opened = await f.Http.PostAsJsonAsync("/api/v1/project-sessions/open-jobs", new OpenProjectSessionRequest(handle.HandleId, Metadata: f.Metadata()), Json);
        return (await NodeTerminal(f, opened)).ProjectSessionId!;
    }
}
