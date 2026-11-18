using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Backend.Worker;

/// <summary>仅供显式 MAKO_SHM_G2_BENCHMARK=1 门禁探针调用的真实生产组件驱动器。</summary>
internal static class ShmG2ProducerRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record CommandEnvelope(
        string Cmd,
        string? StreamId = null,
        string? HostInstanceId = null,
        string? InputGeneration = null,
        int Width = 1280,
        int Height = 720,
        string PixelFormat = "bgra8888",
        uint StrideBytes = 0,
        string? BrokerPath = null,
        string? Handshake = null);

    public static async Task<int> RunAsync()
    {
        string backendName = Environment.GetEnvironmentVariable("MAKO_RENDER_BACKEND")?.Trim().ToLowerInvariant() is "vulkan"
            ? PreviewWireConventions.ApiVulkan
            : PreviewWireConventions.ApiCpu;

        using ISkiaRenderDevice device = backendName == PreviewWireConventions.ApiVulkan
            ? VulkanSkiaRenderDevice.Create(enableGpuPreview: true, previewTransport: PreviewTransportKind.Shm)
            : new CpuSkiaRenderDevice();
        using var renderer = new SkiaRenderPipeline(null, null, null, null, false, device, false);
        using var portManager = new PreviewPortSessionManager(device);

        GpuStreamSession? activeSession = null;

        await WriteEventAsync(new
        {
            type = "ready",
            pid = Environment.ProcessId,
            api = backendName,
            device = device.PreviewIdentity.Name,
        }).ConfigureAwait(false);

        try
        {
            while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                CommandEnvelope cmd = JsonSerializer.Deserialize<CommandEnvelope>(line, JsonOptions)
                    ?? throw new InvalidOperationException("Invalid G2 command JSON.");

                if (cmd.Cmd == "open")
                {
                    if (activeSession is not null)
                    {
                        _ = await activeSession.StopAsync(CancellationToken.None).ConfigureAwait(false);
                        await activeSession.DisposeAsync().ConfigureAwait(false);
                        activeSession = null;
                    }

                    PreviewPixelFormat format = PreviewWireConventions.ParsePixelFormat(cmd.PixelFormat);
                    var target = new RenderTarget { PixelWidth = cmd.Width, PixelHeight = cmd.Height };
                    PreviewPoolInfo pool = portManager.InitializeOutput(
                        target,
                        maximumAllocationBytes: 64UL * 1024 * 1024,
                        new PreviewOutputOptions(PreviewTransportKind.Shm, format, cmd.StrideBytes));

                    var open = new GpuStreamWorkerOpen(
                        cmd.StreamId ?? throw new InvalidOperationException("Missing streamId"),
                        cmd.HostInstanceId ?? "g2-host",
                        cmd.InputGeneration ?? "1",
                        cmd.Width,
                        cmd.Height,
                        TransparentBackground: true,
                        MaximumAllocationBytes: 64UL * 1024 * 1024,
                        ExpectedMainPid: int.Parse(Environment.GetEnvironmentVariable("MAKO_G2_MAIN_PID") ?? "0", System.Globalization.CultureInfo.InvariantCulture),
                        BrokerPath: cmd.BrokerPath ?? throw new InvalidOperationException("Missing brokerPath"),
                        Handshake: cmd.Handshake ?? throw new InvalidOperationException("Missing handshake"),
                        Transport: "shm",
                        BackendApi: backendName,
                        PixelFormat: format.ToWireName(),
                        StrideBytes: pool.StrideBytes);

                    IPreviewPort port = portManager.RequireCurrentPort(target);
                    var channel = new LocalStreamPreviewChannel(open.BrokerPath);
                    try
                    {
                        activeSession = new GpuStreamSession(
                            open,
                            pool,
                            channel,
                            (timeUs, _, _) => ValueTask.FromResult(renderer.RenderSyntheticToPreview(target, timeUs, port, format)),
                            synthetic: true);
                    }
                    catch
                    {
                        channel.Dispose();
                        throw;
                    }

                    await WriteEventAsync(new
                    {
                        type = "opened",
                        opened = activeSession.Opened,
                    }).ConfigureAwait(false);
                }
                else if (cmd.Cmd == "close")
                {
                    bool drained = true;
                    GpuStreamWorkerState? finalState = null;
                    if (activeSession is not null)
                    {
                        drained = await activeSession.StopAsync(CancellationToken.None).ConfigureAwait(false);
                        finalState = activeSession.ReadState();
                        await activeSession.DisposeAsync().ConfigureAwait(false);
                        activeSession = null;
                    }

                    await WriteEventAsync(new
                    {
                        type = "closed",
                        drained,
                        workerState = finalState,
                    }).ConfigureAwait(false);
                }
                else if (cmd.Cmd == "shutdown")
                {
                    if (activeSession is not null)
                    {
                        _ = await activeSession.StopAsync(CancellationToken.None).ConfigureAwait(false);
                        await activeSession.DisposeAsync().ConfigureAwait(false);
                        activeSession = null;
                    }

                    await WriteEventAsync(new { type = "exiting" }).ConfigureAwait(false);
                    return 0;
                }
                else
                {
                    throw new InvalidOperationException($"Unknown G2 command: {cmd.Cmd}");
                }
            }

            return 0;
        }
        finally
        {
            if (activeSession is not null)
            {
                try { await activeSession.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
    }

    private static async Task WriteEventAsync<T>(T payload)
    {
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        await Console.Out.WriteLineAsync("G2_EVENT:" + json).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }
}
