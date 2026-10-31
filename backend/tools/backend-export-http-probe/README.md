# B4-B bounded actual export HTTP probe

This tool owns its inputs, resource/state roots, bootstrap credential, Worker
launcher, Servers and observed private Worker groups. Credentials stay in memory;
HTTP artifacts are redacted and scanned before the final manifest. It never
opens a user project or native window.

Build `RuntimeOracle/RuntimeOracle.csproj` normally under the shared B4 dotnet
lock. Use an explicitly published self-contained Server and an explicit absolute
Worker apphost. The Python tool requires NumPy for bounded frame-at-a-time pixel
comparisons. Example arguments:

```text
python3 tools/backend-export-http-probe/probe.py \
  --output /absolute/new-owned-artifact-directory \
  --server /absolute/published/MakoKaraGenerator.Backend.Server \
  --worker /absolute/published/MakoKaraGenerator.Backend.Worker \
  --oracle /absolute/RuntimeOracle/bin/Debug/net10.0/RuntimeOracle
```

The oracle loads the same authorized pinned project independently through
`ProjectStore` and renders the complete original `CompositionFramePlan` using
Runtime.Hosting Final sessions. It also removes ruby and hides the countdown
scene separately to confirm both contribute actual pixels. The HTTP probe decodes
the complete ordinary/transparent films and audio, compares all original frames,
and records lossy RGB error and glyph/alpha coverage; it does not claim lossless
H.264 or ProRes RGB output. Input is real PCM16 WAV; ordinary MP4 retains original
AAC output encoding and transparent MOV retains PCM output encoding.

After the HTTP process exits, run:

```text
python3 tools/backend-export-http-probe/verify_audio.py /absolute/owned-output-directory
```

This performs
an independent complete stereo decode of those same three films. Both source
channels carry different tones; MOV samples must match the full PCM source
exactly, while AAC uses explicit correlation, level and error bounds. Its
29 assertions are recorded separately and do not count toward the original
HTTP/credential scan assertions.

Controlled cancellation/deadline/process failures apply only to exact observed
owned PID/startTicks/private-group identities. Successful static diagnostics are
empty; transport/native failures are not invented Runtime occurrences. The
one-hour artifact TTL is checked as returned metadata, while actual expiry and
active-read accounting are covered by the separately recorded Host TimeProvider
tests. The C# child guard test supplies the actual RLIMIT_FSIZE hard-limit proof;
HTTP exercises published limits and reservation/input rejection.

Only a final successful `manifest.json`, its exact deployment/source/evidence
hash manifest, and original process exit may be counted as acceptance. Earlier
fixture or tool failures remain separate historical evidence.
