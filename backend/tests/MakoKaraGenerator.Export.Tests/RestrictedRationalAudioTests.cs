using System.Diagnostics;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Export.Tests;

public sealed class RestrictedRationalAudioTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task 强WAV透明有理帧率归一化保留最后一帧并补静音或截断(int sourceSeconds)
    {
        using TemporaryDirectory directory = new();
        string audioPath = directory.FilePath("source.wav");
        int sourceSamples = 44_100 * sourceSeconds;
        using (BinaryWriter wave = new(File.Create(audioPath)))
        {
            wave.Write("RIFF"u8); wave.Write(36 + sourceSamples * 2); wave.Write("WAVEfmt "u8);
            wave.Write(16); wave.Write((short)1); wave.Write((short)1); wave.Write(44_100);
            wave.Write(88_200); wave.Write((short)2); wave.Write((short)16);
            wave.Write("data"u8); wave.Write(sourceSamples * 2);
            for (int sample = 0; sample < sourceSamples; sample++) wave.Write((short)4096);
        }
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45));
        await using IVideoEncodingSession session = await new FfmpegMediaEncoder(TestMediaToolPaths.Ffmpeg).StartAsync(new()
        {
            OutputPath = directory.FilePath("candidate.mov"), Width = 32, Height = 18,
            FrameRate = new(30_000, 1001), TotalFrames = 60, Format = ExportVideoFormat.TransparentProRes4444,
            AudioPath = audioPath, AudioInputFormat = "wav", AudioPlan = AudioEncodingPlan.Decide("pcm_s16le"), NormalizeAudioDuration = true,
        }, deadline.Token);
        for (int frameIndex = 0; frameIndex < 60; frameIndex++)
        {
            byte[] pixels = new byte[32 * 18 * 4];
            for (int pixel = 0; pixel < pixels.Length; pixel += 4)
            {
                pixels[pixel] = 16; pixels[pixel + 1] = 32; pixels[pixel + 2] = 64; pixels[pixel + 3] = 128;
            }
            await session.WriteFrameAsync(new(pixels, new RenderTarget { PixelWidth = 32, PixelHeight = 18 },
                PixelFormat.Bgra8888Premultiplied, frameIndex * 1_001_000L / 30), deadline.Token);
        }
        VideoEncodingResult result = await session.CompleteAsync(deadline.Token);
        using (result.Artifact)
        {
            Assert.Equal(60, result.FramesWritten);
            MediaFileInfo? info = await new FfmpegMediaProbe(TestMediaToolPaths.Ffprobe).ProbeFileAsync(
                result.Artifact.TemporaryPath, true, "mov", deadline.Token);
            Assert.Equal(60L, info!.Video!.DecodedFrameCount);
            Assert.Equal(30_000, info.Video.FrameRateNumerator); Assert.Equal(1001, info.Video.FrameRateDenominator);
            Assert.True(info.Video.HasAlpha); Assert.Equal(2_002_000, info.Video.DurationUs);
            Assert.Equal("pcm_s16le", info.Audio!.CodecName);
            // 2.002 seconds at 44.1 kHz is not an integer number of samples.
            Assert.InRange(info.Audio.DurationUs, 2_001_990, 2_002_010);
            ProcessStartInfo decode = new(TestMediaToolPaths.Ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-v", "error", "-i", result.Artifact.TemporaryPath,
                "-map", "0:a:0", "-f", "s16le", "pipe:1" }) decode.ArgumentList.Add(argument);
            using Process process = Process.Start(decode)!;
            Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            using MemoryStream pcm = new();
            await process.StandardOutput.BaseStream.CopyToAsync(pcm, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode); Assert.Equal(string.Empty, await stderr);
            Assert.Equal(88_288 * 2, pcm.Length);
            byte[] actual = pcm.ToArray();
            for (int offset = 0; offset < actual.Length; offset += 2)
            {
                bool sourceAvailable = offset / 2 < sourceSamples;
                Assert.Equal(0, actual[offset]); Assert.Equal(sourceAvailable ? 16 : 0, actual[offset + 1]);
            }
        }
    }
}
