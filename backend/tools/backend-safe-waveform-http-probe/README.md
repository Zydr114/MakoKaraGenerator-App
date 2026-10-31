# Backend self-contained waveform HTTP delta

Use isolated, committed Server/Worker deployments and the existing analysis
RuntimeOracle with its optional `waveform` mode. The original two-argument
Analyzer oracle behavior stays unchanged; no new oracle project or HTTP route.

```bash
python3 tools/backend-safe-waveform-http-probe/probe.py \
  --output artifacts/backend-extraction/b4-safe-waveform/http/http-final1 \
  --server /absolute/isolated/server/MakoKaraGenerator.Backend.Server \
  --worker /absolute/isolated/worker/MakoKaraGenerator.Backend.Worker \
  --oracle /absolute/isolated/oracle/RuntimeOracle
```

Six real WAV/MP3/native-FLAC/M4A/ADTS-AAC/Ogg fixtures run
create/save/close/open, including source-rate actual duration and clean/history
state. The oracle uses the original default waveform loader on identical encoded
bytes and records complete info, actual sample count and full pyramid hash.
HTTP currently exposes duration, not waveform peaks; full-pyramid compatibility
is proved separately by author regressions, not claimed from HTTP summaries.

A malicious ffconcat upload tagged audio/wav and a fresh current-version
Server-saved package with exact updated media hash/size must fail. The referenced
sentinel is an owned same-client resource-directory neighbour only. Both its
presence and absence are tested; native descriptors must not read it. Failed
open jobs/events may remain, while candidate pins and partial sessions must not.
No claims about arbitrary paths or other clients are inferred from this fixture.

Owned PATH wrappers pass all normal calls to the real /usr/bin tools unchanged.
Only one marker-controlled WAV open adds `-re` for actual decoder observation and
explicit open-job cancellation. Direct Server native PID/startTicks identities
are recorded separately from inherited Worker private-group observations; exact
native exit, no partial publication and same-handle retry are checked. Create
HTTP disconnect cancellation, new deadlines and preparation in a Worker are
outside this batch.

Every mutation has a distinct client-level receipt ID. Reopened declaration
paths are distinct from imported audio grants; this probe does not invent a
source-SHA comparison by equating them. Original old0.70 defect reproduction is
archived separately and its counts are not added to this acceptance. Preserve
all original failures/raw responses and use new immutable integrity/index files.
Credentials stay in memory. No GUI, screenshots, full suite, performance, long
media or manual platform acceptance is included.
