# B4-C bounded real audio analysis HTTP probe

The independent oracle reads the same owned WAV bytes with the original
`AudioFeatureAnalyzer`, bridges through `ToSourceData`, and writes the original
complete `AudioAnalysisJson` canonical bytes. It copies no filter, pulse, beat or
hash algorithm. Source/PCM hash, actual48k sample count, short final hop, semantic
revision and every full-precision feature point are compared through those bytes.

Build `RuntimeOracle/RuntimeOracle.csproj` normally under the shared B4 dotnet
lock. Formal HTTP runs require a newly isolated self-contained Linux Server and
an explicitly configured absolute Worker apphost after all authors freeze their
sources. Credentials remain in memory and artifacts are redacted and scanned.

Run from the repository root with fresh output directories and absolute paths:

```sh
flock -o /tmp/mkg-backend-b4-dotnet.lock dotnet publish tools/backend-analysis-http-probe/RuntimeOracle/RuntimeOracle.csproj -r linux-x64 --self-contained false -m:1 -p:UseSharedCompilation=false -o artifacts/backend-b4c-analysis-http/deployment-final1/oracle
python3 tools/backend-analysis-http-probe/probe.py \
  --output "$PWD/artifacts/backend-b4c-analysis-http/http-reproduction" \
  --server "$PWD/artifacts/backend-b4c-analysis-http/deployment-final1/server/MakoKaraGenerator.Backend.Server" \
  --worker "$PWD/artifacts/backend-b4c-analysis-http/deployment-final1/worker/MakoKaraGenerator.Backend.Worker" \
  --oracle "$PWD/artifacts/backend-b4c-analysis-http/deployment-final1/oracle/RuntimeOracle"
```

The first Server uses real, unthrottled `/usr/bin/ffmpeg` for the six successful
canonical comparisons. A second Server uses an owned `0700` launcher that adds
`-re` only to actual decode calls containing `-i`, and passes `-version` directly
to the same FFmpeg. This controlled input pacing makes cancellation, deadline,
native failure and Worker root-death observation reproducible; it does not
establish default production timing. Only recorded private-group PID/start-time
identities are signalled. Both Servers and all observed group members must exit
before success. Launchers and bootstrap capability files are removed on exit.

The accepted run is archived in
`artifacts/backend-b4c-analysis-http/http-final2/manifest.json`: 789 HTTP requests,
4,243 workflow assertions and 1,700 credential scans. Six inputs produce 680
feature points and 651,120 actual mono48k samples, with all 71,287 canonical bytes
equal to the independent oracle. The earlier `http-final1` attempt is excluded:
its proposed line shrink was correctly rejected because an imported child token
ended at the audio boundary. The accepted fixture changes that token within its
existing line instead. No production change was made for the probe.

This is result-only: task completion must not apply an analysis or change the
project's revision, dirty state or history. It does not expand the existing static
rendering profile, execute scripts, claim precise beat recognition, or run a long
audio/performance/GUI acceptance suite. Actual one-hour expiry is tested through
the separate Host clock cases, not by waiting one hour in HTTP.
