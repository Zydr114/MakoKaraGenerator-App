# Fixed compressed audio export HTTP delta

This thin B4-H workflow inherits the accepted export transport, full-frame original
Runtime oracle, private-process identity capture and artifact download checks. It
does not rerun the original large export matrix or create another oracle project.

```bash
python3 tools/backend-compressed-export-http-probe/probe.py \
  --output artifacts/backend-extraction/b4-compressed-export/http/http-final1 \
  --server /absolute/isolated/server/MakoKaraGenerator.Backend.Server \
  --worker /absolute/isolated/worker/MakoKaraGenerator.Backend.Worker \
  --oracle /absolute/isolated/export-oracle/RuntimeOracle \
  --analysis-oracle /absolute/isolated/analysis-oracle/RuntimeOracle \
  --legacy-worker /absolute/owned/0.70.1/worker/MakoKaraGenerator.Backend.Worker
```

CBR mono44.1k MP3, VBR stereo48k MP3 and native stereo48k FLAC each generate a
three-second ordinary MP4; FLAC also generates a transparent MOV. Legacy PCM16 WAV
retains its omitted wire markers. Every normal clip compares all90 complete
decoded frames against the original Runtime, including meaningful timed lyrics,
ruby and a countdown scene. FLAC uses30000/1001 fps and exactly144144 source
sample frames for its3.003-second derived plan. FLAC is explicitly analyzed and adopted, saved and
reopened before export, covering the second complete-analysis static profile.
The complete analysis canonical is compared to the original Analyzer on exactly
the same encoded FLAC bytes.

Complete output audio is compared per channel to independently decoded original
encoded source bytes at their actual native rate. Source duration is checked
against that complete decoded sample count. AAC uses explicit lossy correlation,
RMS and error bounds; transparent PCM is bitexact for the planned movie interval.
No MP3 comparison to preencoding PCM is called lossless.

A real short MP3 and a FLAC source exactly one sample frame short fail by actual
decoded coverage without a registered artifact. A native8000 Hz FLAC with exactly
300 seconds plus one sample frame exercises the actual compressed-source budget.
Source identity corruption after admission also fails without a partial result;
bad FLAC encoded frames and same-client owned ffconcat references are rejected by
default preparation without publishing a half session. Worker tests separately
exercise bad frames past the export admission seam and dishonest probe duration.
Only one exact owned MP3 source uses a trusted launcher adding `-re` for the
after-admission source-change and active-cancellation cases. They observe strict
decoder arguments and prove its PID/startTicks identity
is gone at the authoritative terminal. All normal exports remain unthrottled.
After cancellation an unthrottled successful retry binds the same resource
handle/hash/size/profile and repeats the full video and audio comparison.
Explicit pure-video export with adopted analysis succeeds with its compressed
declaration file missing and has all90 decoded frames with no audio track or
audio grant. Original authenticated download checks
are reused. An actual old0.70.1 Worker disables export, then current restart
recovery checks entire old/new terminal objects and original report/media bytes.

Raw HTTP responses, terminal objects, process identities, full decoded source and
output audio, comparison metadata, original failures and the manifest are kept in
the unique output directory. Never reuse or rewrite a failed run. Final source,
deployment and evidence hashes are independently checked after actual process
exit. This is not full-suite, performance, GUI, long-media or manual platform
acceptance.
