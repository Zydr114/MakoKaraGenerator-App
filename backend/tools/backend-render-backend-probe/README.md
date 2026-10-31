# Explicit render backend HTTP probe

This probe starts the matched self-contained Linux x64 Server/Worker deployment
on a random loopback port and uses authenticated public HTTP requests. Each run
requires a new output directory; failed evidence is retained.

```bash
python tools/backend-render-backend-probe/host.py publish --dist NEW_DIST
python tools/backend-render-backend-probe/probe.py --dist NEW_DIST --output NEW_HTTP
python tools/backend-render-backend-probe/probe.py --dist NEW_DIST --output NEW_UNAVAILABLE_HTTP --unavailable
```

`host.py` delegates publication, inventory verification, origin handling and
cooperative shutdown to `backend-web-host/host.py`. All dotnet publication and
fixture execution uses `/tmp/mkg-gpu-render-dotnet.lock`. Build the original
fixture generator before the positive run, or supply `--oracle`:

```bash
flock -o /tmp/mkg-gpu-render-dotnet.lock dotnet build tools/backend-project-image-preview-probe/FixtureOracle/FixtureOracle.csproj -c Release -m:1 /p:UseSharedCompilation=false
```

The positive run pins a hardware Vulkan ICD (`--icd`, default RADV), starts with
CPU as the deployment default, and checks:

- Real availability and the public device summary.
- Project, authorDraft, template, presetCandidate and editCandidate preview
  CPU → GPU → CPU switches, complete PNG transport/SHA/readback, generation and
  fixed source retention, same-backend no-op, stale generation and client ACL.
- Two live clients retaining different backend choices.
- Explicit CPU/GPU project measurement and pinned candidate measurement defaults.
- GPU project/author validation and short no-audio export, fixed task input and
  report backend, report bytes/SHA, complete exported video decoding.

The unavailable run sets both `VK_DRIVER_FILES` and `VK_ICD_FILENAMES` to a
nonexistent file in the fresh output directory. It requires GPU to be unavailable,
an explicit GPU switch/create to reject, and the original CPU generation, frame,
diagnostics, PNG handle and later CPU rendering to survive.

Hardware evidence samples only this launcher's published Worker descendants,
records PID/start ticks, retains two whitelisted selector environment values,
and checks the mapped Vulkan loader, pinned hardware driver and DRM render fd.
The probe records normal Server/launcher shutdown, all observed identity drain,
credential scans and a final evidence file inventory. It never kills processes
by name or prints credentials.

This is a finite local HTTP/device acceptance tool. CPU-return pixel consistency
does not assert CPU/GPU pixel equality. PNG/video decoding is not an independent
Runtime content oracle. Browser, cross-platform, GPU encoding and performance
acceptance remain separate.
