using System.Security.Cryptography;
using System.Text;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>Actual Worker file closure SHA256; manifests never substitute for file bytes.</summary>
internal sealed class GpuDeploymentIdentity(string workerDirectory, params string[] extraFiles)
{
    internal long LastTotalBytes { get; private set; }
    internal int LastFileCount { get; private set; }

    public string Read()
    {
        string root = Path.GetFullPath(workerDirectory);
        string[] files = Inventory(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        foreach (string path in files)
        {
            byte[] name = Encoding.UTF8.GetBytes(path);
            hash.AppendData(BitConverter.GetBytes(name.Length)); hash.AppendData(name);
            RejectLink(path);
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            total = checked(total + stream.Length);
            hash.AppendData(SHA256.HashData(stream));
        }
        foreach (string missing in extraFiles.Select(Path.GetFullPath).Where(path => !File.Exists(path)).Order(StringComparer.Ordinal))
        {
            byte[] name = Encoding.UTF8.GetBytes("missing:" + missing);
            hash.AppendData(BitConverter.GetBytes(name.Length)); hash.AppendData(name);
        }
        if (!files.SequenceEqual(Inventory(root), StringComparer.Ordinal))
            throw new IOException("GPU deployment file inventory changed during closure hashing.");
        LastTotalBytes = total; LastFileCount = files.Length;
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private string[] Inventory(string root)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        Visit(root);
        foreach (string extra in extraFiles.Select(Path.GetFullPath))
            if (File.Exists(extra)) { RejectLink(extra); files.Add(extra); }
        return files.ToArray();

        void Visit(string directory)
        {
            RejectLink(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectLink(entry);
                if (Directory.Exists(entry)) Visit(entry);
                else files.Add(entry);
            }
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("GPU deployment closure must not contain symbolic links.");
    }
}
