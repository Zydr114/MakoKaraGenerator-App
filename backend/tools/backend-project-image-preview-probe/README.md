# Saved project Image background preview probe

This tool is intended to perform a finite fresh HTTP acceptance for the `project-image-preview-v1`
profile. It creates probe-owned projects with a saved `BackgroundSource.Image`,
opens them through the public project-session and preview routes, and compares
ordinary Runtime PNG/BGRA output with an independent `ProjectStore.Load` oracle.

The finite HTTP acceptance covers ordinary and transparent four-frame previews, backward
seek, resize, input replacement, authenticated SSE, ACL, stale revisions,
corrupt-image decode failure, source and pinned-input mutation boundaries,
project close, shutdown, Worker/process draining, credential scanning, and an
image contribution counterfactual. The static-analysis fixture retains an original
Available analysis and its saved canonical content identity. The direct package fixture adds the same
Image background to a real package effect and compares a disabled-package
counterfactual. It does not claim video, fonts, presets, validation, export,
browser, performance, GPU, or complete B4/B5/B6 support.

The FixtureOracle first saves each fixture through `ProjectStore.Save`, then
loads that saved closure for every expected frame. The HTTP response never
constructs expected pixels. The probe records explicit mutations of its own
`loaded-input` directory as an internal fault-injection boundary; those files
are not a public media-edit API. Fixed image bytes are expected to be consumed
in memory by the Worker. The probe only checks that owned `loaded-input`
closures and unexpected image staging directories drain after close and
shutdown; it does not require a particular grant-file layout.

The Oracle build and local original-Runtime fixtures are verified separately from
HTTP acceptance. The accepted matched-deployment result is recorded below.

First commit the fixed production source baseline and publish the source-clean
matched Server/Worker deployment. Build the independent Oracle entry point with
explicit references to those immutable Worker DLLs:

```bash
python3 tools/backend-project-image-preview-probe/build_oracle.py \
  --dist /absolute/matched-dist \
  --output /absolute/new-oracle-directory
```

After root provides a source-clean matched Server/Worker deployment, run:

```bash
python3 tools/backend-project-image-preview-probe/probe.py \
  --dist /absolute/matched-dist \
  --oracle /absolute/new-oracle-directory/publish/FixtureOracle.dll \
  --output /absolute/new-evidence-directory
```

Every run requires a new evidence directory. Credentials remain in memory and
the final manifest scans evidence bytes for them. The tool does not publish,
change source, or update generated contract/status documents.

`build_oracle.py` generates a project without production ProjectReferences and
publishes only the fixture/Oracle entry point as Release linux-x64 self-contained.
The original Runtime/compiler DLLs are explicit AssemblyReferences to the
immutable deployment; native libraries and all script refs come from the same
Worker. `input-manifest.json` records tool source hashes, the exact compiler
command, generated project, deployment manifest, references and published bytes.
This preserves independent ProjectStore.Load/original Runtime expectations and
the exact production assembly identity. Recompiling the same production commit
in a temporary worktree produced different DLL bytes in this run, including with
an explicit PathMap; the precise cause was not established. The normal FixtureOracle.csproj
remains useful for local implementation checks, but a successful local build is
insufficient for fresh HTTP acceptance.

The control fixtures contain the same saved Image background and use the shared
actual-author lifecycle matrix: Initialize/Evaluate each exercise input-replacement
cancel, deadline and explicit close, concurrent healthy Image rendering, recovery,
and the exact observed Worker/child process identities draining. The deadline
rejects pixels and requires the stable Worker timeout code.

Before fixture generation or HTTP startup, `oracle-input-identity.json` records
and requires byte equality between every adjacent oracle dependency DLL and
the immutable Worker's deployment manifest. This includes the actual
`MakoKaraGenerator.Scripting.Abstractions.dll` SDK, the original Runtime/render
chain, Roslyn, and all 167 framework-reference DLLs in this baseline. Runtime
diagnostic `artifactHash` remains an exact comparison. Equal product versions,
Release configuration, or framework refs alone are insufficient. This run observed
dependency byte differences across source versions, build paths and publishing
methods; PathMap did not resolve them, and the complete cause was not established.
`ScriptCompiler` includes the actual SDK bytes in its reference identity. A tool-only later commit may
reuse a previously matched immutable deployment and oracle; the byte guard and
production-source manifest, rather than the checkout's current commit label,
decide whether those inputs still match.

The probe treats `CommandNotSupported` image-source failures as HTTP 422 and
checks the stable `ProjectImageSourceChanged` / `ProjectImageSourceUnavailable`
failure codes on the `background` field. The owned pinned image is expected at
`loaded-input/data/media/<sha256>.media`, matching `Background.Media.RelativePath`;
the probe derives the basename from the saved ProjectStore closure and verifies
size and SHA before fault injection.

The finite `http-final6` run under
`artifacts/project-image-preview-0990-acceptance/` passed 1175 HTTP requests and
9760 assertions against source-clean deployment `d961895`. Static, static-analysis
and direct-package Image sources each covered four ordinary and four transparent
frames plus backward seek. The run recorded 40 original-Runtime PNG/pixel
comparisons, 38 diagnostic comparisons, 12 ordinary background and eight package
counterfactuals, and six Initialize/Evaluate × cancel/deadline/close controls.
Server/launcher exited 0; all 105 observed process identities drained, including
83 Worker identities. Credential scanning covered 2752 evidence files; the
manifest contains 2753 files. Root independently rehashed all source, deployment,
Oracle and evidence inputs and decoded all 40 downloaded PNGs; see
`root-final6-acceptance.json` and implementation §77 for exact evidence and SHA.

The original `http` run failed at 118 requests/1014 assertions; final2/3/4
stopped at the dependency identity guard before HTTP (zero requests each).
Final5 passed 419 requests/5346 assertions without static-analysis or the six
controls. Those directories retain their own scope and counts; final6 does not
add their numbers or establish browser, video/font/preset, image validation,
export, measurement, performance or complete B3/B4/B5/B6 acceptance.

## M02 编译依赖分离

独立 Oracle 仍自行从源码编译期望结果。其 Runtime／SDK／渲染依赖逐字节对照匹配发布的 Worker，
`Scripting.Compiler` 与两个 `Microsoft.CodeAnalysis` 程序集改为逐字节对照同一发布的 Server。
完整 framework refs 和相邻依赖仍核对发布 manifest；Worker 若含编译依赖则拒绝验收。
`build_oracle.py` 只编译工具入口并引用上述固定 DLL，不重新构建生产程序集。
