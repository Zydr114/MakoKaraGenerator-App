using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Backend.Hosting;

internal sealed partial class BackendMediaFeatureArtifactStore
{
    private void ScanExisting()
    {
        int count = 0;
        foreach (string path in BoundedChildren(_root, MaximumEntries + 1))
        {
            RejectLink(path);
            if (path == _staging) continue;
            Require(Directory.Exists(path), "Media storage root contains an unexpected file.");
            string id = Path.GetFileName(path);
            Require(BundlePath(id) == path && ++count <= MaximumEntries, "Media storage entries exceed their fixed bound.");
            string[] files = ValidateBundle(path, allowEmpty: true);
            if (files.Length == 0)
            {
                // An interrupted prune may unlink track before removing its directory.
                // Retain the bounded identity slot, never grant it an available artifact.
                _entries.Add(id, new(path, 0) { Missing = true });
                continue;
            }
            long bytes = MeasureFile(files[0]);
            _entries.Add(id, new(path, bytes)); _committedBytes = checked(_committedBytes + bytes);
        }
        foreach (string path in BoundedChildren(_staging, MaximumEntries))
        {
            RejectLink(path);
            Require(Directory.Exists(path) && IsGuid(Path.GetFileName(path)) && ++count <= MaximumEntries,
                "Media staging identity or entry count is invalid.");
            long bytes = MeasureStage(path);
            _orphans.Add(path, bytes); _orphanBytes = checked(_orphanBytes + bytes);
        }
        // Over-quota startup never silently omits bytes or grants a fresh reservation.
    }

    private static string[] BoundedChildren(string directory, int maximum)
    {
        RejectLink(directory); var entries = new List<string>(Math.Min(maximum, 16));
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            Require(entries.Count < maximum, "Media directory contains too many entries.");
            entries.Add(path);
        }
        return [.. entries];
    }

    private static void EnsureDirectoryChain(string directory)
    {
        string full = Path.GetFullPath(directory);
        string volume = Path.GetPathRoot(full) ?? throw Invalid("Media state root is not absolute.");
        string current = volume; RejectLink(current);
        foreach (string segment in full[volume.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current)) throw Invalid("Media directory chain contains a file.");
            if (Directory.Exists(current)) RejectLink(current);
            else { Directory.CreateDirectory(current); RejectLink(current); }
        }
    }

    private static void RejectLink(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        Require((attributes & FileAttributes.ReparsePoint) == 0 &&
            (attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(path).LinkTarget : new FileInfo(path).LinkTarget) is null,
            "Media storage rejects links and reparse points.");
    }

    private static bool ExistsChecked(string path)
    {
        try { RejectLink(path); return true; }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private static long MeasureFile(string path)
    {
        RejectLink(path); Require(!Directory.Exists(path), "Media file slot contains a directory.");
        long length = new FileInfo(path).Length;
        Require(length >= 0 && length <= MediaAbCapacityV1.MaximumArtifactBytes, "Media storage file exceeds the fixed byte ceiling.");
        return length;
    }

    private static string[] ValidateBundle(string directory, bool allowEmpty = false)
    {
        string[] files = BoundedChildren(directory, 1);
        if (allowEmpty && files.Length == 0) return files;
        Require(files.Length == 1 && Path.GetFileName(files[0]) == "track.mft", "Committed media bundle contains missing or extra files.");
        Require(MeasureFile(files[0]) > 0, "Committed media track is empty.");
        return files;
    }

    private static string[] ValidateStage(string directory, bool requireCandidate)
    {
        string[] files = BoundedChildren(directory, 4);
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            Require(name is "intervals.tmp" or "grids.tmp" or "prefixes.tmp" or "candidate.mft" or "track.mft",
                "Media candidate contains an unexpected file.");
            long maximum = name switch
            {
                "intervals.tmp" => MediaAbCapacityV1.MaximumFrames * MediaTrackLayoutV1.IntervalBytes,
                "grids.tmp" => MediaAbCapacityV1.MaximumFrames * MediaTrackLayoutV1.FrameGridBytes,
                "prefixes.tmp" => MediaTrackLayoutV1.GetPrefixCount(MediaSourceKind.Video, MediaAbCapacityV1.MaximumFrames) * MediaTrackLayoutV1.PrefixEntryBytes,
                _ => MediaAbCapacityV1.MaximumArtifactBytes,
            };
            Require(MeasureFile(file) <= maximum, "Media scratch file exceeds its fixed section ceiling.");
        }
        Require(!files.Any(file => Path.GetFileName(file) == "candidate.mft") ||
            !files.Any(file => Path.GetFileName(file) == "track.mft"), "Candidate contains two complete track files.");
        if (requireCandidate)
            Require(files.Any(file => Path.GetFileName(file) == "candidate.mft") && !files.Any(file => Path.GetFileName(file) == "track.mft"),
                "Prepared candidate is missing or was already moved.");
        Require(files.Sum(MeasureFile) <= MediaAbCapacityV1.MaximumScratchAndCandidateBytes,
            "Candidate files exceed the fixed combined byte ceiling.");
        return files;
    }

    private static long MeasureStage(string directory) => ValidateStage(directory, requireCandidate: false).Sum(MeasureFile);

    private void DeleteFile(string path)
    {
        _ = MeasureFile(path); _hooks?.BeforeDeleteFile?.Invoke(Path.GetFileName(path)); File.Delete(path);
    }

    private void DeleteStage(string stage)
    {
        if (!ExistsChecked(stage)) return;
        Require(Directory.Exists(stage), "Media staging slot became a file.");
        foreach (string path in ValidateStage(stage, requireCandidate: false)) DeleteFile(path);
        Directory.Delete(stage);
    }

    private void DeleteBundleDirectory(string path)
    {
        if (!ExistsChecked(path)) return;
        Require(Directory.Exists(path), "Media bundle slot became a file.");
        foreach (string file in BoundedChildren(path, 1))
        {
            Require(Path.GetFileName(file) == "track.mft", "Expired media bundle contains an unexpected file.");
            DeleteFile(file);
        }
        Directory.Delete(path);
    }

    private void ReleaseReservation(Reservation reservation)
    {
        lock (_gate)
        {
            if (_reservations.GetValueOrDefault(reservation.ArtifactId) != reservation) return;
            try
            {
                DeleteStage(reservation.Stage);
                _reservedBytes -= reservation.ReservedByteBudget; _reservations.Remove(reservation.ArtifactId);
            }
            catch (Exception primary)
            {
                RetainOrphan(reservation.Stage, reservation.ReservedByteBudget, primary);
                _reservedBytes -= reservation.ReservedByteBudget; _reservations.Remove(reservation.ArtifactId);
                throw;
            }
        }
    }

    private void ReleaseFailedStage(string stage, long reserved, Exception primary)
    {
        try { DeleteStage(stage); }
        catch (Exception cleanup) { primary.Data["MediaStageCleanupFailure"] = cleanup; RetainOrphan(stage, reserved, primary); }
        _reservedBytes -= reserved;
    }

    private void RetainOrphan(string path, long upperBound, Exception primary)
    {
        long bytes = upperBound;
        try { if (!ExistsChecked(path)) bytes = 0; else if (Directory.Exists(path)) bytes = MeasureStage(path); }
        catch (Exception accounting) { primary.Data["MediaOrphanAccountingFailure"] = accounting; }
        _orphans.Add(path, bytes); _orphanBytes = checked(_orphanBytes + bytes);
    }
}
