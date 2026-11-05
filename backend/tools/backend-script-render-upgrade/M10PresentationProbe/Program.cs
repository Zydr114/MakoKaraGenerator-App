using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Processes.MediaEnvelope;

namespace M10.Presentation.Probe;

internal static class Program
{
    // Deliberately rooted for the process lifetime. A failed terminal is never disposed as successful cleanup.
    private static readonly List<object> FailedTerminals = [];
    private const long NativeBytes = 128L * 1024 * 1024;
    internal static void RetainFailure(params object[] roots)
    { lock (FailedTerminals) FailedTerminals.AddRange(roots); }

    public static async Task<int> Main(string[] args)
    {
        if (args.Length is < 1 || args[0] is not ("discover" or "verify" or "reject") || (args.Length - 1) % 2 != 0)
            return Usage();
        Dictionary<string, string> options;
        try { options = Enumerable.Range(0, (args.Length - 1) / 2).ToDictionary(i => args[1 + i * 2], i => args[2 + i * 2], StringComparer.Ordinal); }
        catch (ArgumentException) { return Usage(); }
        string[] required = args[0] switch
        {
            "discover" => ["--fixtures", "--evidence", "--gate", "--helper"],
            "verify" => ["--fixtures", "--evidence", "--manifest"],
            _ => ["--fixtures", "--evidence", "--manifest", "--negatives"],
        };
        if (options.Count != required.Length || required.Any(key => !options.TryGetValue(key, out string? value)
            || !Path.IsPathFullyQualified(value))) return Usage();
        string evidence = options["--evidence"];
        if (Directory.Exists(evidence) || File.Exists(evidence)) return Usage();
        using var journal = new EvidenceJournal(evidence);
        using var wholeRun = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        try
        {
            string fixtureManifest = options["--fixtures"];
            string fixtureRoot = Path.GetDirectoryName(fixtureManifest)!;
            FixtureSet fixtures = ProbeJson.Read<FixtureSet>(fixtureManifest); fixtures.Validate();
            journal.Write("run", "presentation-probe-start", new { stage = args[0], nativeBytes = NativeBytes,
                profileLimit = MediaAbCapacityV1.MaximumDecodeWorkingBytes, fixtureManifestSha256 = ProbeFiles.Hash(fixtureManifest),
                orchestrator = AuditedEnvelopeBackend.Session.ReadProc((uint)Environment.ProcessId) });
            if (args[0] == "discover")
            {
                using var discovery = new Discovery(Path.GetFullPath(options["--gate"]), Path.GetFullPath(options["--helper"]), fixtureRoot, fixtures, journal, NativeBytes);
                ClosureManifest manifest = await discovery.RunAsync(fixtureManifest, wholeRun.Token).ConfigureAwait(false);
                ProbeJson.Write(Path.Combine(evidence, "loaded-manifest.json"), manifest);
                ProbeJson.Write(Path.Combine(evidence, "summary.json"), new { status = "discovered-not-accepted", cases = fixtures.Cases.Length });
                Console.WriteLine("Actual loaded closure discovered; verification has not run.");
            }
            else
            {
                ClosureManifest manifest = ProbeJson.Read<ClosureManifest>(options["--manifest"]);
                if (manifest.FixturesSha256 != ProbeFiles.Hash(fixtureManifest)) throw new ProbeFailure("probe-discovery-fixture-set-changed");
                if (args[0] == "verify")
                {
                    var verification = new Verification(manifest, fixtureRoot, fixtures, journal, NativeBytes);
                    await verification.RunAsync(wholeRun.Token).ConfigureAwait(false);
                    ProbeJson.Write(Path.Combine(evidence, "summary.json"), new { status = "passed", cases = fixtures.Cases.Length,
                        scenarios = fixtures.Cases.Length * 2, scope = "finite-presentation-fixture-matrix", fixtures.Oracle });
                    Console.WriteLine("Finite real presentation matrix and borrowed-callback cancellation passed.");
                }
                else
                {
                    RejectionSet negatives = ProbeJson.Read<RejectionSet>(options["--negatives"]); negatives.Validate();
                    if (negatives.FixturesSha256 != manifest.FixturesSha256)
                        throw new ProbeFailure("probe-rejection-fixture-set-changed");
                    var rejection = new RejectionProbe(manifest, fixtures, negatives, fixtureRoot, journal, NativeBytes);
                    bool complete = await rejection.RunAsync(wholeRun.Token).ConfigureAwait(false);
                    ProbeJson.Write(Path.Combine(evidence, "summary.json"), new { status = complete ? "passed" : "incomplete-named-unsupported",
                        sourceRejections = negatives.Cases.Length, closureScenarios = 3,
                        scope = "finite-source-rejection-and-closure-fault-matrix" });
                    Console.WriteLine(complete ? "Finite rejection matrix passed." : "Finite rejection matrix contains a named unsupported scenario.");
                    if (!complete) return 43;
                }
            }
            return 0;
        }
        catch (EnvelopeException error) when (error.Failure == EnvelopeFailure.CapacityUnsupported)
        {
            journal.Write("run", "platform-unsupported", new { error.Failure, error.SafeCode });
            ProbeJson.Write(Path.Combine(evidence, "summary.json"), new { status = "capacity-unsupported", error.SafeCode });
            Console.Error.WriteLine("Native envelope capacity unsupported; inspect private evidence.");
            return 42;
        }
        catch (MediaAbException error) when (error.Failure == MediaAbFailure.CapacityUnsupported)
        {
            journal.Write("run", "platform-unsupported", new { error.Failure, error.SafeDetail });
            ProbeJson.Write(Path.Combine(evidence, "summary.json"), new { status = "capacity-unsupported", error.SafeDetail });
            Console.Error.WriteLine("Native envelope capacity unsupported; inspect private evidence.");
            return 42;
        }
        catch (Exception error)
        {
            string code = error switch { ProbeFailure failure => failure.Message, EnvelopeException envelope => envelope.SafeCode,
                MediaAbException media => media.SafeDetail,
                OperationCanceledException => "probe-deadline-or-caller-canceled", _ => "probe-unexpected-failure-inspect-source" };
            journal.Write("run", "probe-failed", new { category = error.GetType().Name, code, retainedTerminalRoots = FailedTerminals.Count });
            ProbeJson.Write(Path.Combine(evidence, "summary.json"), new { status = "failed", code, retainedTerminalRoots = FailedTerminals.Count });
            Console.Error.WriteLine("Presentation probe failed; inspect private evidence.");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: M10.Presentation.Probe discover --fixtures <absolute/fixtures.json> --evidence <new-absolute-directory> --gate <absolute/gate> --helper <absolute/helper>");
        Console.Error.WriteLine("       M10.Presentation.Probe verify --fixtures <absolute/fixtures.json> --evidence <new-absolute-directory> --manifest <absolute/loaded-manifest.json>");
        Console.Error.WriteLine("       M10.Presentation.Probe reject --fixtures <absolute/fixtures.json> --evidence <new-absolute-directory> --manifest <absolute/loaded-manifest.json> --negatives <absolute/rejections.json>");
        return 64;
    }
}
