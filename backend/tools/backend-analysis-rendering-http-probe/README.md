# B4-E complete analysis static-source HTTP probe

This thin workflow inherits the accepted B4-D transport, credential redaction,
owned-process tracking, original analyzer oracle, save/open, and static-output
checks. It does not repeat the B4-D rejection matrix. Current null-analysis and
non-null-analysis sources must select their respective fixed profiles.

Publish a fresh self-contained linux-x64 Server, Worker, the existing analysis
oracle and the existing preview Runtime oracle under the shared dotnet lock.
Use normal restore/analyzers and `-m:1 -p:UseSharedCompilation=false`.

```sh
python3 tools/backend-analysis-rendering-http-probe/probe.py \
  --output "$PWD/artifacts/backend-extraction/b4-analysis-static-profile/http/http-final1" \
  --server "$PWD/artifacts/backend-extraction/b4-analysis-static-profile/http/deployment-final1/server/MakoKaraGenerator.Backend.Server" \
  --worker "$PWD/artifacts/backend-extraction/b4-analysis-static-profile/http/deployment-final1/worker/MakoKaraGenerator.Backend.Worker" \
  --oracle "$PWD/artifacts/backend-extraction/b4-analysis-static-profile/http/deployment-final1/analysis-oracle/RuntimeOracle" \
  --runtime-oracle "$PWD/artifacts/backend-extraction/b4-analysis-static-profile/http/deployment-final1/render-oracle/RuntimeOracle" \
  --legacy-worker "$PWD/artifacts/backend-extraction/b4-analysis-apply/http/deployment-final1/worker/MakoKaraGenerator.Backend.Worker"
```

The existing independent Runtime oracle keeps its six-argument behavior. Its optional
seventh argument loads a complete owned package and
owned-resource argument only normalizes the source's audio path declaration to
the same declaration captured by HTTP; it performs no IO through that argument.
Only this normalized complete codec SHA is compared with the HTTP source SHA.
The original package-path SHA and the separate authorized audio grant are not
conflated. Complete analysis canonical bytes are also checked independently.

Real Worker output covers a transparent PNG with exact premultiplied RGB/alpha,
one ordinary plus one transparent validation frame, and three one-second,
320x180, 30-frame clips: pure MP4, MP4 with audio from owned PCM16 WAV, and pure
transparent MOV. Every clip fully decodes and is compared with the original
Runtime using stated lossy codec tolerances. This preserves analysis as input;
effects remain disabled, so analysis does not drive static visuals.

Only the owned audio resource is temporarily renamed to prove pure-video and
static rendering do not read it; audio inclusion must then fail explicitly.
Clearing the live project must leave an already captured preview input fixed.
Both source profiles retain reports across restart. A short intermediate Server
using the owned old 0.68 Worker must disable export instead of claiming a new
profile. All three Servers and observed private groups must exit normally.
Unknown profile, canonical and total-budget corruption have separate controlled
codec/Host tests. No GUI, screenshots, full suite, performance, long media or new
effects/analysis algorithm is part of this acceptance.
