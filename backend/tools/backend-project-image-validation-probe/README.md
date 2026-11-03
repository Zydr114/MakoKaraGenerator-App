# Saved project Image validation HTTP probe

This tool is the finite fresh HTTP acceptance for the independent
`project-image-validation-v1` Worker capability. It reuses the existing public
validation routes and submits only a project-session revision plus
`framesPerOutput`; image bytes, paths, and profile fields are never added to the
public request.

The probe creates probe-owned `ProjectStore.Save` closures through the
independent `FixtureOracle`. It covers saved Image backgrounds for static,
static-analysis, and direct-package bases, ordinary and transparent four-frame
reports, complete report bytes/size/SHA, diagnostics pages, authenticated SSE,
ACL and stale revisions, report budget, warning and diagnostic unions, corrupt/
dimension/decoded image failures, complete image input budget rejection, internal closure tamper/missing/link boundaries,
queued/running cancel, deadline and close controls, Initialize/Evaluate author
marker controls, one orderly restart/recovery cycle, observed Worker/child
identity drain, and credential scanning. Validation compares report semantics to
an independent `RuntimeFrameValidator`; runtime diagnostic epochs are
mapped by first-seen session identity while retaining ownership and contiguous
sequence checks. This probe does
not claim validation pixel output or a browser/full-project/B3/B4/B5/B6 matrix.

Build the independent oracle against the immutable matching Worker deployment;
production assemblies are explicit references and are never rebuilt by this
tool:

```bash
python3 tools/backend-project-image-validation-probe/build_oracle.py \
  --dist /absolute/matched-dist \
  --output /absolute/image-validation-oracle
```

After root provides a source-clean matching Server/Worker deployment:

```bash
python3 tools/backend-project-image-validation-probe/probe.py \
  --output /absolute/fresh-evidence \
  --server /absolute/matched-dist/server/MakoKaraGenerator.Backend.Server \
  --worker /absolute/matched-dist/worker/MakoKaraGenerator.Backend.Worker \
  --oracle /absolute/image-validation-oracle/publish/FixtureOracle.dll \
  --unsupported-worker /absolute/preview-only-dist/worker/MakoKaraGenerator.Backend.Worker
```

`--unsupported-worker` is optional but required for the complete capability
negative boundary. It must point to the immutable preview-only deployment
(`sourceCommit=d961895`, `sourceDirty=false`); its evidence is written under
`unsupported-worker/` and is kept separate from the new matching deployment.
The main `--worker` must be the fresh source-matched 0.100.0/1.34.0 deployment.

Every acceptance uses a new output directory. Credentials stay in memory and
are redacted from request artifacts; the final manifest scans all evidence
bytes. Failed runs are retained and never overwritten.

## M02 编译依赖分离

独立 Oracle 仍自行从源码编译期望结果。其 Runtime／SDK／渲染依赖逐字节对照匹配发布的 Worker，
`Scripting.Compiler` 与两个 `Microsoft.CodeAnalysis` 程序集改为逐字节对照同一发布的 Server。
完整 framework refs 和相邻依赖仍核对发布 manifest；Worker 若含编译依赖则拒绝验收。
`build_oracle.py` 只编译工具入口并引用上述固定 DLL，不重新构建生产程序集。
