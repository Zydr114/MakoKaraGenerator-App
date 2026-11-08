using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

/// <summary>分析失败后按退避延迟由下一次调用惰性重试，次数用完后本次运行固定失败。</summary>
public sealed class BackendMediaFeatureServiceRetryTests
{
    [Fact]
    public async Task 失败的分析到延迟后换新尝试身份重试且次数用完后固定()
    {
        string root = Directory.CreateTempSubdirectory("mkg-media-retry-").FullName;
        try
        {
            // 工具目录不存在：每次尝试都在部署发现处失败，不需要真实受控解码。
            ManualTime time = new();
            await using BackendMediaFeatureService service = BackendMediaFeatureService.Create(
                Path.Combine(root, "state"), Path.Combine(root, "missing-tools"), time);
            int changes = 0;
            service.Changed += () => Interlocked.Increment(ref changes);
            string image = Path.Combine(root, "background.png");
            byte[] bytes = RandomNumberGenerator.GetBytes(256);
            await File.WriteAllBytesAsync(image, bytes);
            var content = new MediaFileContent(new(Convert.ToHexStringLower(SHA256.HashData(bytes))), bytes.LongLength);
            Task<BackendMediaFeatureResolution> Resolve() =>
                service.ResolveAsync(image, content, MediaSourceKind.Still, Timeout.InfiniteTimeSpan, CancellationToken.None);

            BackendMediaFeatureResolution first = await Resolve();
            Assert.Null(first.Source);
            Assert.Null(first.Artifact);
            Assert.Equal(first.Request, (await Resolve()).Request);

            List<MediaPreparationRequestId> attempts = [first.Request];
            foreach (TimeSpan delay in BackendMediaFeatureService.RetryDelays)
            {
                time.Advance(delay - TimeSpan.FromSeconds(1));
                Assert.Equal(attempts[^1], (await Resolve()).Request);
                time.Advance(TimeSpan.FromSeconds(1));
                BackendMediaFeatureResolution retried = await Resolve();
                Assert.Null(retried.Source);
                Assert.DoesNotContain(retried.Request, attempts);
                attempts.Add(retried.Request);
            }
            time.Advance(TimeSpan.FromHours(12));
            Assert.Equal(attempts[^1], (await Resolve()).Request);
            Assert.Equal(BackendMediaFeatureService.RetryDelays.Length + 1, attempts.Count);
            Assert.Equal(attempts.Count, Volatile.Read(ref changes));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task 有界等待超时后仍返回检查结果且不限时等待不接受负值()
    {
        string root = Directory.CreateTempSubdirectory("mkg-media-wait-").FullName;
        try
        {
            await using BackendMediaFeatureService service = BackendMediaFeatureService.Create(
                Path.Combine(root, "state"), Path.Combine(root, "missing-tools"), TimeProvider.System);
            string image = Path.Combine(root, "background.png");
            byte[] bytes = RandomNumberGenerator.GetBytes(64);
            await File.WriteAllBytesAsync(image, bytes);
            var content = new MediaFileContent(new(Convert.ToHexStringLower(SHA256.HashData(bytes))), bytes.LongLength);
            BackendMediaFeatureResolution bounded = await service.ResolveAsync(image, content, MediaSourceKind.Still,
                TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Null(bounded.Source);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ResolveAsync(image, content, MediaSourceKind.Still,
                TimeSpan.FromSeconds(-1), CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
