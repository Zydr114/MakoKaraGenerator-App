# B4-D explicit analysis adoption real HTTP probe

This probe creates only owned loopback Server processes, client credentials,
resources and project packages. It reuses the B4-C transport/process helpers and
original `AudioFeatureAnalyzer` oracle, but runs a separate B4-D workflow and
records independent counts. No old manifest contributes acceptance counts.

The product source, probe source and deployment must be frozen before the formal
run. Publish the Server as linux-x64 self-contained, the Worker normally, the
analysis oracle from `tools/backend-analysis-http-probe/RuntimeOracle`, and the
render oracle from `tools/backend-preview-http-probe/RuntimeOracle`. Serialize
all dotnet operations with `flock -o /tmp/mkg-backend-b4-dotnet.lock`, normal
restore/analyzers, `-m:1 -p:UseSharedCompilation=false`.

```sh
python3 tools/backend-analysis-apply-http-probe/probe.py \
  --output "$PWD/artifacts/backend-b4d-analysis-apply-http/http-final1" \
  --server "$PWD/artifacts/backend-b4d-analysis-apply-http/deployment-final1/server/MakoKaraGenerator.Backend.Server" \
  --worker "$PWD/artifacts/backend-b4d-analysis-apply-http/deployment-final1/worker/MakoKaraGenerator.Backend.Worker" \
  --oracle "$PWD/artifacts/backend-b4d-analysis-apply-http/deployment-final1/analysis-oracle/RuntimeOracle" \
  --runtime-oracle "$PWD/artifacts/backend-b4d-analysis-apply-http/deployment-final1/render-oracle/RuntimeOracle"
```

The complete real analysis result is compared byte for byte with the original
analyzer. Explicit adoption uses an exact current revision after a lyric timing
edit, then checks summary, one history entry, undo/redo, repeated-adoption no-op,
empty-clear no-op preserving redo, receipts, authenticated ownership, other
session rejection, real result/audio file corruption and read-only rejection.
Saved/reopened packages must keep the full canonical analysis independently of
the retained job sidecar. Previous-host recovery remains read-only.

The static profile remains unchanged: adopted analysis explicitly blocks
preview, formal validation and export. Clearing restores a real PNG that is
compared with the independent original Runtime, a successful validation report,
and one 320x180, one-second, 30-frame pure-video MP4. The full MP4 is decoded;
its first frame is compared against the original Runtime with a bounded lossy
codec tolerance. This is a static-capability restoration check, not a new
analysis rendering profile or broad movie acceptance.

Default analysis uses unthrottled `/usr/bin/ffmpeg`. An owned Worker may be
briefly stopped to make a queued cancellation observable; no unrelated process
is signalled. All observed private-group PID/start-time identities and both
Servers must be gone before success. Credentials stay in memory and every
artifact is scanned. Actual one-hour expiry and injected cleanup failures belong
to separate controlled Hosting tests, not this real-time HTTP run. No GUI,
screenshots, long audio, long video or performance suite is run.
