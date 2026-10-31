using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Export.Tests;

public sealed partial class ExportCommitBoundaryTests
{
    private sealed class BrokenAudioEncoder(string brokenAudio) : IMediaEncoder
    {
        public Task<IVideoEncodingSession> StartAsync(VideoEncodingRequest request, CancellationToken cancellationToken = default) =>
            new FfmpegMediaEncoder().StartAsync(request with { AudioPath = brokenAudio }, cancellationToken);
    }

    private sealed class CallbackProbe(Func<string, CancellationToken, Task<MediaFileInfo?>> probe) : IMediaProbe
    {
        public Task<AudioStreamInfo?> ProbeAsync(string filePath, CancellationToken cancellationToken = default) =>
            new FfmpegMediaProbe().ProbeAsync(filePath, cancellationToken);
        public Task<MediaFileInfo?> ProbeFileAsync(string filePath, CancellationToken cancellationToken = default) => probe(filePath, cancellationToken);
    }

    private sealed class TrackingEncoder : IMediaEncoder
    {
        public Action? AfterComplete { get; init; }
        public Action? AfterDispose { get; init; }
        public Action? AfterFrame { get; init; }
        public int DisposeCount { get; private set; }
        public async Task<IVideoEncodingSession> StartAsync(VideoEncodingRequest request, CancellationToken cancellationToken = default) =>
            new Session(this, await new FfmpegMediaEncoder().StartAsync(request, cancellationToken));

        private sealed class Session(TrackingEncoder owner, IVideoEncodingSession inner) : IVideoEncodingSession
        {
            public int FramesWritten => inner.FramesWritten;
            public async Task WriteFrameAsync(RenderFrame frame, CancellationToken cancellationToken = default)
            {
                await inner.WriteFrameAsync(frame, cancellationToken);
                owner.AfterFrame?.Invoke();
            }
            public async Task<VideoEncodingResult> CompleteAsync(CancellationToken cancellationToken = default)
            {
                VideoEncodingResult result = await inner.CompleteAsync(cancellationToken);
                owner.AfterComplete?.Invoke();
                return result;
            }
            public async ValueTask DisposeAsync()
            {
                owner.DisposeCount++;
                await inner.DisposeAsync();
                owner.AfterDispose?.Invoke();
            }
        }
    }

    private sealed class TrackingRenderFactory(SkiaRenderPipeline pipeline, Action dispose) : IRenderFrameSessionFactory
    {
        public Action? BeforeFrame { get; init; }
        public int DisposeCount { get; private set; }
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IRenderFrameSession>(new Session(this, pipeline, dispose));
        private sealed class Session(TrackingRenderFactory owner, SkiaRenderPipeline pipeline, Action dispose) : IRenderFrameSession
        {
            public ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
            {
                owner.BeforeFrame?.Invoke();
                return ValueTask.FromResult(pipeline.Render(request));
            }
            public ValueTask DisposeAsync()
            {
                owner.DisposeCount++;
                dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
