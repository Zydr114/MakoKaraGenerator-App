using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class LeadInUpgradeRenderingTests
{
    private static Project CreateRubyProject(SceneAttachmentMode mode, ScenePlacement? linePlacement = null,
        NormalizedSafeArea? safeArea = null, CountdownShapeSpec? shape = null, CountdownAnimationSpec? animation = null)
    {
        var first = LyricLine.Create("first", "歌詞", new(11_500_000, 16_500_000), TimingStatus.Edited,
        [
            LyricToken.Create("first-a", "歌", new(12_000_000, 13_500_000), TimingStatus.Edited),
            LyricToken.Create("first-b", "詞", new(13_500_000, 16_000_000), TimingStatus.Edited),
        ]) with
        {
            RubyAnnotations = [RubyAnnotation.Create("first-ruby", "first-a", "か", relativeScale: 0.9, gapPx: 10)],
        };
        var second = LyricLine.Create("second", "第二行", new(17_000_000, 21_000_000), TimingStatus.Edited,
        [
            LyricToken.Create("second-a", "第", new(17_500_000, 19_000_000), TimingStatus.Edited),
            LyricToken.Create("second-b", "二行", new(19_000_000, 20_500_000), TimingStatus.Edited),
        ]);
        var project = Project.Create("前奏升级渲染测试",
            Composition.Create(640, 360, FrameRate.Fps30, RgbaColor.Black, 30_000_000),
            LyricsDocument.Create([first, second]),
            lyricStyle: LyricStyle.Default with
            {
                FontFamilyName = "Noto Sans CJK SC",
                FontSizePx = 36,
                UnsungColor = RgbaColor.White,
                SungColor = new(255, 0, 0, 255),
            });
        if (linePlacement is not null || safeArea is not null)
        {
            project = PresentationEditor.SetBranch(project, new()
            {
                BranchId = "primary",
                DefaultPlacement = linePlacement,
                SafeArea = safeArea ?? NormalizedSafeArea.Full,
            });
        }
        var program = project.LyricPresentation.FindProgram("primary")!;
        Assert.True(LeadInEditor.TryCreateCountdown(project, program.RootContainerId, "提示", 3, out string indicatorId, out project, out var report),
            report.ToSummary());
        var indicator = project.LyricPresentation.FindNode(indicatorId)!;
        var sequence = (CountdownSequenceContent)indicator.Content!;
        indicator = indicator with
        {
            Attachment = new() { Mode = mode },
            Placement = mode == SceneAttachmentMode.CueTargetAboveStart
                ? ScenePlacement.Create(new(0, -0.14), ScenePlacementAnchor.TopLeft)
                : ScenePlacement.Create(new(-0.02, 0), ScenePlacementAnchor.RightCenter),
            Content = sequence with
            {
                Shape = shape ?? sequence.Shape,
                Animation = animation ?? sequence.Animation,
            },
        };
        Assert.True(SceneTreeEditor.TrySetNode(project, indicator, out project, out report), report.ToSummary());
        Assert.True(LeadInEditor.TrySetLeadIn(project, program.Id,
            new() { Id = "cue-first", TargetLineId = "first", IndicatorContainerId = indicatorId, DurationUs = 3_000_000 },
            out project, out report), report.ToSummary());
        return project;
    }

    [Fact]
    public void 双锚点避让高注音且首字上方精准对齐起唱字左缘与注音顶端()
    {
        var textModeProject = CreateRubyProject(SceneAttachmentMode.CueTargetText);
        var aboveModeProject = CreateRubyProject(SceneAttachmentMode.CueTargetAboveStart);

        using SkiaRenderPipeline pipeline = new();
        var textScene = pipeline.GetSceneFrame(ProjectSnapshot.FromProject(textModeProject), 10_000_000);
        var aboveScene = pipeline.GetSceneFrame(ProjectSnapshot.FromProject(aboveModeProject), 10_000_000);

        var textLine = textScene.Lines.Single();
        var textGlyph = textLine.Geometry!;
        Assert.True(textGlyph.BaseTopLeft.HasValue && textGlyph.BaseBottomRight.HasValue);
        double baseCenterY = (textGlyph.EffectiveBaseTopLeft.Y + textGlyph.EffectiveBaseBottomRight.Y) / 2;
        double groupCenterY = (textGlyph.TopLeft.Y + textGlyph.BottomRight.Y) / 2;
        Assert.True(baseCenterY > groupCenterY + 3d);

        var textGroup = textModeProject.LyricPresentation.Nodes.Single(n => n.Content is CountdownSequenceContent);
        var textOwner = textScene.Elements.Single(e => e.Id == textGroup.Id);
        var expectedTextAnchor = SceneMatrix.Translate(textGlyph.EffectiveBaseTopLeft.X, baseCenterY);
        var expectedTextMatrix = textGlyph.WorldMatrix!.Value * expectedTextAnchor
            * SceneMatrix.Translate(textGroup.Placement.Position.X * 640, textGroup.Placement.Position.Y * 360)
            * SceneMatrix.Translate(-640 * textGroup.ReferenceRect.WidthRatio, -360 * textGroup.ReferenceRect.HeightRatio / 2);
        AssertMatrix(expectedTextMatrix, textOwner.WorldMatrix);

        var aboveLine = aboveScene.Lines.Single();
        var aboveGlyph = aboveLine.Geometry!;
        var aboveGroup = aboveModeProject.LyricPresentation.Nodes.Single(n => n.Content is CountdownSequenceContent);
        var aboveOwner = aboveScene.Elements.Single(e => e.Id == aboveGroup.Id);
        var expectedAboveAnchor = SceneMatrix.Translate(aboveGlyph.EffectiveBaseTopLeft.X, aboveGlyph.TopLeft.Y);
        var expectedAboveMatrix = aboveGlyph.WorldMatrix!.Value * expectedAboveAnchor
            * SceneMatrix.Translate(aboveGroup.Placement.Position.X * 640, aboveGroup.Placement.Position.Y * 360);
        AssertMatrix(expectedAboveMatrix, aboveOwner.WorldMatrix);
        Assert.True(aboveOwner.WorldCorners.Max(p => p.Y) < aboveGlyph.WorldMatrix.Value.Map(new(aboveGlyph.EffectiveBaseTopLeft.X, aboveGlyph.TopLeft.Y)).Y);
    }

    [Fact]
    public void 全绑定周期边界自动退让并保持Preparation到Performance零跳变且安全区内零偏移()
    {
        var safeArea = new NormalizedSafeArea { Left = 0.05, Top = 0.08, Right = 0.95, Bottom = 0.92 };
        var leftEdge = CreateRubyProject(SceneAttachmentMode.CueTargetText,
            ScenePlacement.Create(new(0.06, 0.5), ScenePlacementAnchor.LeftCenter), safeArea);
        var topEdge = CreateRubyProject(SceneAttachmentMode.CueTargetAboveStart,
            ScenePlacement.Create(new(0.5, 0.09), ScenePlacementAnchor.TopCenter), safeArea);
        var centered = CreateRubyProject(SceneAttachmentMode.CueTargetText,
            ScenePlacement.Create(new(0.5, 0.5), ScenePlacementAnchor.Center), safeArea);
        var centeredFixed = centered with
        {
            LyricPresentation = centered.LyricPresentation with
            {
                Nodes = [.. centered.LyricPresentation.Nodes.Select(n => n.Content is CountdownSequenceContent
                    ? n with { Attachment = new() { Mode = SceneAttachmentMode.FixedParent } } : n)],
            },
        };

        using SkiaRenderPipeline pipeline = new();

        var leftFixed = leftEdge with
        {
            LyricPresentation = leftEdge.LyricPresentation with
            {
                Nodes = [.. leftEdge.LyricPresentation.Nodes.Select(n => n.Content is CountdownSequenceContent
                    ? n with { Attachment = new() { Mode = SceneAttachmentMode.FixedParent } } : n)],
            },
        };
        var leftPlain = pipeline.GetSceneFrame(ProjectSnapshot.FromProject(leftFixed), 10_000_000);
        var leftPlainGlyph = leftPlain.Lines.Single().Geometry!;
        double leftOrigStartX = leftPlainGlyph.WorldMatrix!.Value.Map(new(leftPlainGlyph.EffectiveBaseTopLeft.X,
            (leftPlainGlyph.EffectiveBaseTopLeft.Y + leftPlainGlyph.EffectiveBaseBottomRight.Y) / 2d)).X;
        var leftSnap = ProjectSnapshot.FromProject(leftEdge);
        var leftPrep = pipeline.GetSceneFrame(leftSnap, 11_990_000);
        var leftSing = pipeline.GetSceneFrame(leftSnap, 12_000_000);
        double leftMarksMinX = leftPrep.TextMarks.SelectMany(m => m.Geometry.Corners()).Min(p => p.X);
        Assert.Equal(leftOrigStartX, FirstMarkCellMinX(leftEdge, leftPrep), 0.5);
        Assert.True(leftMarksMinX >= leftOrigStartX - 0.5);
        Assert.True(leftMarksMinX >= safeArea.Left * 640 - 0.5);
        Assert.Equal(leftPrep.Lines.Single().Geometry!.OriginX, leftSing.Lines.Single().Geometry!.OriginX, 6);
        Assert.Equal(leftPrep.Lines.Single().Geometry!.BaselineY, leftSing.Lines.Single().Geometry!.BaselineY, 6);
        AssertMatrix(leftPrep.Lines.Single().Geometry!.WorldMatrix!.Value, leftSing.Lines.Single().Geometry!.WorldMatrix!.Value);

        var topSnap = ProjectSnapshot.FromProject(topEdge);
        var topPrep = pipeline.GetSceneFrame(topSnap, 11_990_000);
        var topSing = pipeline.GetSceneFrame(topSnap, 12_000_000);
        var topGroupId = topEdge.LyricPresentation.Nodes.Single(n => n.Content is CountdownSequenceContent).Id;
        var topOwner = topPrep.Elements.Single(e => e.Id == topGroupId);
        Assert.True(topOwner.WorldCorners.Min(p => p.Y) >= safeArea.Top * 360 - 0.5);
        Assert.Equal(topPrep.Lines.Single().Geometry!.OriginX, topSing.Lines.Single().Geometry!.OriginX, 6);
        Assert.Equal(topPrep.Lines.Single().Geometry!.BaselineY, topSing.Lines.Single().Geometry!.BaselineY, 6);
        AssertMatrix(topPrep.Lines.Single().Geometry!.WorldMatrix!.Value, topSing.Lines.Single().Geometry!.WorldMatrix!.Value);

        var centeredSnap = ProjectSnapshot.FromProject(centered);
        var centeredPrep = pipeline.GetSceneFrame(centeredSnap, 11_990_000);
        var centeredSing = pipeline.GetSceneFrame(centeredSnap, 12_000_000);
        var centeredPlain = pipeline.GetSceneFrame(ProjectSnapshot.FromProject(centeredFixed), 10_000_000);
        var centeredPlainGlyph = centeredPlain.Lines.Single().Geometry!;
        double centeredOrigStartX = centeredPlainGlyph.WorldMatrix!.Value.Map(new(centeredPlainGlyph.EffectiveBaseTopLeft.X,
            (centeredPlainGlyph.EffectiveBaseTopLeft.Y + centeredPlainGlyph.EffectiveBaseBottomRight.Y) / 2d)).X;
        double centeredMarksMinX = centeredPrep.TextMarks.SelectMany(m => m.Geometry.Corners()).Min(p => p.X);
        Assert.Equal(centeredOrigStartX, FirstMarkCellMinX(centered, centeredPrep), 0.5);
        Assert.True(centeredMarksMinX >= centeredOrigStartX - 0.5);
        Assert.True(centeredPrep.Lines.Single().Geometry!.Pivot.X > centeredPlainGlyph.Pivot.X + 120d);
        AssertMatrix(centeredPrep.Lines.Single().Geometry!.WorldMatrix!.Value, centeredSing.Lines.Single().Geometry!.WorldMatrix!.Value);
    }

    [Fact]
    public void 删减至单标记与缩放及隐藏标记均按实际可见标记宽度退让且全部透明时不退让()
    {
        var baseProject = CreateRubyProject(SceneAttachmentMode.CueTargetText,
            ScenePlacement.Create(new(0.5, 0.5), ScenePlacementAnchor.Center));
        var group = baseProject.LyricPresentation.Nodes.Single(n => n.Content is CountdownSequenceContent);
        var sequence = (CountdownSequenceContent)group.Content!;
        var unattachedProject = baseProject with
        {
            LyricPresentation = baseProject.LyricPresentation with
            {
                Nodes = baseProject.LyricPresentation.Nodes.Replace(group, group with { Attachment = new() { Mode = SceneAttachmentMode.FixedParent } }),
            },
        };
        using SkiaRenderPipeline pipeline = new();
        var plainGlyph = pipeline.GetSceneFrame(ProjectSnapshot.FromProject(unattachedProject), 10_000_000).Lines.Single().Geometry!;
        double origStartX = plainGlyph.WorldMatrix!.Value.Map(new(plainGlyph.EffectiveBaseTopLeft.X,
            (plainGlyph.EffectiveBaseTopLeft.Y + plainGlyph.EffectiveBaseBottomRight.Y) / 2d)).X;

        // 1. 删除后两个标记仅留 1 个，并放大容器 Scale 至 1.8，应仅退让单个标记放大后宽度 + 默认间距。
        Assert.True(SceneTreeEditor.TryDeleteNode(baseProject, sequence.MarkNodeIds[2], out var shrunkProject, out var r1), r1.ToSummary());
        Assert.True(SceneTreeEditor.TryDeleteNode(shrunkProject, sequence.MarkNodeIds[1], out shrunkProject, out var r2), r2.ToSummary());
        var shrunkGroup = shrunkProject.LyricPresentation.FindNode(group.Id)!;
        double gapRatio = LeadInEditor.DefaultInlineGapPx / 640d;
        Assert.True(SceneTreeEditor.TrySetNode(shrunkProject, shrunkGroup with
        {
            Placement = shrunkGroup.Placement with { Position = new(-gapRatio, 0d), Anchor = ScenePlacementAnchor.RightCenter, Scale = 1.8d },
        }, out shrunkProject, out var r3), r3.ToSummary());
        var shrunkFrame = pipeline.GetSceneFrame(ProjectSnapshot.FromProject(shrunkProject), 10_000_000);
        double singleMarkMinX = shrunkFrame.TextMarks.Single().Geometry.Corners().Min(p => p.X);
        Assert.Equal(origStartX, FirstMarkCellMinX(shrunkProject, shrunkFrame), 0.5);
        Assert.True(singleMarkMinX >= origStartX - 0.5);
        double expectedShift = LeadInEditor.MarkCellSizePx * 1.8d + LeadInEditor.DefaultInlineGapPx;
        Assert.Equal(expectedShift, shrunkFrame.Lines.Single().Geometry!.Pivot.X - plainGlyph.Pivot.X, 1.0);

        // 2. 隐藏前 2 个标记（Opacity = 0），仅按紧邻歌词的第 3 个可见标记退让。
        var treeWithHidden = baseProject.LyricPresentation;
        for (int i = 0; i < 2; i++)
        {
            var m = treeWithHidden.FindNode(sequence.MarkNodeIds[i])!;
            treeWithHidden = treeWithHidden with
            {
                Nodes = treeWithHidden.Nodes.Replace(m, m with
                {
                    Content = ((TextMarkContent)m.Content!) with { AppearanceOverride = new() { Opacity = 0f } },
                }),
            };
        }
        var hiddenGroup = treeWithHidden.FindNode(group.Id)!;
        var treeWithHiddenPlaced = treeWithHidden with
        {
            Nodes = treeWithHidden.Nodes.Replace(hiddenGroup, hiddenGroup with
            {
                Placement = hiddenGroup.Placement with { Position = new(-gapRatio, 0d), Anchor = ScenePlacementAnchor.RightCenter, Scale = 1d },
            }),
        };
        var partialHiddenFrame = pipeline.GetSceneFrame(ProjectSnapshot.FromProject(baseProject with { LyricPresentation = treeWithHiddenPlaced }), 10_000_000);
        double firstVisibleShift = partialHiddenFrame.Lines.Single().Geometry!.Pivot.X - plainGlyph.Pivot.X;
        Assert.Equal(LeadInEditor.MarkCellSizePx + LeadInEditor.DefaultInlineGapPx, firstVisibleShift, 1.0);

        // 3. 全部标记 Opacity = 0 时不产生退让。
        var mLast = treeWithHiddenPlaced.FindNode(sequence.MarkNodeIds[2])!;
        var allHiddenTree = treeWithHiddenPlaced with
        {
            Nodes = treeWithHiddenPlaced.Nodes.Replace(mLast, mLast with
            {
                Content = ((TextMarkContent)mLast.Content!) with { AppearanceOverride = new() { Opacity = 0f } },
            }),
        };
        var allHiddenFrame = pipeline.GetSceneFrame(ProjectSnapshot.FromProject(baseProject with { LyricPresentation = allHiddenTree }), 10_000_000);
        AssertMatrix(plainGlyph.WorldMatrix!.Value, allHiddenFrame.Lines.Single().Geometry!.WorldMatrix!.Value);
    }

    [Fact]
    public void TrySetAttachment切换到首字上方时渲染标记整体位于注音上方且左缘对齐起唱字()
    {
        var project = CreateRubyProject(SceneAttachmentMode.CueTargetText);
        var program = project.LyricPresentation.FindProgram("primary")!;
        string indicatorId = program.LeadIns.Single().IndicatorContainerId;
        var fixedNodes = SceneTreeGeometryResolver.Resolve(project.LyricPresentation.Nodes, 640, 360);
        using SkiaRenderPipeline pipeline = new();
        var initialFrame = pipeline.GetSceneFrame(ProjectSnapshot.FromProject(project), 10_000_000);
        var refs = SceneAttachmentReferences.Resolve(ProjectSnapshot.FromProject(project), initialFrame, includeFixed: true, SceneAttachmentMode.CueTargetAboveStart);
        Assert.True(LeadInEditor.TrySetAttachment(project, indicatorId, SceneAttachmentMode.CueTargetAboveStart,
            fixedNodes[indicatorId], refs[indicatorId], out var aboveProject, out var report), report.ToSummary());

        var aboveFrame = pipeline.GetSceneFrame(ProjectSnapshot.FromProject(aboveProject), 10_000_000);
        var targetGlyph = aboveFrame.Lines.Single().Geometry!;
        var targetWorld = targetGlyph.WorldMatrix!.Value;
        double expectedLeftX = targetWorld.Map(new(targetGlyph.EffectiveBaseTopLeft.X, targetGlyph.TopLeft.Y)).X;
        double rubyTopY = targetWorld.Map(new(targetGlyph.EffectiveBaseTopLeft.X, targetGlyph.TopLeft.Y)).Y;
        var markCorners = aboveFrame.TextMarks.SelectMany(m => m.Geometry.Corners()).ToArray();
        Assert.Equal(expectedLeftX, FirstMarkCellMinX(aboveProject, aboveFrame), 0.5);
        Assert.True(markCorners.Min(p => p.X) >= expectedLeftX - 0.5);
        Assert.True(markCorners.Max(p => p.Y) < rubyTopY);
    }

    [Theory]
    [InlineData(CountdownShapeKind.Circle)]
    [InlineData(CountdownShapeKind.SteppedBars)]
    public void BlinkThenWipeFill具备余弦闪烁与左至右渐进双态填充及多层描边阴影辉光(CountdownShapeKind kind)
    {
        var project = CreateRubyProject(SceneAttachmentMode.FixedParent,
            shape: new CountdownShapeSpec { Kind = kind, BarMinHeightRatio = 0.35 },
            animation: new CountdownAnimationSpec { Mode = CountdownAnimationMode.BlinkThenWipeFill, BlinkRatio = 0.35, BlinkCount = 2, FeatherRatio = 0.06 });
        var tree = project.LyricPresentation;
        var group = tree.Nodes.Single(n => n.Content is CountdownSequenceContent);
        var sequence = (CountdownSequenceContent)group.Content!;
        var decorated = sequence.Appearance with
        {
            Pending = new()
            {
                Fill = new() { Color = new(0, 0, 255, 255) },
                Strokes = [StrokeSpec.Create(new(0, 255, 0, 255), 2f), StrokeSpec.Create(new(255, 255, 0, 255), 2f)],
                Shadow = ShadowSpec.Create(new(0, 200, 200, 255), 1f, 90f, 10f, 2f),
                Glow = GlowSpec.Create(new(255, 0, 255, 255), 1f, 6f, 2f),
            },
            Elapsed = SungAppearance.Following(new() { Color = new(255, 0, 0, 255) }),
        };
        group = group with
        {
            Placement = ScenePlacement.Create(new(0.5, 0.5)),
            ReferenceRect = new(0.6, 0.25),
            Content = sequence with { Appearance = decorated },
        };
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree with { Nodes = tree.Nodes.Replace(tree.FindNode(group.Id)!, group) } });

        using SkiaRenderPipeline pipeline = new();
        // Range is [9_000_000, 12_000_000). Blink start t = 9_000_000 (u = 0 -> blinkOpacity = 1.0); dip at phase = 0.5 cycles -> u = 0.35 / 4 = 0.0875 -> t = 9_262_500
        long startUs = 9_000_000;
        long dipUs = 9_000_000 + (long)(0.0875 * 3_000_000);
        var startScene = pipeline.GetSceneFrame(snapshot, startUs);
        var dipScene = pipeline.GetSceneFrame(snapshot, dipUs);
        Assert.Equal(1d, startScene.LeadIns[0].BlinkOpacity, 3);
        Assert.InRange(dipScene.LeadIns[0].BlinkOpacity, 0.24, 0.26);

        using var startFrame = pipeline.Render(RenderRequest.At(snapshot, startUs) with { TransparentBackground = true });
        using var dipFrame = pipeline.Render(RenderRequest.At(snapshot, dipUs) with { TransparentBackground = true });
        Assert.True(MaxAlphaInBox(dipFrame, dipScene.TextMarks[0].Geometry) < MaxAlphaInBox(startFrame, startScene.TextMarks[0].Geometry) / 2);

        if (kind == CountdownShapeKind.SteppedBars)
        {
            double h0 = startScene.TextMarks[0].Geometry.BottomRight.Y - startScene.TextMarks[0].Geometry.TopLeft.Y;
            double h1 = startScene.TextMarks[1].Geometry.BottomRight.Y - startScene.TextMarks[1].Geometry.TopLeft.Y;
            double h2 = startScene.TextMarks[2].Geometry.BottomRight.Y - startScene.TextMarks[2].Geometry.TopLeft.Y;
            Assert.True(h0 > h1 && h1 > h2);
        }

        // Mid-wipe of mark 0: u = 0.35 + 0.65 * (0.5 / 3.0) = 0.4583333 -> t = 9_000_000 + 1_375_000 = 10_375_000
        long midWipeUs = 10_375_000;
        var midScene = pipeline.GetSceneFrame(snapshot, midWipeUs);
        Assert.InRange(midScene.TextMarks[0].WipeProgress, 0.45, 0.55);
        Assert.Equal(0d, midScene.TextMarks[1].WipeProgress, 4);

        using var midFrame = pipeline.Render(RenderRequest.At(snapshot, midWipeUs) with { TransparentBackground = true });
        var mark0 = midScene.TextMarks[0].Geometry;
        var mark1 = midScene.TextMarks[1].Geometry;
        int elapsedLeftRed = CountColorInHalf(midFrame, mark0, leftHalf: true, isRed: true);
        int pendingRightBlue = CountColorInHalf(midFrame, mark0, leftHalf: false, isRed: false);
        Assert.True(elapsedLeftRed > 20, $"Expected red elapsed pixels on left half of mark0, got {elapsedLeftRed}");
        Assert.True(pendingRightBlue > 20, $"Expected blue pending pixels on right half of mark0, got {pendingRightBlue}");
        Assert.True(CountColorInHalf(midFrame, mark1, leftHalf: true, isRed: false) > 20);
        // Verify multi-stroke green/yellow pixels are also drawn around mark0.
        Assert.True(CountGreenOrYellowStrokePixels(midFrame, mark0) > 10);
    }

    [Fact]
    public void BlinkThenErase先闪烁再从左至右渐进擦除消失()
    {
        var project = CreateRubyProject(SceneAttachmentMode.FixedParent,
            shape: new CountdownShapeSpec { Kind = CountdownShapeKind.Capsule },
            animation: new CountdownAnimationSpec { Mode = CountdownAnimationMode.BlinkThenErase, BlinkRatio = 0.2, BlinkCount = 1, FeatherRatio = 0.04 });
        var tree = project.LyricPresentation;
        var group = tree.Nodes.Single(n => n.Content is CountdownSequenceContent);
        var sequence = (CountdownSequenceContent)group.Content!;
        group = group with
        {
            Placement = ScenePlacement.Create(new(0.5, 0.5)),
            ReferenceRect = new(0.6, 0.22),
            Content = sequence with
            {
                Appearance = SceneTextAppearance.Default with
                {
                    PendingFill = new() { Color = new(0, 0, 255, 255) },
                    ElapsedFill = new() { Color = new(255, 0, 0, 255) },
                },
            },
        };
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree with { Nodes = tree.Nodes.Replace(tree.FindNode(group.Id)!, group) } });

        using SkiaRenderPipeline pipeline = new();
        // u = 0.2 + 0.8 * (1.5 / 3.0) = 0.6 -> t = 9_000_000 + 1_800_000 = 10_800_000
        // Mark 0: WipeProgress = 1.0 (erased); Mark 1: WipeProgress = 0.5 (left half erased, right half blue); Mark 2: WipeProgress = 0 (all blue).
        var scene = pipeline.GetSceneFrame(snapshot, 10_800_000);
        Assert.Equal(1d, scene.TextMarks[0].WipeProgress, 4);
        Assert.InRange(scene.TextMarks[1].WipeProgress, 0.45, 0.55);
        Assert.Equal(0d, scene.TextMarks[2].WipeProgress, 4);

        using var frame = pipeline.Render(RenderRequest.At(snapshot, 10_800_000) with { TransparentBackground = true });
        Assert.Equal(0, MaxAlphaInBox(frame, scene.TextMarks[0].Geometry));
        Assert.Equal(0, CountColorInHalf(frame, scene.TextMarks[1].Geometry, leftHalf: true, isRed: false));
        Assert.True(CountColorInHalf(frame, scene.TextMarks[1].Geometry, leftHalf: false, isRed: false) > 20);
        Assert.True(CountColorInHalf(frame, scene.TextMarks[2].Geometry, leftHalf: true, isRed: false) > 20);
    }

    [Fact]
    public void CustomSvg同时支持剪影着色包含currentColor描边以及原色保留并参与渐进擦除()
    {
        const string StrokeSvg = "<svg viewBox=\"0 0 100 100\"><circle cx=\"50\" cy=\"50\" r=\"36\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"16\"/></svg>";
        const string ColorSvg = "<svg viewBox=\"0 0 100 100\"><rect x=\"4\" y=\"4\" width=\"92\" height=\"92\" fill=\"#00ff00\"/><rect x=\"30\" y=\"30\" width=\"40\" height=\"40\" fill=\"#ff0000\"/></svg>";

        var silhouetteProject = CreateRubyProject(SceneAttachmentMode.FixedParent,
            shape: new CountdownShapeSpec { Kind = CountdownShapeKind.CustomSvg, SvgXml = StrokeSvg, SvgColorMode = CountdownSvgColorMode.SilhouetteStyle },
            animation: new CountdownAnimationSpec { Mode = CountdownAnimationMode.BlinkThenWipeFill, BlinkRatio = 0, BlinkCount = 0, FeatherRatio = 0.02 });
        var tree1 = silhouetteProject.LyricPresentation;
        var g1 = tree1.Nodes.Single(n => n.Content is CountdownSequenceContent);
        g1 = g1 with
        {
            Placement = ScenePlacement.Create(new(0.5, 0.5)),
            ReferenceRect = new(0.6, 0.25),
            Content = ((CountdownSequenceContent)g1.Content!) with
            {
                Appearance = SceneTextAppearance.Default with
                {
                    PendingFill = new() { Color = new(0, 0, 255, 255) },
                    ElapsedFill = new() { Color = new(255, 0, 0, 255) },
                },
            },
        };
        var s1 = ProjectSnapshot.FromProject(silhouetteProject with { LyricPresentation = tree1 with { Nodes = tree1.Nodes.Replace(tree1.FindNode(g1.Id)!, g1) } });

        var originalProject = CreateRubyProject(SceneAttachmentMode.FixedParent,
            shape: new CountdownShapeSpec { Kind = CountdownShapeKind.CustomSvg, SvgXml = ColorSvg, SvgColorMode = CountdownSvgColorMode.OriginalColor },
            animation: new CountdownAnimationSpec { Mode = CountdownAnimationMode.BlinkThenWipeFill, BlinkRatio = 0, BlinkCount = 0, FeatherRatio = 0.02 });
        var tree2 = originalProject.LyricPresentation;
        var g2 = tree2.Nodes.Single(n => n.Content is CountdownSequenceContent);
        g2 = g2 with { Placement = ScenePlacement.Create(new(0.5, 0.5)), ReferenceRect = new(0.6, 0.25) };
        var s2 = ProjectSnapshot.FromProject(originalProject with { LyricPresentation = tree2 with { Nodes = tree2.Nodes.Replace(tree2.FindNode(g2.Id)!, g2) } });

        using SkiaRenderPipeline pipeline = new();
        // Mark 0 mid-wipe at t = 9_500_000 (u = 0.5 / 3 = 1/6)
        var scene1 = pipeline.GetSceneFrame(s1, 9_500_000);
        using var frame1 = pipeline.Render(RenderRequest.At(s1, 9_500_000) with { TransparentBackground = true });
        Assert.True(CountColorInHalf(frame1, scene1.TextMarks[0].Geometry, leftHalf: true, isRed: true) > 10);
        Assert.True(CountColorInHalf(frame1, scene1.TextMarks[0].Geometry, leftHalf: false, isRed: false) > 10);

        // At t = 10_500_000 (u = 0.5), mark 0 is elapsed (full alpha original colors), mark 2 is pending (dimmed original colors)
        var scene2 = pipeline.GetSceneFrame(s2, 10_500_000);
        using var frame2 = pipeline.Render(RenderRequest.At(s2, 10_500_000) with { TransparentBackground = true });
        var (greenCount0, redCount0) = CountGreenAndRed(frame2, scene2.TextMarks[0].Geometry);
        Assert.True(greenCount0 > 30 && redCount0 > 30, $"Expected green and red original SVG colors in mark0, got G={greenCount0}, R={redCount0}");
        Assert.True(MaxAlphaInBox(frame2, scene2.TextMarks[0].Geometry) > 240);
        Assert.InRange(MaxAlphaInBox(frame2, scene2.TextMarks[2].Geometry), 85, 110);
    }

    private static void AssertMatrix(SceneMatrix a, SceneMatrix b)
    {
        Assert.Equal(a.A, b.A, 6);
        Assert.Equal(a.B, b.B, 6);
        Assert.Equal(a.C, b.C, 6);
        Assert.Equal(a.D, b.D, 6);
        Assert.Equal(a.Tx, b.Tx, 6);
        Assert.Equal(a.Ty, b.Ty, 6);
    }

    private static int MaxAlphaInBox(RenderFrame frame, LineRenderGeometry geometry)
    {
        var pixels = frame.PixelMemory.Span;
        int max = 0;
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
                if (geometry.Contains(new(x + 0.5, y + 0.5)))
                    max = Math.Max(max, pixels[y * frame.Stride + x * 4 + 3]);
        return max;
    }

    private static int CountColorInHalf(RenderFrame frame, LineRenderGeometry geometry, bool leftHalf, bool isRed)
    {
        var pixels = frame.PixelMemory.Span;
        double midX = (geometry.TopLeft.X + geometry.BottomRight.X) / 2d;
        int count = 0;
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                if (!geometry.Contains(new(x + 0.5, y + 0.5))) continue;
                var local = geometry.WorldMatrix!.Value.Unmap(new(x + 0.5, y + 0.5));
                if (leftHalf ? local.X >= midX - 2d : local.X <= midX + 2d) continue;
                var px = pixels.Slice(y * frame.Stride + x * 4, 4);
                if (px[3] <= 180) continue;
                if (isRed ? px[2] > px[0] * 2 && px[2] > px[1] * 2 : px[0] > px[2] * 2 && px[0] > px[1] * 2)
                    count++;
            }
        }
        return count;
    }

    private static int CountGreenOrYellowStrokePixels(RenderFrame frame, LineRenderGeometry geometry)
    {
        var pixels = frame.PixelMemory.Span;
        int count = 0;
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                if (!geometry.Contains(new(x + 0.5, y + 0.5), padding: 8d)) continue;
                var px = pixels.Slice(y * frame.Stride + x * 4, 4);
                if (px[3] > 180 && px[1] > 150 && px[0] < 80)
                    count++;
            }
        }
        return count;
    }

    private static (int Green, int Red) CountGreenAndRed(RenderFrame frame, LineRenderGeometry geometry)
    {
        var pixels = frame.PixelMemory.Span;
        int green = 0, red = 0;
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                if (!geometry.Contains(new(x + 0.5, y + 0.5))) continue;
                var px = pixels.Slice(y * frame.Stride + x * 4, 4);
                if (px[3] <= 70) continue;
                if (px[1] > px[0] * 2 && px[1] > px[2] * 2) green++;
                if (px[2] > px[0] * 2 && px[2] > px[1] * 2) red++;
            }
        }
        return (green, red);
    }

    private static double FirstMarkCellMinX(Project project, SceneFrame frame)
    {
        var snap = ProjectSnapshot.FromProject(project);
        var refs = SceneAttachmentReferences.Resolve(snap, frame);
        var nodes = SceneTreeGeometryResolver.Resolve(project.LyricPresentation.Nodes, 640, 360, refs);
        var seq = (CountdownSequenceContent)project.LyricPresentation.Nodes.Single(n => n.Content is CountdownSequenceContent).Content!;
        var g = nodes[seq.MarkNodeIds[0]];
        CanvasPoint[] corners =
        [
            g.WorldMatrix.Map(new(0, 0)),
            g.WorldMatrix.Map(new(g.Width, 0)),
            g.WorldMatrix.Map(new(g.Width, g.Height)),
            g.WorldMatrix.Map(new(0, g.Height)),
        ];
        return corners.Min(p => p.X);
    }
}
