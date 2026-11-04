# GPU Worker pool real acceptance tools

The console harness is a real Host/Proxy integration probe, not a mock test. It owns
all Worker processes it launches. It must run after M02/M04 source integration and a
matched publish. The default command prints help and exits before touching a device.
The explicit `--run-real-gpu` switch is intended only for a parent-assigned exclusive
GPU window. Execution results live in separate private evidence directories; this document describes the reproducible tool boundary.

## Build and stage (outside formal performance windows)

1. Build the integrated App and publish matched Server/Worker using the normal App
   build. Preserve deployment.json, script-refs/net10.0 and native files together.
2. Build only the tool entry points against that exact immutable dist, outside the
   timed device window. Do not rebuild SDK/runtime/Host through ProjectReference:

   ```bash
   python3 backend/tools/backend-gpu-worker-pool-acceptance/build_driver.py \
     --dist .local/backend-dist/<matched-M04-publish> \
     --output .local/m04-acceptance/tool-round-1
   ```

   build_driver.py validates clean linux-x64 self-contained deployment hashes,
   compiler absence in Worker, byte-identical shared server/worker assemblies and
   exact copied DLL hashes. It compiles Program.cs/DualConsumerDriver.cs/MalformedWorkerProbe.cs only using
   direct AssemblyReferences, writes tool files outside dist and records all source,
   project, command and reference bytes in input-manifest.json. Using a separately
   built Debug SDK against Release Worker would change strict SDK identity and is
   prohibited for acceptance.

3. Prepare an exact-byte isolated copy with a new output path:

   ```bash
   python3 backend/tools/backend-gpu-worker-pool-acceptance/prepare.py \
     --deployment .local/backend-dist/<matched-M04-publish> \
     --output .local/m04-acceptance/round-1
   ```

   This uses reflink or ordinary copy, never hardlinks, and compares every file hash.
   The probe temporarily appends one newline to **only that copied** deployment.json
   to test configuration invalidation and restores its original bytes in `finally`.
   It refuses any Worker path outside `<output>/dist/`. Active deployment files and
   primary profiles are never modified.

## Exclusive device run

```bash
.local/m04-acceptance/tool-round-1/publish/MakoKaraGenerator.Backend.Hosting.Tests \
  --run-real-gpu \
  --worker .local/m04-acceptance/round-1/dist/worker/MakoKaraGenerator.Backend.Worker.dll \
  --output .local/m04-acceptance/round-1 \
  --rapid-count 8
```

Optional `--snapshot <file>` supplies bytes accepted by ProjectSnapshotSourceProfile.
The supplied snapshot must have Color background; prepared package grants are created
by the real BackendProjectRuntimeInputLease. Default input has no author script, so a
default pass does not establish script execution acceptance. For final M04 use the
same real scripted snapshot as M01/M02, and archive only its content hash if source is
private. Compilation uses the real isolated Worker's reference directory and a new
Host compiler artifact directory under the output. No credentials or profile are used.

The ten-minute cancellation deadline covers these ordered checks:

1. Immediately admit the constructor's initial real opening process and record its
   exact lock-protected opening-wait/miss classification, then close it. Wait for full
   spare readiness; capture actual Worker proof with runtime
   implementation assembly names/MVID, retained reference byte count, initialization
   pair ID, one actual VkDevice creation and one actual GRContext creation.
2. Bind spare to fixed input and allocate a real GPU image pool. Compare returned
   WarmupBinding against the original preparation and exact PID/startTicks/group.
3. Open second real pool; assert two active/zero spare and total two. Query availability
   using the existing active Worker; compare process set before/after. Third admission
   must reject. No third helper is permitted.
4. Close second pool, await exact original process termination, observe one active plus
   one new spare. A structurally invalid snapshot is rejected by Host validation, then its acquired single-use lease drains. This does not cover Worker Bind-before-decode malformed payload execution.
5. Eight rapid new input generations, closing each after actual pool open. Capture
   exact locked admission hit/opening-wait/cold-reservation, elapsed open time and unique original process
   identity. These observations may show only hits; they do not guarantee a cold miss.
   Exhaustion is separately proven by step 3. Record actual observed miss count; never
   relabel an opening wait or rejection as a warm hit. The pre-admission pool snapshot
   is contextual; `admission` comes from the actual pool lock.
6. Close the remaining unconnected pool before its 30-second consumer connection
   deadline. Then let the unused spare exceed its real 30-second TTL. Require a different
   exact spare identity and original process exit. Mutate the isolated deployment
   manifest, query availability, and prove old unused preparation is drained. Restore
   exact manifest bytes and query again, proving a second transition.
7. Wait for spare, dispose pool and inspect every observed
   original group member PID/startTicks. All observed original identities must be gone.

Private `events.jsonl` records stopwatch ticks/frequency, UTC for correlation, pool slot
states and preparation proofs. A 100 ms observer captures actual process-group members
as well as opening PIDs. Opening/retiring/quarantined slots all remain in the maximum
two budget. A successful proxy stop additionally confirms its owned group/tree drain;
the observer is not an exhaustive scheduler-atomic process inventory.

This harness connects no consumer. Real pool open and producer close before consumer
attachment prove device/object reuse and host ownership only. They cannot prove
consumer completion, frame presentation, external leases, no stale draw or UI latency.

## Real two-consumer desktop gate (required)

The additional `dual-main.cjs` is a standalone acceptance Electron main. It creates
two independent sandbox/context-isolated webContents, each with the real production
preload and native broker. `adapter-factory.cjs` reads the current production main
and executes its **unchanged** claim/connect/import/transfer/native completion ACK/
control gate functions in separate closures; an exact-sender IPC router dispatches
each webContents to its own closure. Production startup/profile/menu/UI code is
excluded. Source anchors fail closed if main is refactored. Adapter source/preload/
broker/Worker/driver hashes are recorded.

The scratch stdio driver uses the real GpuWorkerPool, GpuStreamWorkerSession and
GpuPreviewStreamRegistry. Its two clients and two consumers must obey the real
registry's one-per-client, one-per-consumer and two-per-host admission. A private
authenticated temporary loopback claim route delegates one-time exact-generation
claims to that registry; capability responses pass only to the same Electron main.
This is a trusted acceptance composition root, not the product's single-consumer
HTTP/desktop configuration. It does not loosen production authentication or change UX.

Prepare another fresh exact-byte copy using prepare.py, then run **in an assigned
device window** (use the actual pinned Electron executable from desktop/node_modules):

```bash
desktop/node_modules/electron/dist/electron \
  backend/tools/backend-gpu-worker-pool-acceptance/dual-main.cjs --run-real-gpu \
  --root /mnt/Data/Program/MakoKaraGenerator-App \
  --output .local/m04-acceptance/dual-round-1 \
  --broker .local/backend-dist/<matched-M04-publish>/desktop/gpu_preview_broker.node \
  --driver .local/m04-acceptance/tool-round-1/publish/MakoKaraGenerator.Backend.Hosting.Tests.dll
```

Optional `--snapshot` has the same scripted Color-background input semantics as the
console probe. Both windows must remain visible. A standalone temporary page drives
playing state at exactly 500 ms; it uses the production preload's renderer clock and
texture receiver. No screenshots or CPU readbacks are requested.

The gate waits for one native-final-release **and drawn** frame matching all six
attachment identities on each surface, with original device/context bind proof
count=1. It then checks two bound/zero spare, availability with unchanged exact Worker
set, gracefully detaches the second consumer, closes its Worker via the registry,
waits for one-bound/one-ready-spare within the existing two slots and requires a new
exact completed drawn frame on the first unchanged attachment after that close.
Both consumers detach gracefully before final Worker/pool shutdown. Observer logs
include Worker preparation/slot states and actual child PID/startTicks; final drain
requires original observed identities gone. `dual-events.jsonl` contains no handshake,
consumer secret or broker endpoint. This gate establishes actual two-consumer flow;
the no-consumer console probe alone never substitutes for it.

CPU-only validation is `node --test adapter-factory.test.cjs`, Node syntax checks and
the scratch compile-check project build. ProjectReference M04.Acceptance.csproj is
development-only; final execution uses build_driver.py fixed dist dependencies.
These only verify extraction/routing/compilation; real device acceptance requires a separate successful explicit GPU command.

## Desktop finite evidence and M00 comparison

After the Host/Proxy lifecycle probe, run existing desktop finite content acceptance in
its own device window against the same matched M04 build:

```bash
node scripts/gpu-preview-p3.mjs --finite --provider runtime \
  --output .local/m04-acceptance/desktop-round-1 \
  --session-name m04-finite-r1
```

It retains the existing 500 ms control path and records exact streamId, hostInstanceId,
producerEpoch, inputGeneration, consumerEpoch and poolGeneration for every native
completed drawn frame. This separately covers actual consumer draw/release, repeated
pause/seek/latest input, resize, attach/detach and owned App process shutdown. Its
finite result is correctness evidence, not formal performance acceptance.

The frozen M00 entry is `.local/m00-desktop-baseline` at commit
`0969972c71efa6c8e78df818baa8a03b2dedc0c6`, original matched deployment
`0be255d3197d52286341cd62080f352b317474b5` version 0.110.2 plus explicitly documented
paused-barrier fix. The already completed finite r3 evidence is under its
`.local/script-sdk-m00-20261103/gpu-finite-r3`. That run proves finite correctness only.

The parent schedules three paired A/B rounds with source/deployment/runner/preload
hashes fixed, identical script/input, resolution, playback and pause/seek actions.
Use the existing finite runner for correctness and separate timed protocol for timing;
do not compare console pool-open stopwatch values to desktop request→matched native
completion latency. Report initial readiness, observed admission hit/miss, business
bind+pool open and old process drain independently. Resource peaks must include the
unused spare. Pair medians/p95 and raw samples by round; no formal threshold claim is
made by this prepared runbook or by pure tests.

## Current-version WordEmphasis fixture

`build_fixture.py --dist <matched-dist> --output <new-App-.local-directory>` builds only ExportFixture.cs and the unchanged existing `backend-render-performance/Runner/Fixtures.cs` against fixed matched dist DLLs, then writes `word-emphasis.snapshot.json`. It uses the original WordEmphasis package, Color background, and production profile codec round trip; it never compiles author code or creates a device. Historical saved projects remain version-gated; no version field is edited. Fixture provenance records the builder, package, complete source snapshot and exact deployment DLL hashes.

Opening process groups can change during setsid; console liveness checks use stable PID/startTicks while recording group as diagnostics. `pool-sample` remains approximate periodic observation, not a scheduler-atomic inventory. Hard slot quota comes from the production pool lock.

## Direct Worker malformed business envelope

After an exclusive GPU window is assigned, build with `build_driver.py` against the same immutable matched desktop dist and prepare a fresh isolated copy, then run:

```bash
.local/m04-acceptance/tool-round-N/publish/MakoKaraGenerator.Backend.Hosting.Tests \
  --run-real-gpu --malformed-worker-only \
  --worker .local/m04-acceptance/malformed-round-N/dist/worker/MakoKaraGenerator.Backend.Worker.dll \
  --output .local/m04-acceptance/malformed-round-N
```

This mode uses the real `WorkerProcessProxy.WarmGpuAsync`, original owned process, and exact retained device proof. The trusted tool holds the original proxy call gate and borrows its original stdin/stdout using reflection. It uses production `WorkerProtocol.WriteAsync`/`ReadAsync` to preserve valid framing and exact payload SHA. This bypasses Host source validation and the proxy invocation wrapper's error-triggered reap only; production source and normal error behavior remain unchanged. Reflection anchors fail closed. The production proxy remains the only process owner and performs final Stop/Dispose.

The exact sequence is:

1. A valid outer `ProjectRuntimeInputWorkerContract.OpenKind` message with payload `[1,2,3]`, valid payload SHA, session Guid, source SHA and frame sequence 0 must return precisely `WorkerSnapshotBudgetExceeded`, the production `DecodeOpen` length error. An arbitrary first failure is rejected.
2. A valid warm request carries the original warmed identity and exact SHA; the same PID/startTicks must return `WorkerGpuWarmupInvalid`.
3. A complete `PackageRenderWorkerContract.EncodeOpen(newGuid, "{}")` envelope is round-trip checked locally and sent with exact metadata/SHA. The same original Worker must return `WorkerGpuWarmupInvalid` from the consumed Bind, before the later invalid source can be decoded.
4. Device state before and after must contain byte-equivalent preparation/initialization/PID proof, with one original device and context. All replies correlate request/session/SHA/sequence exactly, all raw error fields match the narrow expected envelope, and original Process/pipe objects never change.
5. Release the borrowed call gate, let the original proxy Stop/Dispose, and independently require every observed PID/startTicks absent and proxy ProcessId cleared. Process group is diagnostic.

`malformed-worker-events.jsonl` records this direct Worker coverage separately from the console Host-validation negative case. No consumer, rendered frame, or performance conclusion belongs to this mode. Preparation/editing alone is not execution evidence.

Finite action A/B, strict offline comparison and symmetric actual initialization instrumentation are in [performance/PLAN.md](performance/PLAN.md). They are separate from the console and dual-consumer correctness gates.
