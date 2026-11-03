using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class GpuDeploymentIdentityTests
{
    [Theory]
    [InlineData("script-refs/MakoKaraGenerator.Scripting.Abstractions.dll")]
    [InlineData("MakoKaraGenerator.Runtime.Hosting.dll")]
    public void ActualReferenceOrRuntimeMutationChangesIdentityWithUnchangedManifestAndMtime(string changed)
    {
        string root = Path.Combine(Path.GetTempPath(), "mako-gpu-closure-" + Guid.NewGuid().ToString("N"));
        string worker = Path.Combine(root, "worker");
        Directory.CreateDirectory(Path.Combine(worker, "script-refs"));
        try
        {
            string executable = Path.Combine(worker, "MakoKaraGenerator.Backend.Worker.dll");
            string runtime = Path.Combine(worker, "MakoKaraGenerator.Runtime.Hosting.dll");
            string reference = Path.Combine(worker, "script-refs/MakoKaraGenerator.Scripting.Abstractions.dll");
            string manifest = Path.Combine(root, "deployment.json");
            File.WriteAllBytes(executable, [1, 2, 3, 4]);
            File.WriteAllBytes(runtime, [5, 6, 7, 8]);
            File.WriteAllBytes(reference, [9, 10, 11, 12]);
            File.WriteAllText(manifest, "{\"unchanged\":true}");
            var identity = new GpuDeploymentIdentity(worker, manifest);
            string before = identity.Read();
            string target = Path.Combine(worker, changed);
            DateTime originalTime = File.GetLastWriteTimeUtc(target);
            byte[] bytes = File.ReadAllBytes(target); bytes[0] ^= 0xff;
            File.WriteAllBytes(target, bytes); File.SetLastWriteTimeUtc(target, originalTime);
            Assert.NotEqual(before, identity.Read());
            Assert.Equal("{\"unchanged\":true}", File.ReadAllText(manifest));
            Assert.Equal(4, identity.LastFileCount);
            Assert.True(identity.LastTotalBytes > 0);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
