using System.Text.Json;
using MakoKaraGenerator.Rendering;

using (RenderPerformanceTrace.Measure("no-capture-before"))
{
    RenderPerformanceTrace.AddBytes("readback", 100);
    Thread.SpinWait(1_000);
}

bool noCaptureExportThrows = false;
try { _ = RenderPerformanceTrace.ExportJson(); }
catch (InvalidOperationException) { noCaptureExportThrows = true; }
Require(noCaptureExportThrows, "Export before the first capture must fail.");

using (RenderPerformanceTrace.BeginCapture("root"))
{
    using (RenderPerformanceTrace.Measure("async.parent"))
    {
        await Task.Run(async () =>
        {
            using (RenderPerformanceTrace.Measure("async.task"))
            {
                await Task.Yield();
                using (RenderPerformanceTrace.Measure("async.after-await")) Thread.SpinWait(1_000);
                RenderPerformanceTrace.AddBytes("readback", 40);
                Recurse(3);
            }
        });
    }
    using (RenderPerformanceTrace.Measure("sibling.first")) Thread.SpinWait(1_000);
    RenderPerformanceTrace.AddBytes("readback", 2);
    await Task.Yield();
    using (RenderPerformanceTrace.Measure("sibling.second")) Thread.SpinWait(1_000);
}

string json = RenderPerformanceTrace.ExportJson();
using (RenderPerformanceTrace.Measure("no-capture-after")) Thread.SpinWait(1_000);
Require(json == RenderPerformanceTrace.ExportJson(), "A no-capture span changed the previous capture.");
using JsonDocument document = JsonDocument.Parse(json);
JsonElement root = document.RootElement;
Require(root.GetProperty("valid").GetBoolean(), "Capture validation failed: " + root.GetProperty("errors"));
Require(root.GetProperty("spanCount").GetInt32() == 9, "Unexpected span count.");
Require(root.GetProperty("bytes").GetProperty("readback").GetInt64() == 42, "Unexpected readback bytes.");

var stages = root.GetProperty("stages").EnumerateArray().ToDictionary(
    stage => stage.GetProperty("name").GetString()!, stage => stage, StringComparer.Ordinal);
string[] expected = ["root", "async.parent", "async.task", "async.after-await", "recursive", "sibling.first", "sibling.second"];
Require(stages.Count == expected.Length && expected.All(stages.ContainsKey), "Unexpected stage names.");
foreach (string name in expected)
{
    JsonElement stage = stages[name];
    Require(stage.GetProperty("calls").GetInt32() == (name == "recursive" ? 3 : 1), "Unexpected call count: " + name);
    double inclusive = stage.GetProperty("inclusiveMs").GetDouble();
    double exclusive = stage.GetProperty("exclusiveMs").GetDouble();
    double min = stage.GetProperty("minMs").GetDouble();
    double max = stage.GetProperty("maxMs").GetDouble();
    Require(inclusive >= 0 && exclusive >= 0 && exclusive <= inclusive && min >= 0 && max >= min,
        "Invalid duration: " + name);
}
double rootInclusive = stages["root"].GetProperty("inclusiveMs").GetDouble();
double exclusiveSum = stages.Values.Sum(stage => stage.GetProperty("exclusiveMs").GetDouble());
Require(Math.Abs(rootInclusive - exclusiveSum) <= Math.Max(1e-9, rootInclusive * 1e-10),
    $"Exclusive sum {exclusiveSum} ms differs from root {rootInclusive} ms.");
Console.WriteLine("TraceCheck passed: async propagation, nested and sibling spans, no-capture behavior, and exclusive accounting.");
Console.WriteLine(json);

static void Recurse(int depth)
{
    if (depth == 0) return;
    using (RenderPerformanceTrace.Measure("recursive"))
    {
        Thread.SpinWait(1_000);
        Recurse(depth - 1);
    }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
