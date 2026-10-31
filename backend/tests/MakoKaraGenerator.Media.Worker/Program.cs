using System.Text.Json;
using System.Diagnostics;

// An owned controllable process; input is a test fixture, never a real media file.
if (args[0] == "--hold-pipe")
{
    File.WriteAllText(args[1] + ".descendant.pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await Task.Delay(TimeSpan.FromHours(1)); return;
}
string path = args.Contains("-i", StringComparer.Ordinal) ? args[Array.IndexOf(args, "-i") + 1] : args[^1];
if (File.Exists(path + ".forward"))
{
    File.AppendAllText(path + ".decoder.starts", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
    var forwarding = new ProcessStartInfo(File.ReadAllText(path + ".forward")) { UseShellExecute = false };
    foreach (string argument in args) forwarding.ArgumentList.Add(argument);
    using var decoder = Process.Start(forwarding)!;
    await decoder.WaitForExitAsync(); Environment.ExitCode = decoder.ExitCode; return;
}
using var configuration = JsonDocument.Parse(File.ReadAllText(path));
string mode = configuration.RootElement.GetProperty("mode").GetString()!;
bool decoding = args.Contains("-i", StringComparer.Ordinal);
File.WriteAllText(path + (decoding ? ".decoder.pid" : ".probe.pid"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
if (decoding) File.AppendAllText(path + ".decoder.starts", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
if (!decoding)
{
    if (mode == "probe-descendant")
    {
        var child = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        child.ArgumentList.Add("--hold-pipe"); child.ArgumentList.Add(path);
        using var descendant = Process.Start(child)!; await Task.Delay(TimeSpan.FromHours(1));
    }
    if (mode == "probe-gated-exit")
    {
        while (!File.Exists(path + ".release")) await Task.Delay(5);
    }
    if (mode == "probe-stall") { Console.Write("{"); Console.Out.Flush(); await Task.Delay(TimeSpan.FromHours(1)); }
    if (mode == "probe-invalid") { Console.Write("{invalid"); return; }
    if (mode == "probe-overflow") { Console.Write(new string('o', 1_100_000)); Console.Out.Flush(); await Task.Delay(TimeSpan.FromHours(1)); }
    if (mode == "probe-error") { Console.Error.Write(new string('e', 512_000)); Environment.Exit(7); }
    if (mode == "probe-late") await Task.Delay(1000);
    if (mode == "probe-full-stderr") { Console.Error.Write(new string('e', 512_000)); Console.Error.Flush(); }
    Console.Write("{\"streams\":[{\"width\":1,\"height\":1,\"avg_frame_rate\":\"30/1\"}],\"format\":{\"duration\":\"2\"}}");
    return;
}
using Stream output = Console.OpenStandardOutput();
if (mode == "decoder-error") { Console.Error.Write("controlled decoder failure"); Environment.Exit(8); }
if (mode == "decoder-error-flood") { Console.Error.Write(new string('e', 512_000)); Environment.Exit(8); }
if (mode == "late-frame") await Task.Delay(1000);
if (mode == "half-frame")
{
    await output.WriteAsync(new byte[] { 1, 2 }); await output.FlushAsync();
    await Task.Delay(TimeSpan.FromHours(1));
}
else
{
    int start = args.Contains("-ss", StringComparer.Ordinal)
        ? (int)Math.Round(double.Parse(args[Array.IndexOf(args, "-ss") + 1], System.Globalization.CultureInfo.InvariantCulture) * 30) : 0;
    for (int i = start; i < 60; i++)
    {
        if (mode == "one-frame-then-half" && i > 0)
        { await output.WriteAsync(new byte[] { 99, 2 }); await output.FlushAsync(); await Task.Delay(TimeSpan.FromHours(1)); }
        byte[] frame = [(byte)i, 2, 3, 255];
        if (mode == "drip-frame")
        {
            foreach (byte value in frame) { await output.WriteAsync(new byte[] { value }); await output.FlushAsync(); await Task.Delay(200); }
        }
        else { await output.WriteAsync(frame); await output.FlushAsync(); if (mode == "slow-sequence") await Task.Delay(80); }
    }
}
