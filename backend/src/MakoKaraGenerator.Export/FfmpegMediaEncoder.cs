using System.Buffers;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using CliWrap;
using MakoKaraGenerator.Processes;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Export;

/// <summary>
/// 用 FFmpeg 把裸像素帧写成视频文件（docs/roadmap.md §10 的 <c>EXPORT-02</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="MakoKaraGenerator.Media.FfmpegMediaProbe"/> 同样的做法：命令行参数由强类型请求
/// 生成，可执行文件名只在这里出现，界面永远看不到（docs/development.md §12.2）。
/// </para>
/// <para>
/// 取消走 CliWrap 的取消语义：取消标记一响，进程被杀掉，<c>finally</c> 负责收尾。
/// 超时是<b>另一件事</b>——它不该被报成"用户取消了导出"，所以两者分开记账。
/// </para>
/// </remarks>
public sealed class FfmpegMediaEncoder : IMediaEncoder
{
    /// <summary>默认可执行文件名。</summary>
    public const string DefaultExecutableName = "ffmpeg";

    private readonly string _executablePath;

    /// <summary>创建一个编码器。</summary>
    /// <param name="executablePath">FFmpeg 可执行文件路径；省略时使用 PATH 中的 <c>ffmpeg</c>。</param>
    /// <exception cref="ArgumentException"><paramref name="executablePath"/> 为空白时抛出。</exception>
    public FfmpegMediaEncoder(string executablePath = DefaultExecutableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">
    /// 画面尺寸不为正、或（不透明 MP4 时）不为偶数、总帧数为零、输出路径为空时抛出。
    /// </exception>
    /// <exception cref="FileNotFoundException">请求里的音频文件不存在时抛出。</exception>
    /// <exception cref="DirectoryNotFoundException">输出目录不存在时抛出。</exception>
    public async Task<IVideoEncodingSession> StartAsync(
        VideoEncodingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        FfmpegEncodingSession session = new(_executablePath, request);
        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>开始前的校验：宁可在起进程之前把不合法的地方说清楚。</summary>
    private static void Validate(VideoEncodingRequest request)
    {
        if (request.Width <= 0 || request.Height <= 0)
        {
            throw new ArgumentException("画面尺寸必须为正。", nameof(request));
        }

        // 只有 yuv420p 这条路径要求偶数：ProRes 4444 用 yuva444p10le，奇数尺寸完全合法，
        // 不该被"另一种格式的限制"挡住。界面上的奇数尺寸提示也照这个分工说话。
        if (!request.Format.SupportsAlpha() && (request.Width % 2 != 0 || request.Height % 2 != 0))
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"MP4（yuv420p）要求宽高都是偶数，当前为 {request.Width}×{request.Height}；请在合成设置里改成偶数尺寸。"),
                nameof(request));
        }

        if (request.TotalFrames <= 0)
        {
            throw new ArgumentException("总帧数必须为正。", nameof(request));
        }

        if (request.AudioInputFormat is not (null or "wav" or "mp3" or "flac" or "aac" or "mov" or "ogg"))
            throw new ArgumentException("Restricted audio input format must be wav, mp3, flac, aac, mov or ogg.", nameof(request));
        if (request.MaximumOutputBytes is <= 0)
            throw new ArgumentException("Output byte limit must be positive.", nameof(request));

        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);

        if (!string.IsNullOrWhiteSpace(request.AudioPath) && !File.Exists(request.AudioPath))
        {
            throw new FileNotFoundException($"找不到音频文件：{request.AudioPath}", request.AudioPath);
        }

        string fullPath = Path.GetFullPath(request.OutputPath);
        if (!request.Overwrite && File.Exists(fullPath)) throw new IOException($"输出文件已存在：{fullPath}");
        string? directory = Path.GetDirectoryName(fullPath);

        if (directory is not null && !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"输出目录不存在：{directory}");
        }
    }
}

/// <summary>一次正在运行的 FFmpeg 编码会话。</summary>
internal sealed class FfmpegEncodingSession : IVideoEncodingSession
{
    /// <summary>保留的日志行上限：够定位问题，又不会被长片刷爆内存。</summary>
    private const int MaxLogLines = 200;

    private readonly string _executablePath;
    private readonly VideoEncodingRequest _request;
    private readonly string _temporaryOutputPath;
    private readonly Channel<RenderFrame> _frames;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _pipesReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _log = [];
    private readonly object _logGate = new();

    private Task<CommandResult>? _execution;
    private CancellationTokenSource? _timeoutSource;
    private CancellationTokenSource? _linked;
    private int _framesWritten;
    private int _droppedFrames;
    private bool _disposed;
    private bool _completed;

    internal FfmpegEncodingSession(string executablePath, VideoEncodingRequest request)
    {
        _executablePath = executablePath;
        _request = request;
        string fullOutput = Path.GetFullPath(request.OutputPath);
        _temporaryOutputPath = Path.Combine(Path.GetDirectoryName(fullOutput)!,
            "." + Path.GetFileNameWithoutExtension(fullOutput) + "." + Guid.NewGuid().ToString("N") + ".encoding" + Path.GetExtension(fullOutput));

        // 容量 2：让渲染与编码真正重叠（渲染一帧的同时编码器在吃上一帧），
        // 又不会把整部片子堆在内存里。
        _frames = Channel.CreateBounded<RenderFrame>(
            new BoundedChannelOptions(2)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
    }

    /// <inheritdoc/>
    public int FramesWritten => Volatile.Read(ref _framesWritten);

    /// <summary>编码器报告的丢帧数。</summary>
    internal int DroppedFrames => Volatile.Read(ref _droppedFrames);

    /// <summary>给日志用的命令行安全摘要。</summary>
    internal string CommandSummary { get; private set; } = string.Empty;

    /// <summary>启动进程。</summary>
    /// <exception cref="IOException">进程无法启动，或它在写入任何帧之前就结束了。</exception>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> arguments = FfmpegCommandLine.BuildVideoEncoding(_request with { OutputPath = _temporaryOutputPath, Overwrite = false });
        CommandSummary = FfmpegCommandLine.Describe(_executablePath, arguments);
        AddLog(CommandSummary);

        _timeoutSource = new CancellationTokenSource(_request.Timeout ?? VideoExportOptions.DefaultTimeout);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken,
            _timeoutSource.Token);

        Command command = Cli.Wrap(_executablePath)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .WithStandardInputPipe(PipeSource.Create(WriteFramesAsync))
            .WithStandardOutputPipe(PipeTarget.Create(ReadProgressAsync))
            .WithStandardErrorPipe(PipeTarget.Create(ReadLogAsync));

        _execution = ExecuteAsync(command);

        // "进程起不来"必须在这里就报出来，而不是等到第一帧写完才发现。
        // 管道回调在进程起来之后才会被调用，于是这两件事谁先完成就说明了问题。
        Task first = await Task.WhenAny(_pipesReady.Task, _execution).ConfigureAwait(false);

        if (first != _pipesReady.Task)
        {
            await _execution.ConfigureAwait(false);

            throw new IOException($"编码进程在写入任何帧之前就结束了：{Tail()}");
        }
    }

    /// <inheritdoc/>
    public async Task WriteFrameAsync(RenderFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_disposed)
        {
            frame.Dispose();
            throw new ObjectDisposedException(nameof(FfmpegEncodingSession));
        }

        if (frame.Width != _request.Width || frame.Height != _request.Height)
        {
            frame.Dispose();

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"帧尺寸 {frame.Width}×{frame.Height} 与请求的 {_request.Width}×{_request.Height} 不一致。"));
        }

        try
        {
            await _frames.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消要把整条管线停下：光丢一帧没有意义，进程还在等下一帧。
            frame.Dispose();
            await _lifetime.CancelAsync().ConfigureAwait(false);

            throw;
        }
        catch (ChannelClosedException)
        {
            frame.Dispose();

            throw new IOException($"编码进程已经结束，无法再写入帧：{Tail()}");
        }
    }

    /// <inheritdoc/>
    public async Task<VideoEncodingResult> CompleteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_completed) throw new InvalidOperationException("编码会话已经完成。");

        _frames.Writer.TryComplete();

        CommandResult result;

        try
        {
            result = await Execution.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_timeoutSource?.IsCancellationRequested == true)
        {
            await DisposeAsync().ConfigureAwait(false);

            throw new IOException(string.Create(
                CultureInfo.InvariantCulture,
                $"编码超时（超过 {_request.Timeout ?? VideoExportOptions.DefaultTimeout}）：已终止编码进程。"));
        }

        if (result.ExitCode != 0)
        {
            throw new IOException(string.Create(
                CultureInfo.InvariantCulture,
                $"编码失败（退出码 {result.ExitCode}）：{Tail()}"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        _linked!.Token.ThrowIfCancellationRequested();
        long size = File.Exists(_temporaryOutputPath) ? new FileInfo(_temporaryOutputPath).Length : 0L;
        if (size == 0) throw new IOException("编码产物为空，原输出保持。");
        // 暂存产物转交导出事务；验证及会话释放成功之前不能发布最终输出。
        VideoEncodingArtifact artifact = new(_temporaryOutputPath);
        _completed = true;

        return new VideoEncodingResult(
            result.ExitCode,
            FramesWritten,
            DroppedFrames,
            size,
            LogSnapshot(),
            artifact);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 还在跑就杀掉：取消 _lifetime 会让 CliWrap 终止进程。
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _frames.Writer.TryComplete();

        if (_execution is not null)
        {
            try { await _execution.ConfigureAwait(false); }
            catch { /* 执行失败由 Start／Complete 或调用方原始错误报告；此处确保管道和进程已经结束。 */ }
        }

        while (_frames.Reader.TryRead(out RenderFrame? leftover))
        {
            leftover.Dispose();
        }

        _timeoutSource?.Dispose();
        _linked?.Dispose();
        _lifetime.Dispose();
        if (!_completed) File.Delete(_temporaryOutputPath);
    }

    private Task<CommandResult> Execution => _execution
        ?? throw new InvalidOperationException("编码会话尚未启动。");

    private async Task<CommandResult> ExecuteAsync(Command command)
    {
        try
        {
            return await command.ExecuteAsync(_linked!.Token).ConfigureAwait(false);
        }
        catch (Win32Exception exception)
        {
            throw new IOException(
                $"无法启动 {_executablePath}：{exception.Message}。请确认 FFmpeg 已安装并位于 PATH 中。",
                exception);
        }
    }

    /// <summary>把渲染帧写进编码器的标准输入。</summary>
    private async Task WriteFramesAsync(Stream stdin, CancellationToken cancellationToken)
    {
        _pipesReady.TrySetResult();
        try { await WriteFramesToPipeAsync(stdin, cancellationToken).ConfigureAwait(false); }
        finally
        {
            // 消费管道提前退出也必须唤醒正在等待容量的生产者，否则编码失败会永远卡在写帧。
            _frames.Writer.TryComplete();
        }
    }

    private async Task WriteFramesToPipeAsync(Stream stdin, CancellationToken cancellationToken)
    {
        await foreach (RenderFrame frame in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                ReadOnlyMemory<byte> pixels = frame.PixelMemory;

                if (_request.Format.SupportsAlpha())
                {
                    // 预乘 → 直乘：透明输出必须换算，否则半透明歌词会被二次压暗。
                    // 不透明成片里每像素 Alpha 都是 255，这一步是恒等变换，故按格式分支跳过。
                    byte[] straight = ArrayPool<byte>.Shared.Rent(pixels.Length);

                    try
                    {
                        AlphaFrameConverter.ToStraight(pixels.Span, straight);
                        await stdin
                            .WriteAsync(straight.AsMemory(0, pixels.Length), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(straight);
                    }
                }
                else
                {
                    await stdin.WriteAsync(pixels, cancellationToken).ConfigureAwait(false);
                }

                Interlocked.Increment(ref _framesWritten);
            }
            catch (IOException)
            {
                // 编码器提前关掉了输入（例如 -shortest 让它在音频结束时收工）：
                // 这不是错误，剩下的交给 CompleteAsync 的退出码判断。
                return;
            }
            finally
            {
                frame.Dispose();
            }
        }

        await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读编码器的机器可读进度。</summary>
    private async Task ReadProgressAsync(Stream stdout, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(stdout, Encoding.UTF8);

        await foreach (string line in BoundedTextReader.ReadLinesAsync(reader, 4096, true, cancellationToken).ConfigureAwait(false))
        {
            int separator = line.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator];
            string value = line[(separator + 1)..];

            if (key == "drop_frames"
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dropped))
            {
                Interlocked.Exchange(ref _droppedFrames, dropped);
            }
        }
    }

    /// <summary>收集编码器的诊断输出。</summary>
    private async Task ReadLogAsync(Stream stderr, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(stderr, Encoding.UTF8);

        await foreach (string line in BoundedTextReader.ReadLinesAsync(reader, 2048, false, cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                AddLog(line.Trim());
            }
        }
    }

    private void AddLog(string line)
    {
        lock (_logGate)
        {
            _log.Add(line);

            if (_log.Count > MaxLogLines)
            {
                _log.RemoveAt(0);
            }
        }
    }

    private string Tail()
    {
        ImmutableArray<string> lines = LogSnapshot();

        return lines.IsEmpty
            ? "（编码器没有输出任何诊断信息）"
            : string.Join(" | ", lines[^Math.Min(3, lines.Length)..]);
    }

    private ImmutableArray<string> LogSnapshot()
    {
        lock (_logGate)
        {
            return [.. _log];
        }
    }
}
