# Package no-audio export HTTP probe

This probe runs a fresh authenticated loopback HTTP acceptance against a
matched self-contained Server/Worker deployment. FixtureOracle generates a
real `ProjectStore` package project under the probe-owned authorized root.

The 2026-10-22 fresh HTTP run passed 457 requests and 2575 assertions against
the matched deployment from source `4e0f909` (product 0.98.1, contract 1.32.0).
See [acceptance §76](../../docs/archive/history/backend-extraction-progress.md#b4-p2-package-no-audio-export-http-0981)
for the deployment/evidence manifests and finite acceptance boundary. The probe
definition and that run's evidence remain separately recorded.

```bash
python3 tools/backend-package-export-http-probe/probe.py \
  --dist /absolute/matched-dist \
  --oracle /absolute/FixtureOracle.dll \
  --output /absolute/new-evidence-directory
```

The optional historical rejection matrix requires a separate matched old
deployment; it never treats the current package-enabled Worker as an old one:

```bash
python3 tools/backend-package-export-http-probe/probe.py \
  --dist /absolute/current-dist \
  --old-dist /absolute/old-dist \
  --old-worker \
  --oracle /absolute/FixtureOracle.dll \
  --output /absolute/new-old-worker-evidence-directory
```

The finite slice covers `package-lyrics-scene-v1` with `includeAudio:false`:

- opaque MP4 and transparent MOV short exports (21 frames each in that run);
- independent ffprobe frame count/no-audio checks and full-frame ffmpeg BGRA
  length/frame count/alpha checks; no exported-video comparison with a Runtime
  content oracle or claim of complete pixel equivalence;
- immutable report and media bytes, size and SHA, complete diagnostics, SSE;
- authenticated ACL, anonymous/foreign-client rejection, ETag/304, Range and HEAD;
- stable `includeAudio:true` rejection;
- actual Worker cancellation, deadline, project close `reject`/`cancelAndWait`;
- previous-host orderly restart recovery and no automatic task replay;
- observed Worker identities and credential scan.

The long control fixture is a probe-owned copy of the package project with only
the persisted output duration changed to ten seconds. It exists to make the
real Worker control paths observable; it does not expand the accepted package
input boundary. This probe does not claim package audio export, external
image/video/font closure, preset+package, package analysis/measurement, complete
B4/B5/B6, browser acceptance or performance/GPU completion.
