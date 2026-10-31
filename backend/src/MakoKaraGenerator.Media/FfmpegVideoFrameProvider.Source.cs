using System.Globalization;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media;

public sealed partial class FfmpegVideoFrameProvider
{
    private sealed class FfmpegVideoFrameSource : IVideoFrameSource
    {
        private readonly string _ffmpegPath, _path;
        private readonly byte[] _buffer;
        private readonly VideoFrame _frame;
        private OwnedMediaProcess? _process;
        private bool _disposed;
        private long _nextFrameIndex;
        private long _cachedFrameIndex;
        private bool _hasCachedFrame;
        private readonly TimeSpan _operationTimeout;
        private readonly string? _demuxer;
        internal FfmpegVideoFrameSource(string ffmpegPath, string path, VideoMetadata metadata, TimeSpan operationTimeout, string? demuxer)
        {
            _ffmpegPath = ffmpegPath; _path = path; _operationTimeout = operationTimeout;
            _demuxer = demuxer;
            Width = metadata.Width; Height = metadata.Height;
            FrameRate = metadata.FrameRate > 0 ? metadata.FrameRate : 25;
            DurationUs = metadata.DurationUs;
            _buffer = new byte[checked(Width * 4 * Height)];
            _frame = new(Width, Height, Width * 4, _buffer);
        }
        public long DurationUs { get; }
        public double FrameRate { get; }
        public int Width { get; }
        public int Height { get; }
        public string? LastError { get; private set; }
        public bool TryGetFrame(long sourceTimeUs, out VideoFrame? frame) => TryGetFrame(sourceTimeUs, out frame, CancellationToken.None);
        public bool TryGetFrame(long sourceTimeUs, out VideoFrame? frame, CancellationToken cancellationToken)
        {
            frame = null;
            frame = GetFrameAsync(sourceTimeUs, cancellationToken).GetAwaiter().GetResult();
            return frame is not null;
        }
        private async Task<VideoFrame?> GetFrameAsync(long sourceTimeUs, CancellationToken caller)
        {
            using var deadline = new MediaOperationDeadline(_operationTimeout, caller);
            using var cancellation = deadline.Token.Register(() => Volatile.Read(ref _process)?.Terminate());
            try
            {
                deadline.ThrowIfExpired();
                if (_disposed) { LastError = "视频来源已经释放。"; return null; }
                long target = sourceTimeUs <= 0 ? 0 : (long)Math.Floor(sourceTimeUs / 1_000_000d * FrameRate);
                if (_hasCachedFrame && target == _cachedFrameIndex)
                {
                    deadline.ThrowIfExpired();
                    LastError = null;
                    return _frame;
                }
                // The borrowed buffer will now be overwritten, including on a partial read.
                _hasCachedFrame = false;
                if (_process is null || target < _nextFrameIndex || target > _nextFrameIndex + MaxSequentialSkipFrames)
                    await RestartAsync(target, deadline.Token).ConfigureAwait(false);
                deadline.ThrowIfExpired();
                while (_nextFrameIndex <= target)
                {
                    int total = 0;
                    while (total < _buffer.Length)
                    {
                        int read = await _process!.Output.ReadAsync(_buffer.AsMemory(total), deadline.Token).ConfigureAwait(false);
                        deadline.ThrowIfExpired();
                        if (read == 0)
                        {
                            var ended = _process;
                            await DisposeProcessAsync().ConfigureAwait(false);
                            deadline.ThrowIfExpired();
                            string errors = ended is null ? string.Empty : await ended.Errors.ConfigureAwait(false);
                            LastError = $"背景视频「{Path.GetFileName(_path)}」在这一帧提前结束。{errors.Trim()}";
                            return null;
                        }
                        total += read;
                    }
                    _nextFrameIndex++;
                }
                deadline.ThrowIfExpired();
                _cachedFrameIndex = target;
                _hasCachedFrame = true;
                LastError = null;
                return _frame;
            }
            catch (OperationCanceledException)
            {
                await DisposeProcessAsync().ConfigureAwait(false);
                caller.ThrowIfCancellationRequested();
                LastError = "背景视频取帧超时。";
                return null;
            }
            catch (IOException error)
            {
                await DisposeProcessAsync().ConfigureAwait(false);
                caller.ThrowIfCancellationRequested();
                LastError = error.Message;
                return null;
            }
        }
        private async Task RestartAsync(long target, CancellationToken token)
        {
            await DisposeProcessAsync().ConfigureAwait(false);
            var args = new List<string> { "-v", "error" };
            AddSelfContainedArguments(args, _demuxer);
            if (target > 0)
            { args.Add("-ss"); args.Add((target / FrameRate).ToString("0.######", CultureInfo.InvariantCulture)); }
            args.AddRange(["-i", _path, "-an", "-sn", "-f", "rawvideo", "-pix_fmt", "bgra", "-"]);
            token.ThrowIfCancellationRequested();
            _process = OwnedMediaProcess.Start(_ffmpegPath, args);
            _nextFrameIndex = target;
            LastError = null;
        }
        private async Task DisposeProcessAsync()
        {
            _hasCachedFrame = false;
            var process = Interlocked.Exchange(ref _process, null);
            if (process is not null) await process.CloseAsync().ConfigureAwait(false);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DisposeProcessAsync().GetAwaiter().GetResult();
        }
    }
}
