# Fixed compressed audio analysis HTTP delta

Run after the fixed product/tool commits and normal isolated publishes. This thin
workflow inherits the accepted analysis/adoption/rendering transport and process
helpers; it does not rerun their large matrices or create another oracle project.

```bash
python3 tools/backend-compressed-analysis-http-probe/probe.py \
  --output artifacts/backend-extraction/b4-compressed-analysis/http/http-final1 \
  --server /absolute/isolated/server/MakoKaraGenerator.Backend.Server \
  --worker /absolute/isolated/worker/MakoKaraGenerator.Backend.Worker \
  --oracle /absolute/isolated/analysis-oracle/RuntimeOracle \
  --runtime-oracle /absolute/isolated/render-oracle/RuntimeOracle \
  --legacy-worker /absolute/owned/0.69/worker/MakoKaraGenerator.Backend.Worker
```

Three encoded short inputs cover CBR mono44.1k MP3, VBR stereo48k MP3 and native
stereo44.1k FLAC. Each complete Worker canonical result is compared byte for byte
with the original Analyzer on the same encoded bytes, including all points and
source/PCM/revision/content hashes. A legacy PCM16 WAV retains its exact duration.
Compressed input duration is omitted; final duration comes from actual48k samples.
Explicit adoption/save/reopen preserve complete canonical data. One adopted FLAC
runs a real PNG comparison (all57,600 premultiplied pixels, zero RGB/alpha error)
and formal ordinary1+transparent1 report. Export and analysis-driven visuals are
outside this batch. The existing render oracle's optional seventh argument only
normalizes the owned declaration after full package load, granting no file IO.

One same-length damaged current-session asset (bound by assetId, not bytes alone) must fail without a registered partial
result through sourceSHA verification; decoder-damage rejection is separately tested
with a complete FLAC header and corrupt encoded frames. Valid complete-frame
truncation can represent a separate short source; not all truncation is detectable.
Only one owned MP3 resource named in a transient marker uses a trusted
FFmpeg launcher that adds `-re`; cancellation observes the actual decoder and
checks its PID/startTicks identity is gone at terminal. All normal input analyses
are unthrottled. Format/decoder capability lists prove tool configuration/startup,
while actual HTTP analyses prove decoding. Three Server runs cover current,
actual old0.69 Worker (analysis disabled), and current restart read-only recovery
of old/new complete results. Every observed private process group must drain.

Credentials remain in memory. Raw HTTP responses, terminal objects, deployment
hashes, source endpoints, original failures and process identities are preserved
under the unique output directory. Do not reuse an output directory or rewrite a
failed run. Source freeze, final deployment/evidence integrity and terminal/raw
response equivalence are independently checked before acceptance. This is not
full-suite, performance, GUI, long-media or platform/manual acceptance.
