using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;

namespace MakoKaraGenerator.Scripting.Tests;

// Separate bounded process, no GUI or media devices. Compile and a two-second warmup are excluded.
internal static class SemanticCostBenchmark
{
    private const string Fingerprint = "semantic-cost-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string Source = """
        using System;
        using MakoKaraGenerator.Scripting;
        public sealed class CostProbe : EffectBase
        {
            private string mode;
            public override void Initialize(EffectInitializationContext context) { mode = context.Parameters.GetEnum("mode"); }
            public override void Evaluate(FrameContext frame, FrameWriter writer)
            {
                if (mode == "warning") frame.Diagnostics.Report(DiagnosticSeverity.Warning, "CostWarning", "one real Evaluate occurrence");
                if (mode == "records")
                {
                    var records = frame.History.Records;
                    if (records.Length > 0 && records[records.Length - 1].TimeUs > frame.LastSimulationBoundaryUs)
                        throw new Exception("Future record leaked");
                }
            }
        }
        """;

    internal static async Task<int> RunAsync(string scenario, int seconds, string output, bool incremental = false)
    {
        try
        {
            if (scenario is not ("empty" or "audio" or "warning" or "records") || seconds is < 1 or > 180)
                throw new ArgumentOutOfRangeException(nameof(scenario));
            var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
            var artifact = await compiler.CompileAsync([new("src/CostProbe.cs", Source)]);
            if (!artifact.Succeeded) throw new InvalidOperationException(string.Join("\n", artifact.Diagnostics));
            var definition = new CompiledFrameEffect(new("cost"), artifact, "CostProbe",
                new(new Dictionary<string, ParameterValue> { ["mode"] = new ParameterValue.Choice(scenario) }),
                new EffectBinding.Project(), EffectCapabilities.None);

            async Task<Result> Measure(int duration, bool measured)
            {
                long durationUs = duration * 1_000_000L;
                var snapshot = ProjectSnapshot.FromProject(Project.Create("semantic cost")) with
                { Composition = Project.Create("semantic cost").Composition with { OutputDurationUs = durationUs }, EffectInputsFingerprint = Fingerprint };
                AudioFeatures? audio = scenario is "audio" or "records" ? new("fixed-50Hz", durationUs,
                    Enumerable.Range(0, duration * 50).Select(index => new AudioFeaturePoint(index * 20_000L, new(.1, .2, null, 0))).ToImmutableArray()) : null;
                var factory = new TimelineFrameSourceFactory([definition], Fingerprint, static () => new Geometry(), ReplayReason.Export, audio);
                using var bounded = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                long peak = 0; using var stop = new ManualResetEventSlim(); using var started = new ManualResetEventSlim();
                var sampler = new Thread(() =>
                {
                    started.Set();
                    do { long current = GC.GetTotalMemory(false); if (current > peak) Interlocked.Exchange(ref peak, current); }
                    while (!stop.Wait(2));
                }) { IsBackground = true, Name = "owned semantic heap sampler" };
                ResetCounters();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); sampler.Start(); started.Wait();
                long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                var watch = Stopwatch.StartNew(); long visits = 0; int lastDiagnostics = 0;
                var cursor = new RuntimeDiagnosticCursor();
                try
                {
                    await using var session = await factory.CreateAsync(snapshot, bounded.Token);
                    var target = RenderTarget.FromComposition(snapshot.Composition);
                    for (int frameIndex = 0; frameIndex <= duration * 30; frameIndex++)
                    {
                        var frame = await session.PrepareAsync(frameIndex * 1_000_000L / 30, OutputMode.Opaque, target, bounded.Token);
                        lastDiagnostics = incremental ? frame.DiagnosticSnapshot.Count : frame.Diagnostics.Length;
                        var diagnostics = incremental ? cursor.ReadNew(frame.DiagnosticSnapshot).Select(item => item.Diagnostic)
                            : frame.Diagnostics.AsEnumerable();
                        foreach (var diagnostic in diagnostics)
                        {
                            if (diagnostic.Diagnostic.Severity == DiagnosticSeverity.Error) throw new InvalidOperationException("A successful frame contains Error.");
                            visits++;
                        }
                    }
                }
                finally { watch.Stop(); stop.Set(); sampler.Join(TimeSpan.FromSeconds(5)); }
                if (sampler.IsAlive) throw new IOException("Owned heap sampler did not stop.");
                return new(scenario, duration, GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore, peak,
                    watch.Elapsed.TotalMilliseconds, ReadCounter("BenchmarkValidatedRecords"), ReadCounter("BenchmarkHistoryCopiedRecords"),
                    ReadCounter("BenchmarkCompatibilityCopiedRecords"), ReadCounter("BenchmarkCompatibilityReads"), visits, lastDiagnostics, measured,
                    ReadDiagnosticCounter(typeof(RuntimeDiagnosticBuffer), "BenchmarkDiagnosticCopiedRecords"),
                    ReadDiagnosticCounter(typeof(RuntimeDiagnosticSnapshot), "BenchmarkDiagnosticArrayCopiedRecords"),
                    ReadDiagnosticCounter(typeof(RuntimeDiagnosticSnapshot), "BenchmarkDiagnosticReadRecords"),
                    ReadDiagnosticCounter(typeof(RuntimeDiagnosticBuffer), "BenchmarkDiagnosticValidatedRecords"));
            }

            await Measure(2, measured: false);
            var result = await Measure(seconds, measured: true);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            {
                productVersion = ProductVersion.Current, processId = Environment.ProcessId, scenario, seconds, incremental,
                runtime = Environment.Version.ToString(), processorCount = Environment.ProcessorCount, result,
                note = "2ms sampled managed occupancy; not a guaranteed peak or native/process RSS. Compilation, audio fixture and warmup excluded; session creation, advance, selected full or incremental diagnostic consumer and disposal included.",
            }, JsonOptions));
            Console.WriteLine(JsonSerializer.Serialize(result)); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static readonly string[] CounterNames = ["BenchmarkValidatedRecords", "BenchmarkHistoryCopiedRecords", "BenchmarkCompatibilityCopiedRecords", "BenchmarkCompatibilityReads"];
    private static void ResetCounters()
    {
        foreach (string name in CounterNames) typeof(TimelineHistory).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, 0L);
        foreach (var (type, name) in new[] { (typeof(RuntimeDiagnosticBuffer), "BenchmarkDiagnosticCopiedRecords"),
            (typeof(RuntimeDiagnosticSnapshot), "BenchmarkDiagnosticArrayCopiedRecords"), (typeof(RuntimeDiagnosticSnapshot), "BenchmarkDiagnosticReadRecords"),
            (typeof(RuntimeDiagnosticBuffer), "BenchmarkDiagnosticValidatedRecords") })
            type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, 0L);
    }
    private static long? ReadCounter(string name) => (long?)typeof(TimelineHistory).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
    private static long? ReadDiagnosticCounter(Type type, string name) => (long?)type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
    private sealed record Result(string Scenario, int Seconds, long AllocatedBytes, long SampledPeakManagedBytes,
        double AdvanceMilliseconds, long? ValidatedRecords, long? HostCopiedRecords, long? CompatibilityCopiedRecords,
        long? CompatibilityReads, long DiagnosticConsumerVisits, int FinalDiagnostics, bool Measured,
        long? DiagnosticCopiedRecords, long? DiagnosticArrayCopiedRecords, long? DiagnosticReadRecords, long? DiagnosticValidatedRecords);
    private sealed class Geometry : ILineGeometryProvider
    {
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => null;
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
            => LyricSceneSampler.SampleFrame(snapshot, timeUs);
    }
}
