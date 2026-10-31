# Package validation HTTP probe

`probe.py` exercises the real loopback Server and owned Worker for the
`package-lyrics-scene-v1` validation profile. It generates probe-owned package
fixtures with the existing `FixtureOracle`, opens one through the public file
handle/project-session API, and submits a real validation job. A matched Worker
must return a real immutable report and complete diagnostics page; an old or
mismatched Worker can be checked with `--expect-package no` and must withhold
the profile and reject package validation.

The probe uses a fresh self-contained deployment and evidence directory:

```bash
python3 tools/backend-package-validation-http-probe/probe.py \
  --output artifacts/package-validation-http-<run> \
  --server <dist>/server/MakoKaraGenerator.Backend.Server \
  --worker <dist>/worker/MakoKaraGenerator.Backend.Worker \
  --oracle tools/backend-public-package-preview-probe/FixtureOracle/bin/Release/net10.0/FixtureOracle.dll
```

The `--worker` value must be the executable apphost in the published Worker
directory. Do not replace it with a shell launcher: the Server resolves the
adjacent `script-refs` directory to compute the Worker identity and capability
handshake.

The matching-worker matrix also runs the oracle's compile failure, runtime
Error, image/material warning, and report-budget fixtures. The image/material
fixtures use a declared `BackgroundColor` input while retaining the package's
media background requirement: a fixed four-frame plan completes four opaque
and four transparent frames, and the transparent output retains the structured
`TransparentBackgroundDependency` warning. Each warning issue is checked as a
non-error with its `imageEffect` or `material` union branch, exclusive union
shape, and `inputId=background`. This does not claim an external media or font
closure.

The primary `package-validation-scale` success case uses the same fixed
four-frame plan for each output. The probe checks persisted source/profile/
revision, 320x180 geometry, 30 fps timing, immutable report bytes/SHA, and
the complete diagnostics page. This closes the public primary success path
only and does not claim complete B4-P1.

The manifest records HTTP assertions, successful report/diagnostic checks, terminal failure
codes, observed Worker identities and scope. Rejected requests do not fabricate a package
result. The current finite slice does not claim package export,
analysis, preset/media/font closure, browser coverage, or complete B3/B4.

The matching matrix also checks active cancellation, the configured 8-second
validation deadline, project close (`reject` followed by `cancelAndWait`), and
one orderly previous-host restart/recovery cycle and one abrupt crash/restart/recovery cycle.  The orderly recovery cycle keeps a completed
package report readable only through `/api/v1/recovery-jobs/{id}/validation-result`,
persists an active task as cancelled, and verifies that no package task is
automatically replayed after the new Server starts. A separate abrupt crash
cycle sends SIGKILL to the Server (recorded as exit -9): the next host retains
the successful report, rejects the old host credential, and converts the
active task to `unknown` with `internalError`, `canCancel=false`, and no report.
This follows `BackendJobLedger` crash semantics; crash is distinct from
orderly shutdown cancellation.
It uses the existing 700ms/30fps `scale` fixture with a legal 22-frame plan for
each output. The probe pauses a PID plus start-tick verified owned Worker only
after observing its job running. This proves job/owned Worker control; it does
not prove that an unbounded script started executing. The current fresh matrix
requires exactly `WorkerTaskTimedOut`, with `cancellationRequested=false`.
Cancelled/timed-out/closed jobs must have no report, and the authenticated
report route must return HTTP404 `jobNotFound`.

Process evidence records each observed descendant's PID, start ticks, original
parent identity and command hash. At the first terminal observation for each
controlled validation job, its Worker and observed descendants must already
be absent. Clean shutdown must return exit0 with no observed descendants.
Forced cleanup and survivors before cleanup are recorded separately and always
make the run fail. `/proc` polling proves only identities actually observed;
short-lived processes can escape observation.

Provenance includes the probe and shared helper hashes, Server/Worker apphost
hashes, Oracle assembly hash, repository commit/dirty state, and the adjacent
`deployment.json` hash/source commit/dirty fields when present. An apphost hash
is not an assembly dependency closure; use the hashed deployment manifest for
that comparison. The Oracle hash similarly identifies its assembly, not all of
its dependencies. Evidence files are checked for the in-memory credentials.
The probe covers one orderly and one abrupt package recovery cycle (G0→G1→G2). It does
not cover multiple crash generations, recovery during arbitrary Worker phases,
report corruption during restart, real runaway scripts, or the three static
execution profiles. Earlier manifests without restart/recovery and identity/
drain fields keep their original narrower evidence boundaries.
