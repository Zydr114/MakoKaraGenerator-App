using System.Diagnostics;
using MakoKaraGenerator.Backend.Contracts;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerOperationBoundsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Real_worker_bounds_active_operations_and_releases_completed_slots()
    {
        string dll = WorkerBinaryLocator.LocateBackendWorkerDll();
        ProcessStartInfo start = new("dotnet")
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(dll);
        using Process process = new() { StartInfo = start };
        Assert.True(process.Start());
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        Stream input = process.StandardInput.BaseStream, replies = process.StandardOutput.BaseStream;
        try
        {
            await WorkerProtocol.WriteAsync(input, new("hello", "hello"), deadline.Token);
            Assert.Equal("hello", (await WorkerProtocol.ReadAsync(replies, deadline.Token))!.Kind);
            for (int index = 0; index < 3; index++)
                await WorkerProtocol.WriteAsync(input, new("render", $"active-{index}", WorkerProtocol.ComputeSha256([1]), [1], delayMilliseconds: 60_000), deadline.Token);
            WorkerMessage busy = (await WorkerProtocol.ReadAsync(replies, deadline.Token))!;
            Assert.Equal("active-2", busy.RequestId); Assert.Equal("error", busy.Kind); Assert.Equal("WorkerBusy", busy.ErrorCode);
            await WorkerProtocol.WriteAsync(input, new("cancel", "active-0"), deadline.Token);
            await WorkerProtocol.WriteAsync(input, new("cancel", "active-1"), deadline.Token);
            HashSet<string> cancelled = [];
            for (int index = 0; index < 2; index++)
            {
                WorkerMessage response = (await WorkerProtocol.ReadAsync(replies, deadline.Token))!;
                Assert.True(response.Cancelled); cancelled.Add(response.RequestId);
            }
            Assert.Equal(["active-0", "active-1"], cancelled.Order(StringComparer.Ordinal));
            // Wait only for the completed-reply continuation to release its handoff slot.
            await Task.Delay(20, deadline.Token);
            for (int index = 0; index < 8; index++)
            {
                byte[] payload = [(byte)index];
                await WorkerProtocol.WriteAsync(input, new("render", $"later-{index}", WorkerProtocol.ComputeSha256(payload), payload), deadline.Token);
                WorkerMessage response = (await WorkerProtocol.ReadAsync(replies, deadline.Token))!;
                Assert.Equal($"later-{index}", response.RequestId); Assert.Equal("result", response.Kind); Assert.Equal(payload, response.Payload);
            }
            await WorkerProtocol.WriteAsync(input, new("shutdown", "shutdown"), deadline.Token);
            Assert.Equal("shutdown", (await WorkerProtocol.ReadAsync(replies, deadline.Token))!.Kind);
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
            output.WriteLine($"Bounded real Worker PID={process.Id}; third active call rejected, eight subsequent calls accepted, shutdown ExitCode={process.ExitCode}.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            _ = await stderr;
        }
    }
}
