# Managed Worker waveform preparation HTTP delta

This B4-I probe reuses the accepted authenticated HTTP helpers, six-family
waveform workflow and original default waveform loader oracle. It adds no HTTP
route or oracle project. Run only after the product and tool sources have been
committed and the isolated deployments have been frozen by the root agent.

```bash
python3 tools/backend-worker-prepare-http-probe/probe.py \
  --output artifacts/backend-extraction/b4-worker-prepare/http/http-final1 \
  --server /absolute/isolated/server/MakoKaraGenerator.Backend.Server \
  --worker /absolute/isolated/worker/MakoKaraGenerator.Backend.Worker \
  --oracle /absolute/previously-published/waveform-oracle/RuntimeOracle \
  --legacy-worker /absolute/owned/0.71.0/worker/MakoKaraGenerator.Backend.Worker
```

Six real WAV/MP3/native-FLAC/M4A/ADTS-AAC/Ogg fixtures execute
create/save/close/open. Six additional actual unsaved sessions retain those
identical sources through normal shutdown autosaves. A new Server host reads
their actual recovery markers and runs each existing recover job, checking the
original duration, complete encoded bytes, dirty state and fresh history before
closing the recovered session. Successful recovery consumes the original marker.
The existing waveform oracle loads identical encoded
bytes and records the original AudioStreamInfo, native sample rate, sample
count and complete pyramid hash. HTTP exposes duration and session state;
complete waveform transport equality is proved separately by actual Worker
regressions. The oracle must be the previously published pre-B4-I Media
deployment, with its original deployment SHA and assembly version verified;
comparing the new Worker with a newly rebuilt shared Media helper does not
establish original algorithm compatibility. This probe checks every oracle file
against the archived B4-G deployment manifest and every old Worker file against
the final B4-H HTTP deployment manifest. Existing same-client owned ffconcat references remain rejected
with their sentinel present and absent, including a current-version saved
package with accurate media hash and size.

Absolute trusted FFmpeg/FFprobe launchers pass normal native arguments to
`/usr/bin` unchanged. A marker adds `-re` only to owned waveform decodes used
for lifecycle controls. The probe observes the actual native decoder inside
its Worker private process group. Active open cancellation, deadline with the
observed Worker and native decoder stopped, and Worker death must drain all
observed PID/startTicks identities before their authoritative terminal. Failed
candidates publish no partial session, release their pinned closure, and retry
successfully through the same directory handle. The Server stays healthy.
A synchronous create deadline separately returns the stable public timeout
code after full group drainage, then retries with the same uploaded audio.
A blocked active preparation also exercises queued open cancellation, four
queued synchronous create deadlines and the next create's HTTP429 quota
rejection. Queued operations launch no decoder. A final stopped active open and
an observed candidate queued behind it are cancelled by normal Server shutdown,
with both original terminal ledgers preserved and no queued decoder launch.

A synchronous create body is sent through a real HTTP connection. The
connection is closed after its actual Worker decode begins and before reading
the response. A retry with the same receipt must return the one accepted
session. This confirms the existing accepted-operation behavior; disconnecting
is not cancellation. An actual old0.71 Worker disables preparation, create
returns unavailable, and an accepted open job fails unavailable without a Host
native decode fallback. The current Worker then reopens the same saved package.

Each run uses a new output directory and preserves complete raw responses,
whole terminal objects, oracle records, process identities and source,
deployment and evidence SHA manifests. Never reuse or overwrite a failed run.
Credentials remain in memory and every evidence artifact is scanned. The scope
excludes GUI, screenshots, the full suite, performance, long media and manual
platform acceptance; older batch counters are not added to this delta.
MOV external references, corrupt/NoStream inputs, complete payload/base bucket
budget rejection and existing analysis/export compatibility belong to the
separate formal Worker/Media/Hosting regressions. This delta does not claim
those cases as direct HTTP coverage or add rendering/movie acceptance.
