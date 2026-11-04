using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;

if (args.Length != 2) throw new ArgumentException("backend-root output-snapshot-json");
string backend = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
var fixture = await Fixtures.CreateAsync(backend, "csharp", "none", false, null);
var snapshot = fixture.Snapshot;
if (snapshot.Background != BackgroundSource.Color) throw new InvalidOperationException("Acceptance fixture requires original Color background.");
byte[] bytes = ProjectSnapshotSourceProfile.Encode(snapshot);
var decoded = ProjectSnapshotSourceProfile.Decode(bytes);
if (!ProjectSnapshotSourceProfile.Encode(decoded).AsSpan().SequenceEqual(bytes)) throw new InvalidOperationException("Snapshot round trip changed bytes.");
File.WriteAllBytes(output, bytes);
string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
File.WriteAllText(output + ".provenance.json", JsonSerializer.Serialize(new {
    scope = "Existing backend-render-performance Fixtures.CreateAsync csharp builder used unchanged with original repository WordEmphasis package. New current-version Color snapshot; no compile, render, GPU device, or HTTP process started. M03 saved snapshot was not reused because production correctly rejects its 0.110.6 version.",
    builder = Path.Combine(backend, "tools/backend-render-performance/Runner/Fixtures.cs"),
    builderSha256 = Hash(File.ReadAllBytes(Path.Combine(backend, "tools/backend-render-performance/Runner/Fixtures.cs"))),
    fixture.Description, fixture.Sha256, snapshotSha256 = Hash(bytes), snapshotBytes = bytes.Length,
    profile = ProjectSnapshotSourceProfile.Select(snapshot), snapshot.Name, snapshot.EffectInputsFingerprint,
    packages = snapshot.Effects.Packages.Select(p => new { p.PackageId, p.Revision, files = p.Files.Select(f => new { f.Path, f.Role, f.Sha256 }) }),
    instanceCount = snapshot.Effects.Instances.Length, presetApplications = snapshot.PresetApplications.Items.Length
}, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine(JsonSerializer.Serialize(new { output, bytes = bytes.Length, sha256 = Hash(bytes), profile = ProjectSnapshotSourceProfile.Select(snapshot), packages = snapshot.Effects.Packages.Length }));
