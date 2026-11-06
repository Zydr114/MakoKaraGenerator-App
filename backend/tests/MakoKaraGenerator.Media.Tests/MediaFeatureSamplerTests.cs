using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Abstractions;
using Xunit;
using static MakoKaraGenerator.Media.Tests.MediaFeatureSamplerFixture;

namespace MakoKaraGenerator.Media.Tests;

public sealed class MediaFeatureSamplerTests
{
    [Fact]
    public async Task FormalBuilderCodecAndSynchronousProviderMatchIndependentSevenMomentOracle()
    {
        Project project = Project(); ProjectMediaDependencyPlan plan = ProjectMediaDependencyResolver.Resolve(project);
        MediaFeatureFileSource source = Source(); float[][] pixels = [Pixels(), Pixels(1), Pixels(2)];
        var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes);
        await using var opened = await BuildAndOpenAsync(source, pixels, budget);
        foreach (BackgroundPlaybackMode playback in new[] { BackgroundPlaybackMode.Loop, BackgroundPlaybackMode.FreezeLastFrame })
        {
            MediaMapping mapping = Mapping(playback: playback);
            var input = MediaFeatureInput.CreateAvailableFile(Context(plan, source, mapping), source, opened.Artifact);
            using var sampler = new MediaFeatureSampler(input, plan, opened.Reader, budget);
            foreach (long time in new long[] { 0, 1_000, 3_000, 9_000 }) foreach (int window in new[] { 0, 1 })
            {
                var sourceRect = new MediaFeatureQueryRect(.125, .25, .75, .875);
                var canvasRect = new MediaFeatureQueryRect(19.5, 2.25, 207.75, 174.5);
                double[][] grid = MediaFeatureSamplerOracle.Grid(pixels, time, window == 0 ? 0 : 2_000, playback);
                var sourceExpected = MediaFeatureSamplerOracle.Rect(grid, sourceRect, 0, 0, 1, 1, default);
                var canvasExpected = MediaFeatureSamplerOracle.Rect(grid, canvasRect, 0, 30, 320, 180, mapping.BackColor);
                ProjectMediaFeatureSample a = sampler.Sample(Dependency(plan, "source-" + window), sourceRect, time, default);
                ProjectMediaFeatureSample b = sampler.Sample(Dependency(plan, "canvas-" + window), canvasRect, time, default);
                MediaFeatureSamplerOracle.Source(sourceExpected, a); MediaFeatureSamplerOracle.Canvas(canvasExpected, b);
                Assert.Same(input, a.Input); Assert.Same(Dependency(plan, "source-" + window), a.Dependency);
            }
        }
        Assert.True(budget.LiveBytes > 0); // Only the borrowed formal reader remains live after providers drain.
        await opened.DisposeAsync(); Assert.Equal(0, budget.LiveBytes);
    }

    [Theory]
    [InlineData(BackgroundFit.Contain, 320, 240, 0, 30, 320, 180)]
    [InlineData(BackgroundFit.Cover, 240, 240, -93.33333333333333, 0, 426.6666666666667, 240)]
    [InlineData(BackgroundFit.Original, 80, 60, 24, 21, 32, 18)]
    public async Task FormalStillHonorsFitCropLetterboxAndFixedBottom(BackgroundFit fit, int width, int height,
        double x, double y, double fullWidth, double fullHeight)
    {
        ProjectMediaDependencyPlan plan = ProjectMediaDependencyResolver.Resolve(Project()); MediaFeatureFileSource source = Source(MediaSourceKind.Still);
        float[][] pixels = [Pixels()]; var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes);
        await using var opened = await BuildAndOpenAsync(source, pixels, budget);
        var mapping = MediaMapping.Create(width, height, fit, BackgroundPlaybackMode.Loop, new(.125, .0625, 0, .5), MediaOutputMode.Normal);
        using var sampler = new MediaFeatureSampler(MediaFeatureInput.CreateAvailableFile(Context(plan, source, mapping), source, opened.Artifact), plan, opened.Reader, budget);
        var rect = new MediaFeatureQueryRect(0, 0, width, height);
        double[][] grid = MediaFeatureSamplerOracle.Grid(pixels, 0, 0, BackgroundPlaybackMode.Loop);
        var expected = MediaFeatureSamplerOracle.Rect(grid, rect, x, y, fullWidth, fullHeight, mapping.BackColor);
        MediaFeatureSamplerOracle.Canvas(expected, sampler.Sample(Dependency(plan, "canvas-0"), rect, 0, default));
        MediaFeatureSamplerOracle.Canvas(expected, sampler.Sample(Dependency(plan, "canvas-1"), rect, long.MaxValue, default));
    }

    [Fact]
    public async Task FormalRotatedNonSquareSarReaderKeepsCanonicalPixelsAndDoubleGeometry()
    {
        ProjectMediaDependencyPlan plan = ProjectMediaDependencyResolver.Resolve(Project());
        MediaFeatureFileSource source = Source(MediaSourceKind.Still, 18, 32, 2, 1, MediaQuarterTurn.Clockwise90, true);
        float[][] pixels = [Pixels()]; var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes);
        await using var opened = await BuildAndOpenAsync(source, pixels, budget);
        var mapping = Mapping();
        using var sampler = new MediaFeatureSampler(MediaFeatureInput.CreateAvailableFile(Context(plan, source, mapping), source, opened.Artifact), plan, opened.Reader, budget);
        var rect = new MediaFeatureQueryRect(17.25, 22.75, 277.5, 233.5);
        var expected = MediaFeatureSamplerOracle.Rect(MediaFeatureSamplerOracle.Grid(pixels, 0, 0, BackgroundPlaybackMode.Loop),
            rect, 53.33333333333333, 0, 213.33333333333334, 240, mapping.BackColor);
        MediaFeatureSamplerOracle.Canvas(expected, sampler.Sample(Dependency(plan, "canvas-0"), rect, 0, default));
    }

    [Fact]
    public async Task TinyDoubleEndpointsAndPooledVarianceUseTheFormalTrackWithoutFloatNarrowing()
    {
        ProjectMediaDependencyPlan plan = ProjectMediaDependencyResolver.Resolve(Project()); MediaFeatureFileSource source = Source();
        float[] black = Enumerable.Range(0, 576).SelectMany(_ => new float[] { 0, 0, 0, 1 }).ToArray();
        float[] white = Enumerable.Repeat(1f, 576 * 4).ToArray(); float[][] pixels = [black, white];
        var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes);
        await using var opened = await BuildAndOpenAsync(source, pixels, budget);
        var mapping = MediaMapping.Create(8, 8, BackgroundFit.Original, BackgroundPlaybackMode.Loop, default, MediaOutputMode.Normal);
        using var sampler = new MediaFeatureSampler(MediaFeatureInput.CreateAvailableFile(Context(plan, source, mapping), source, opened.Artifact), plan, opened.Reader, budget);
        double tiny = Math.ScaleB(1d, -30); var rect = new MediaFeatureQueryRect(0, 0, tiny, tiny);
        ProjectCanvasMediaFeatureValues result = sampler.Sample(Dependency(plan, "canvas-1"), rect, 3_000, default).CanvasValues!;
        MediaFeatureSamplerOracle.Near(.5, result.Mean.R); MediaFeatureSamplerOracle.Near(.25, result.PremulYVariance);
        Assert.Equal(tiny, rect.Right);
        var sourceRect = new MediaFeatureQueryRect(.3, .4, .3 + tiny, .4 + tiny);
        Assert.Equal((float)sourceRect.Left, (float)sourceRect.Right);
        MediaFeatureSamplerOracle.Near(.5, sampler.Sample(Dependency(plan, "source-1"), sourceRect, 3_000, default).SourceValues!.Mean.R);
    }

    [Fact]
    public async Task ConcurrentExplicitTimesAndWindowsOnTheFormalReaderKeepIndependentResults()
    {
        ProjectMediaDependencyPlan plan = ProjectMediaDependencyResolver.Resolve(Project()); MediaFeatureFileSource source = Source();
        float[][] pixels = [Pixels(), Pixels(1), Pixels(2)];
        var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes);
        await using var opened = await BuildAndOpenAsync(source, pixels, budget);
        using var sampler = new MediaFeatureSampler(MediaFeatureInput.CreateAvailableFile(Context(plan, source), source, opened.Artifact), plan, opened.Reader, budget);
        var rect = new MediaFeatureQueryRect(.125, .25, .75, .875);
        (long Time, int Window)[] requests = [(0, 0), (1_000, 1), (3_000, 1), (9_000, 0), (1_000, 0), (0, 1)];
        Task<ProjectMediaFeatureSample>[] calls = requests.Select(request => Task.Run(() =>
            sampler.Sample(Dependency(plan, "source-" + request.Window), rect, request.Time, default))).ToArray();
        ProjectMediaFeatureSample[] results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10));
        for (int i = 0; i < requests.Length; i++)
        {
            var request = requests[i];
            var expected = MediaFeatureSamplerOracle.Rect(MediaFeatureSamplerOracle.Grid(pixels, request.Time,
                request.Window == 0 ? 0 : 2_000, BackgroundPlaybackMode.Loop), rect, 0, 0, 1, 1, default);
            MediaFeatureSamplerOracle.Source(expected, results[i]);
        }
    }

    [Fact]
    public void SolidUsesTheSharedQuantizationGridWithOneTimelessChargeAndNoReader()
    {
        ProjectMediaDependencyPlan plan = ProjectMediaDependencyResolver.Resolve(Project([0, 1, 2, 3])); MediaFeatureFileSource file = Source();
        var color = new LinearPremulRgba(.123456789, .0625, .25, .5);
        var context = Context(plan, file, MediaMapping.CreateSolid(320, 240));
        var input = MediaFeatureInput.CreateAvailableSolid(context, MediaFeatureSolidSource.Create(file.Profile, color));
        var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes);
        using var sampler = new MediaFeatureSampler(input, plan, null, budget);
        long held = budget.LiveBytes; Assert.InRange(held, 32_256, 45_000);
        var expected = MediaFeatureSamplerOracle.Compose(MediaFeatureSamplerOracle.Quantize(color.R, color.G, color.B, color.A), default);
        foreach (long time in new long[] { 0, 5_000, long.MaxValue }) foreach (int window in new[] { 0, 1, 2, 3 })
        {
            MediaFeatureSamplerOracle.Source(expected, sampler.Sample(Dependency(plan, "source-" + window), new(0, 0, 1, 1), time, default));
            MediaFeatureSamplerOracle.Canvas(expected, sampler.Sample(Dependency(plan, "canvas-" + window), new(0, 0, 320, 240), time, default));
            Assert.Equal(held, budget.LiveBytes);
        }
        Assert.NotEqual(color.R, sampler.Sample(Dependency(plan, "source-0"), new(0, 0, 1, 1), 0, default).SourceValues!.Mean.R);
        // An unrepresentable area is rejected by the existing kernel without changing immutable solid backing.
        Assert.Throws<MediaAbException>(() => sampler.Sample(Dependency(plan, "source-0"), new(0, 0, double.Epsilon, double.Epsilon), 0, default));
        MediaFeatureSamplerOracle.Source(expected, sampler.Sample(Dependency(plan, "source-0"), new(0, 0, 1, 1), 0, default));
        sampler.Dispose(); Assert.Equal(0, budget.LiveBytes);
    }

    [Fact]
    public void NonavailableStatesRemainFixedDoNotAllocateAndDoNotHideInvalidCalls()
    {
        ProjectMediaDependencyPlan plan = ProjectMediaDependencyResolver.Resolve(Project()); MediaFeatureFileSource source = Source();
        MediaInputContext context = Context(plan, source);
        MediaFeatureInput[] inputs = [MediaFeatureInput.CreatePending(context, source, new(Hash('a'))),
            MediaFeatureInput.CreateUnavailable(context, source, new(Hash('a')), MediaFailure.Create(new(Hash('b')), "MediaDecodeFailed", "Controlled decode failed.")),
            MediaFeatureInput.CreateNoMedia(context, MediaNoMediaReason.NoBackgroundContribution, Hash('c')),
            MediaFeatureInput.CreateNoMedia(Context(plan, source, Mapping(output: MediaOutputMode.Transparent)), MediaNoMediaReason.TransparentOutput, Hash('d'))];
        foreach (MediaFeatureInput input in inputs)
        {
            var budget = new MediaWorkingSetBudget(1); using var sampler = new MediaFeatureSampler(input, plan, null, budget);
            foreach (long time in new long[] { 0, 7_000, long.MaxValue })
            {
                ProjectMediaFeatureSample sample = sampler.Sample(Dependency(plan, "source-0"), new(0, 0, 1, 1), time, default);
                Assert.Same(input, sample.Input); Assert.Null(sample.SourceValues); Assert.Null(sample.CanvasValues);
                Assert.Same(source.Profile, sample.Input.Context.RequestedProfile);
            }
            Assert.Equal(0, budget.LiveBytes); Assert.Equal(0, budget.GetSnapshot().PeakReservationCount);
            Assert.Throws<MediaAbException>(() => sampler.Sample(Dependency(plan, "source-0"), default, 0, default));
            Assert.Throws<MediaAbException>(() => sampler.Sample(Dependency(plan, "source-0"), new(0, 0, 1, 1), -1, default));
            Assert.Throws<MediaAbException>(() => sampler.Sample(Dependency(plan, "canvas-0"), new(0, 0, 321, 240), 0, default));
            Assert.Throws<MediaAbException>(() => sampler.Sample(Dependency(plan, "source-0"), new(double.NaN, 0, 1, 1), 0, default));
            Assert.Throws<MediaAbException>(() => sampler.Sample(Dependency(plan, "source-0"), new(0, 0, double.PositiveInfinity, 1), 0, default));
        }
        var first = Assert.IsType<MediaFeatureInput.Pending>(inputs[0]);
        Assert.NotEqual(first.InputRevision, MediaFeatureInput.CreatePending(context, source, new(Hash('e'))).InputRevision);
    }

    [Fact]
    public void ExactPlanMembershipInputDigestsAndReaderMetadataAreRequiredBeforeAnyReadOrCharge()
    {
        ProjectMediaDependencyPlan plan = ProjectMediaDependencyResolver.Resolve(Project()); var fixture = new Mft1IndependentFixture(MediaSourceKind.Still);
        var reader = new ControlledReader(fixture.Identity); var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes);
        var available = MediaFeatureInput.CreateAvailableFile(Context(plan, fixture.Source), fixture.Source, fixture.Identity);
        Assert.Throws<MediaAbException>(() => new MediaFeatureSampler(available, plan, null, budget));
        var wrongContext = MediaInputContext.Create(fixture.Source.Profile, Mapping(), Hash('a'), plan.FixedDependencyDigest);
        Assert.Throws<MediaAbException>(() => new MediaFeatureSampler(MediaFeatureInput.CreateAvailableFile(wrongContext, fixture.Source, fixture.Identity), plan, reader, budget));
        var wrong = new ControlledReader(fixture.Identity) { OverrideSummary = new(fixture.Source.Key, fixture.Source.Profile.Digest,
            fixture.Source.Presentation.Digest, Hash('b'), 1, null) };
        Assert.Throws<MediaAbException>(() => new MediaFeatureSampler(available, plan, wrong, budget));
        var pending = MediaFeatureInput.CreatePending(Context(plan, fixture.Source), fixture.Source, new(Hash('c')));
        Assert.Throws<MediaAbException>(() => new MediaFeatureSampler(pending, plan, reader, budget));
        Assert.Equal(0, budget.LiveBytes); Assert.Equal(0, reader.Reads);
        using var sampler = new MediaFeatureSampler(available, plan, reader, budget);
        foreach (ProjectMediaDependencyPlan foreign in new[] { ProjectMediaDependencyResolver.Resolve(Project()),
            ProjectMediaDependencyResolver.Resolve(Project(parameters: "{\"peak\":1.75}")), ProjectMediaDependencyResolver.Resolve(Project(material: true)) })
            Assert.Throws<MediaAbException>(() => sampler.Sample(foreign.Dependencies[0], new(0, 0, 1, 1), 0, default));
        Assert.Equal(0, reader.Reads);
        Assert.True(EffectPackageProfiles.SupportedV2Capabilities.Contains("media.features"));
    }

    [Fact]
    public void CacheHasOneExactTimePerWindowSharedAcrossSpacesAndActualOwners()
    {
        ProjectMediaDependencyPlan plan = ProjectMediaDependencyResolver.Resolve(Project([0, 1, 2, 3], instances: 2));
        var fixture = new Mft1IndependentFixture(MediaSourceKind.Still); var reader = new ControlledReader(fixture.Identity);
        var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes);
        using var sampler = new MediaFeatureSampler(MediaFeatureInput.CreateAvailableFile(Context(plan, fixture.Source), fixture.Source, fixture.Identity), plan, reader, budget);
        long charge = budget.LiveBytes; Assert.InRange(charge, 4 * 32_256, 4 * 32_256 + 2_000);
        for (int i = 0; i < 4; i++) sampler.Sample(Dependency(plan, "source-" + i), new(0, 0, 1, 1), 7, default);
        Assert.Equal(4, reader.Reads);
        for (int i = 0; i < 4; i++) sampler.Sample(Dependency(plan, "canvas-" + i, 1), new(0, 0, 320, 240), 7, default);
        Assert.Equal(4, reader.Reads);
        sampler.Sample(Dependency(plan, "source-0"), new(0, 0, 1, 1), 8, default);
        sampler.Sample(Dependency(plan, "source-0"), new(0, 0, 1, 1), 7, default);
        Assert.Equal(6, reader.Reads); Assert.Equal(charge, budget.LiveBytes);
    }

    [Fact]
    public void ReadCancellationAndNumericalFailureNeverPublishAPartialOrOldKey()
    {
        ProjectMediaDependencyPlan plan = ProjectMediaDependencyResolver.Resolve(Project()); var fixture = new Mft1IndependentFixture(MediaSourceKind.Still);
        var reader = new ControlledReader(fixture.Identity); var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes);
        using var sampler = new MediaFeatureSampler(MediaFeatureInput.CreateAvailableFile(Context(plan, fixture.Source), fixture.Source, fixture.Identity), plan, reader, budget);
        ProjectMediaDependency dependency = Dependency(plan, "source-0"); var rect = new MediaFeatureQueryRect(0, 0, 1, 1);
        sampler.Sample(dependency, rect, 1, default);
        reader.OnRead = (_, target, _) => { target.Span[0] = new(.5, 0, 0, 1, 0, 0, 1); throw new IOException("Controlled partial read."); };
        Assert.Throws<IOException>(() => sampler.Sample(dependency, rect, 2, default));
        reader.OnRead = null; sampler.Sample(dependency, rect, 1, default); Assert.Equal(3, reader.Reads);
        using var cancellation = new CancellationTokenSource();
        reader.OnRead = (_, target, _) => { target.Span.Fill(new(0, 0, 0, 1, 0, 0, 1)); cancellation.Cancel(); };
        Assert.Equal(cancellation.Token, Assert.ThrowsAny<OperationCanceledException>(() => sampler.Sample(dependency, rect, 2, cancellation.Token)).CancellationToken);
        reader.OnRead = null; sampler.Sample(dependency, rect, 2, default); Assert.Equal(5, reader.Reads);
        reader.OnRead = (_, target, _) => target.Span.Fill(new(.8, .8, .8, 1, 0, 0, 1));
        Assert.Throws<MediaAbException>(() => sampler.Sample(dependency, rect, 3, default));
        reader.OnRead = null; sampler.Sample(dependency, rect, 3, default); Assert.Equal(7, reader.Reads);
    }

    [Fact]
    public async Task LockWaitCancellationDoesNotReadAndDisposeDrainsBeforeReturningOnlyItsCharge()
    {
        ProjectMediaDependencyPlan plan = ProjectMediaDependencyResolver.Resolve(Project()); var fixture = new Mft1IndependentFixture(MediaSourceKind.Still);
        var reader = new ControlledReader(fixture.Identity); var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes);
        using IMediaMemoryReservation readerCharge = budget.Reserve(4_096, MediaWorkingSetPurpose.ReaderCache);
        var sampler = new MediaFeatureSampler(MediaFeatureInput.CreateAvailableFile(Context(plan, fixture.Source), fixture.Source, fixture.Identity), plan, reader, budget);
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.OnRead = (_, target, _) => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None)) throw new TimeoutException(); target.Span.Fill(new(0, 0, 0, 1, 0, 0, 1)); };
        var dependency = Dependency(plan, "source-0"); var rect = new MediaFeatureQueryRect(0, 0, 1, 1);
        Task<ProjectMediaFeatureSample> active = Task.Run(() => sampler.Sample(dependency, rect, 1, default)); Task? dispose = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancel = new CancellationTokenSource();
            Task blocked = Task.Run(() => sampler.Sample(dependency, rect, 2, cancel.Token)); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await blocked);
            dispose = Task.Run(sampler.Dispose);
            // Observe the actual closing admission while the accepted reader is still blocked.
            // Starting Task.Run alone would allow an unscheduled broken Dispose to pass.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            bool closingObserved = false;
            while (!closingObserved)
            {
                deadline.Token.ThrowIfCancellationRequested();
                using var probe = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
                try { sampler.Sample(dependency, rect, 3, probe.Token); }
                catch (ObjectDisposedException) { closingObserved = true; }
                catch (OperationCanceledException error) { Assert.Equal(probe.Token, error.CancellationToken); }
            }
            Assert.NotSame(dispose, await Task.WhenAny(dispose, Task.Delay(100, deadline.Token)));
            Assert.True(budget.LiveBytes > readerCharge.Bytes); Assert.False(reader.Disposed);
        }
        finally
        {
            release.Set(); await active.WaitAsync(TimeSpan.FromSeconds(10));
            if (dispose is not null) await dispose.WaitAsync(TimeSpan.FromSeconds(10)); else sampler.Dispose();
        }
        Assert.Equal(1, reader.Reads); Assert.Equal(readerCharge.Bytes, budget.LiveBytes); Assert.False(reader.Disposed);
        Assert.Throws<ObjectDisposedException>(() => sampler.Sample(dependency, rect, 1, default)); sampler.Dispose();
        readerCharge.Dispose(); Assert.Equal(0, budget.LiveBytes);
    }

    [Fact]
    public void BudgetRefusalPrecedesReadAndSharedReaderReservationCountsAgainstTheSameCeiling()
    {
        ProjectMediaDependencyPlan plan = ProjectMediaDependencyResolver.Resolve(Project()); var fixture = new Mft1IndependentFixture(MediaSourceKind.Still);
        var reader = new ControlledReader(fixture.Identity); var input = MediaFeatureInput.CreateAvailableFile(Context(plan, fixture.Source), fixture.Source, fixture.Identity);
        var budget = new MediaWorkingSetBudget(65_000); using IMediaMemoryReservation readerCharge = budget.Reserve(1_000, MediaWorkingSetPurpose.ReaderCache);
        Assert.Equal(MediaAbFailure.CapacityExceeded, Assert.Throws<MediaAbException>(() => new MediaFeatureSampler(input, plan, reader, budget)).Failure);
        Assert.Equal(1_000, budget.LiveBytes); Assert.Equal(0, reader.Reads);
        var tooLarge = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumDecodeWorkingBytes);
        Assert.Equal(MediaAbFailure.CapacityUnsupported, Assert.Throws<MediaAbException>(() => new MediaFeatureSampler(input, plan, reader, tooLarge)).Failure);
        Assert.Equal(0, tooLarge.LiveBytes);
    }

    [Fact]
    public void BindingRechecksExactSourceMaterialClosureParametersCanvasAndExplicitOutputPolicy()
    {
        Project project = Project(material: true); ProjectSnapshot snapshot = ProjectSnapshot.FromProject(project);
        ProjectMediaDependencyPlan plan = ProjectMediaDependencyResolver.Resolve(snapshot.Effects, snapshot.LyricStyle, snapshot.Lyrics, snapshot.LyricPresentation, snapshot.PresetApplications);
        Assert.Empty(snapshot.Effects.Instances); Assert.Equal(ProjectMediaDependencyOwnerKind.SourceMaterial, Assert.Single(plan.Owners).Kind);
        var source = Source(); var input = MediaFeatureInput.CreatePending(Context(plan, source), source, new(Hash('a')));
        using var sampler = new MediaFeatureSampler(input, plan, null, new MediaWorkingSetBudget(1));
        var binding = new ProjectMediaFeatureBinding(sampler); binding.ValidateSnapshot(snapshot, MediaOutputMode.Normal);
        Assert.Throws<MediaAbException>(() => binding.ValidateSnapshot(snapshot with { Composition = snapshot.Composition with { CanvasWidth = 321 } }, MediaOutputMode.Normal));
        Assert.Throws<MediaAbException>(() => binding.ValidateSnapshot(snapshot, MediaOutputMode.Transparent));
        var changed = project with { LyricStyle = LyricStyle.Default };
        Assert.Throws<MediaAbException>(() => binding.ValidateSnapshot(ProjectSnapshot.FromProject(changed), MediaOutputMode.Normal));
        MaterialSpec.PackageMaterial material = Assert.IsType<MaterialSpec.PackageMaterial>(project.LyricStyle.UnsungFill.Material);
        var changedParameters = project with { LyricStyle = project.LyricStyle with
        {
            UnsungFill = new() { Material = new MaterialSpec.PackageMaterial(material.PackageId, material.Revision, material.ExportId,
                EffectParameterOverrides.Parse("{\"amount\":0.75}")) },
        } };
        Assert.Throws<MediaAbException>(() => binding.ValidateSnapshot(ProjectSnapshot.FromProject(changedParameters), MediaOutputMode.Normal));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => binding.ValidateSnapshot(snapshot, MediaOutputMode.Normal, cancellation.Token));
    }
}
