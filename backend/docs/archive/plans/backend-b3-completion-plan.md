# B3 完整工程 Worker 预览接续计划

2026-10-13。调研基线 `bc6b26d / 0.63.0`；这是施工计划，不是 B3 验收记录。并行施工中的 40 字节 `render-frame-v1` 只建立纯色背景真实 Worker 帧接缝，不包含歌词、场景、包、分析或视频，不能作为完整工程预览出口。

## 1. 结论与不复用边界

现有正式链路可复用：`ProjectSnapshot` → `ProjectRenderSessionFactory` → 独立 `IRenderFrameSession` → `RenderFrame`。需要增加专用 `snapshot-v1` 输入编解码、媒体/字体能力门禁、持久 Worker 会话代理和 HTTP 预览所有者。

没有现成完整 snapshot IPC DTO。`Core/Persistence/ProjectDtoMapper.cs` 是 internal 工程文件 mapper，其读方向允许缺失字段使用默认值并追加诊断；effects、preset、媒体和分析另有文件入口。禁止用 `ProjectStore.Save/Load`、`ProjectJson.Options` 或 `ProjectFileDto` 包装 Worker 输入：工程文件容错、路径解析、迁移和发布事务不应变成 IPC 语义。允许复用纯领域值、构造器及包验证器，专用 schema 不引用 persistence DTO。

`Backend.Contracts` 继续仅依赖 BCL，既有依赖检查明确要求它没有 ProjectReference（`Backend.Hosting.Tests/BackendDependencyTests.cs`）。新增 `Backend.Rendering`：只依赖 Core 和 Rendering.Abstractions，放专用 DTO/codec；Hosting 和 Worker 后续共同引用它，不引入 Runtime/Skia 到 Server。

## 2. 完整源字段

必须逐项承认 `Rendering.Abstractions/ProjectSnapshot.cs` 的全部 15 个可写源属性；`LineCount` 是计算属性，不传输。映射时直接创建 Snapshot，不通过 `FromProject` 再投影一次：Effects 已是 `PresetApplicationsProjection.Effects(project)` 的固定投影。

| 字段 | 完整合同 | 首个静态 codec 出口 |
|---|---|---|
| Name | 原字符串 | 保留 |
| SchemaVersion | 精确当前工程 schema，不迁移 | 校验后保留 |
| Composition | 整数尺寸、约分帧率、RGBA、可空整数微秒时长 | 保留 |
| Lyrics | schema、PrimaryBranchId、全部来源/行/元素、原文/语言/role/时间状态、完整时间、样式覆盖、注音 | 保留；不传便利 Lines/Language/AllLines |
| LyricPresentation | 节点/布局/父子/位置/参考矩形/Content/Attachment、Programs/Clips/ScheduleContext/LeadIns/生成配置、行覆盖 | 保留；Content 明确判别类型 |
| LyricStyle | Typography/Fill/Stroke/Shadow/Gradient/Opacity/Scale/Alignment/位置 | 保留；材质引用依赖包，首片拒绝 |
| LyricStylePresetId | 来源信息 | 保留 |
| EnterAnimation | 完整静态配置 | 保留 |
| Background | Kind/Fit/Playback、媒体逻辑能力与固定身份 | 首片只接受真正 Color；Path/Media 非空即拒绝 |
| AudioPath | 与无音频/未分析相区分的存在性；真正文件能力另由Hosting授予 | 首片仅无损保留声明，不读取路径；path-only不推断固定内容 |
| AudioMedia | 原 hash/length/name | 首片原值保留；存在身份时必须有非空AudioPath |
| AudioAnalysis | Available/Failed/NoStream 联合及完整真实 hops | 首片非空拒绝 |
| Effects | 固定包闭包、实例/执行顺序/预算 | 首片包/实例/自定义预算非空即拒绝 |
| PresetApplications | 固定样式快照/配方/包、绑定/Identity/Seed/BranchInputs/Enabled | 首片非空拒绝 |
| EffectInputsFingerprint | 原固定源指纹；完整版本额外核对输入一致性 | 首片非空拒绝 |

PresetApplications 不能因 Effects 已投影就省略：`PresetSceneStyleProjection.cs`、`SkiaRenderPipeline.TextMarks.cs`、`FrameEffectHost.cs`、`CompiledProjectEffects.cs` 与 `PreparedSourceMaterials.cs` 仍直接消费它。SourceMaterialCatalog 还会从歌词/场景/预设收集材质；无实例并不证明无包依赖。

## 3. 专用转换器和严格读取

首片可以复用纯不可变 init record 的形状，但序列化契约采用明确源成员清单，移除所有计算属性与可写兼容访问外观；LyricsDocument 的 init 便利属性 `Lines`/`Language` 也明确移除，否则它们可能回写主来源。读取拒绝未知字段、缺失源字段、重复字段、无效 enum、非有限数、错版本、非法领域值和超限输入，不能给缺字段补默认。

必须专门处理：

* FrameRate：通过 `(numerator, denominator)` 构造器读取，不能让 getter-only struct 变成 0/0；校验规范约分。
* OptionalValue<T>：明确 `isSet`/`value` 两个字段，保留 unset / set-null / set-value 三态；不能让默认 struct 吞掉覆盖。
* SceneContent：封闭类型 `countdownSequence` 和 `textMark`，未知判别类型拒绝。
* MaterialSpec：首片封闭类型 `solid` 和 `gradient`；包材质明确拒绝。Fill/Stroke只传完整Material，不传Color/Gradient访问外观。
* MediaFileRevision：专用hash/byteLength/originalName记录经公开校验构造器读取，不传计算RelativePath。
* LyricsDocument：只用 PrimaryBranchId/Branches/SchemaVersion，排除便利属性。
* 所有嵌套源记录：严格只读源成员清单，computed IsValid/IsEnabled/RelativePath/OffsetXPx 等不进入输入。

完整包/分析/预设扩展需要独立 converter/DTO：EffectPackageContents（manifest bytes + files），EffectParameterOverrides（原规范 JSON → Parse），ProjectEffects（包/实例/预算），ProjectEffectBinding（公开构造器，集合顺序保留），PresetApplications/PresetApplication/PresetEffectIdentity（公开构造器及完整固定 StyleTemplate），ProjectAudioAnalysis（Available/Failed/NoStream），AudioAnalysisRevision（sourceHash/pcmHash/sampleCount/points → 公开构造器并核对 revision/hash），MediaFileRevision（hash/length/name → 公开构造器）。这些不能依赖默认 JsonSerializer 自动重建 private constructor。

包还原通过 `EffectPackageContents.Create(manifest, files, ProjectEffectsJson.PackageProfile, budget, token)`；它已验证相对路径、重复/缺失/额外文件、SHA、规范 manifest、PackageVersion/SDK/Profile，随后核对传输 Revision。包图片/图/shader/C# 原字节已在 `EffectPackageContents.Files`，Runtime 使用内存闭包，不需要发布到临时工程或重新读库。

版本至少包含 `worker-1`（控制协议）、`snapshot-v1`（源结构）、明确 engine/build 身份、工程/歌词 schema、包/SDK/profile。源 hash 覆盖专用输入字节，资源清单单独含 hash/size；输入、结果都校验来源代次/修订/会话/requestId。不能从产品版本猜包 SDK。

## 4. 媒体和文件能力

仅传 Snapshot 的绝对 Path 不构成固定输入。`BackgroundFrameSourceResolver.cs` 用 hash/length 作缓存键，但真正打开时仍读取 path；它不校验当前文件字节。`FfmpegVideoFrameProvider.cs` 支持真实视频，缺失/解码失败可回退背景色，必须带真实诊断，完整预览验收不得把该回退当视频成功。

Hosting 在持有工程 Writer 时确认 exact revision、捕获 Snapshot 和资源租约，然后离开队列固定资源。首版推荐会话私有受管 staging：只从已授权 asset 或被服务打开并验证的工程闭包读源，流式复制、hash/size 校验、拒绝链接/路径越界/外部改变，Worker 仅拿服务授予的 manifest/root 能力；Worker 再核对该 manifest，不接受 HTTP 客户端绝对路径。媒体不能 Base64 塞进当前 4MiB opaque payload：长音频/视频需要有界文件能力或分块二进制块，不能静默裁切。

可复用 Core 的源固定规则但不复用保存事务：`ContentAddressFileStore` 当前 internal；`ProjectAssetCollector.Collect` 是公开固定入口但带工程保存目录结构与持久诊断，直接拿它作IPC会混淆职责。应提取或新增中立 `WorkerResourceLeaseStore`，采用同等 no-links/流式hash/长度/双重限额合同。包资源走原内存闭包，音频/背景视频走独立能力。关闭工程不能释放仍被 Preview/Worker 持有的租约。

## 5. 字体门禁

Snapshot 保存的是 Typography 的字体族/字重/斜体/回退名，没有字体字节身份。`SkiaTextLayoutEngine.cs` 默认通过 `SKFontManager.Default.MatchCharacter/MatchFamily`，失败还会试默认族；当前只有 internal 注入委托，没有正式固定字体 provider。仅传 family 字符串不能宣称跨环境像素确定。

首片同进程等价测试使用同一显式字体族与实际字体 stream hash，检查该字体可读、覆盖测试文本，证明有实际文字/注音/场景像素；两个源在同一进程使用相同字体环境。后续本机 Worker 首版 hello 回报字体环境身份并门禁，验证实际请求字体/回退与文件内容一致；完整可搬移闭包需要公开中立字体能力、Typeface provider 与 Worker 字节/文件lease，作为单独切片接入 Runtime factory。未做此项时只报告本机固定夹具，不报告任意机器等价。

## 6. 分步实施归属和出口

1. **纯色40字节 Worker 接缝（并行当前切片）**：Contracts RenderFrameV1WorkerContract、Worker Program、WorkerProcessProxy。建立真实Runtime跨进程裸BGRA结果/元数据/hash/超时回收；不包含工程 codec。
2. **静态歌词/场景 snapshot codec（本计划首个获准实现切片）**：新增 Backend.Rendering 项目及独立 Backend.Rendering.Tests，solution挂接；逐项源字段、严格converter、schema/预算/领域验证、unsupported拒绝。音频路径/身份作为声明保留且codec零IO；有固定身份必须有非空路径，Hosting另持asset lease，path-only不制造媒体修订。roundtrip覆盖整数>2^53、注音、样式三态、渐变/描边/阴影、多来源、显示片段、倒计时；正式Runtime同进程 opaque/transparent原始像素逐字节相等并证明文字非空。此时还不是 Worker 静态工程接入。
3. **持久静态工程 Worker 会话**：Worker `open-render-session-v1` 固定一次 snapshot/创建Runtime session，`render-frame-v1` 只发 sessionId/代次/time/target/mode，`close` 等待释放。新增 Hosting `WorkerRenderFrameSessionFactory` 实现既有接口；禁止每帧重新创建 Snapshot/编译/重放。Proxy 复用既有 call/cancel/kill tree合同并核对 session/source/request/outputhash。真实进程 opaque/transparent及至少3个前进时间/seek与原宿主逐字节相等。
4. **完整包/预设/分析源**：按§3扩展 codec并有界传完整文件字节；先直接实例C# + 图片sprite + shader/material/graph，再预设固定样式/配方身份/大seed/具名输入、真实分析hop。两端使用同一Runtime，不重做业务映射。用原fixtures形成复现基准，不把无包静态测试推广到脚本。
5. **媒体/字体闭包和运行能力**：§4/5的资源租约、native字体身份、Worker引用包、FFmpeg路径/能力/config。真实图片/视频loop/hold/透明语义、源被改/删除的固定性、引用释放及失控脚本强制回收后新session成功；Worker包含真实script-refs/native复制，不能只验证Debug已装SDK环境。
6. **HTTP预览所有者**：新增 `ProjectSession.CapturePreviewAsync`、`PreviewSessionRegistry`，Server `POST create`/`PUT input`/`POST frame-requests`/`GET state`/`POST close`；已存在GET frame/diagnostics继续复用。用ClientRequestReceiptStore做client级回执，所有输入更新在Writer中核对exact sourceRevision，Preview代次独立递增，seek原子换代。POST只接受后返回，RequestAborted不取消已接受帧请求。
7. **PNG/诊断/事件发布和B3出口**：中立BGRA→PNG编码器（独立Skia适配，不能让Contracts引用Skia），转换premultiplied→straight并核对透明像素。PreviewFrameHandleStore发布只由预览所有者调用，数量/字节按会话淘汰旧帧而非全局第9帧永久拒绝。持有读取副本至HTTP完成。BackendPreviewCoordinator的内部Sequence要明确映射HTTP client RequestSeq，不能把两个序列混用；事件携带实际被渲染请求。生产诊断delta/occurrence/有界完整报告，当前PreviewDiagnosticsStore仅存外部页、会默默淘汰第33页，不能当完整生产链。扩ClientEventJournal的preview事件；读取state能重同步。

## 7. B3真正剩余验收

完整 B3 要同时有：代表完整工程固定Snapshot→专用IPC→真实持久Worker→原正式Runtime→裸帧→PNG→受认证HTTP，歌词/场景/包图片/脚本/预设/视频不丢失；普通/透明帧和原宿主等价；持续playback有一执行+最新pending且不饥饿；换输入/resize/seek/关会话后旧帧拒绝；失控真实脚本被杀、归属FFmpeg子进程收敛、主服务仍响应且新会话成功；资源/帧/诊断的容量、lease/关闭/断流边界可重复。

快照重复投影、静态Scene逐帧重建、锁外事件、释放等待各有独立出口。当前`ProjectSnapshot.FromProject`生成effects/fingerprint时多次投影，`ProjectRenderSession`复用pipeline但不证明scene索引已静态缓存；它们不能由跨进程链通过自动记为已优化。全量、性能、原生UI/平台、B4/B5和原暂停范围不随本计划恢复。

<a id="b3-package-source-codec-plan"></a>

## 8. B3-E：固定效果包源 codec（0.73.0，有限出口已完成）

**状态：本批有限出口已完成，完整 B3／B4 仍未完成。** 五 scope 统一验证已实际完成；本节只收口固定效果包源 codec 与同进程 Runtime 出口，不把作者过程、变异过程或未开放的 Worker／HTTP 包执行写成产品完成。

本轮只增加 `Backend.Rendering.PackageProjectSnapshotCodec` 与 `package-lyrics-scene-v1` profile；不把 profile 加入 `Backend.Contracts` 运行 allowlist，不扩大旧 Static SelectProfile、Worker 或 HTTP 能力，不新增 route、job、schema、SDK、store 或权限。完整 B3/B4、包执行 Worker IPC／HTTP、失控脚本恢复、图片/视频/字体闭包及原全量/性能/GUI/平台范围保持未完成。

### 8.1 固定源与严格预算

Codec 必须覆盖 `ProjectSnapshot` 的 15 个可写源属性：`Name`、`SchemaVersion`、`Composition`、`Lyrics`、`LyricPresentation`、`LyricStyle`、`LyricStylePresetId`、`EnterAnimation`、`Background`、`AudioPath`、`AudioMedia`、`AudioAnalysis`、`Effects`、`PresetApplications`、`EffectInputsFingerprint`。计算/便利属性（如 `LineCount`、`Lines`、`Language`、`IsValid`、`RelativePath`）不进入输入。读取拒绝未知或重复字段、缺失源字段、错版本/enum、非有限数、非法领域值和超限输入，不用默认值补齐缺失字段。

完整包必须保留规范 manifest 原始 bytes、全部 files 原始 bytes、相对路径、顺序、SHA/长度、PackageVersion/SDK/profile、预算及 `PackageMaterial`；直接实例必须保留所有字段、顺序、参数原规范 JSON 及数字精度、绑定/读取范围、seed、启用状态和五项 `ProjectEffectBudgets`。`AudioAnalysis` 的完整联合状态、source/PCM hash、sampleCount、points/hops、revision 与诊断字段，以及 `EffectInputsFingerprint` 必须是原 `ProjectEffectInputs` 算法产生的固定源指纹，不是 snapshot wire bytes 的 SHA，均须严格验证。4MiB 总 wire 预算、总 200000 个源值（含 JSON 深层值）、JSON `MaxDepth=64` 与提前图深度 60 须在解析前后统一计数，拒绝超限、非 canonical manifest、manifest 非当前 `PackageProfile`、规范 manifest bytes 不 byte-equal、重复/缺失/额外文件、路径越界或 hash 不符，不截断、不重建临时包；这些预算只属本 codec 合同，不改旧静态 profile。

非空 `PresetApplications` 必须稳定拒绝；图片或视频 `Background`，以及 Background 的非空媒体路径/修订或外部引用同样拒绝，不能清空后继续渲染。`AudioMedia` 作为源声明不因该门禁被误拒绝。音频 path/identity 只保留声明，codec 零 IO，不由 path-only 推断固定文件。所有拒绝须不改源且不进入 Runtime。

### 8.2 最小 Runtime 出口与边界

同进程原正式 Runtime 的普通／透明目标、效果包实例像素贡献、前进／回退 seek／再次前进及完整 round-trip 已通过；完整 15 字段、manifest/files bytes、直接实例顺序、预算、PackageMaterial、analysis 与 fingerprint 均在本 scope 闭合。固定字体和包 bytes 夹具只证明同进程等价，不能推出跨进程 Worker、HTTP wire、跨机器字体、图片／视频闭包、脚本隔离或完整 B3/B4。

### 8.3 统一验证与证据

权威 manifest `artifacts/backend-extraction/b3-e-final-verification/manifest.json` SHA `f1351ce60eb4e8a5f199a6b621d8d373a813dca5db4308701f0de1618fc60848`。五 commands 均 exit 0，solution 零警告／错误；`Backend.Rendering.Tests` 155／155（旧 61＋新合同 86＋Runtime 8），Hosting 555／555、Worker 70／70、self-contained Server 44／44，合计 824／824。1609 source 与 5849 binary 无漂移，37 个 solution 输出均为 0.73.0，输出 DLL 与对应 intermediate DLL 逐字节一致，362 个实际观察 PID/startTicks 在 finalize 均已消失。root 独立审查 `root-final-audit.json` SHA `0bba0794b05e820d7d8dff591d52cfc271e815248a214452517284867b179741` 重核 7484 唯一文件（1609 source＋5849 binary＋26 evidence）零 hash mismatch；author audit `root-author-evidence-audit.json` SHA `69417b5491920f590cbfa5861213ad64a2f2edd6f3e265aff6a6e7bfd6c4463d` 与作者过程另列。预算顺序证据来自源审查而非 allocation telemetry，不能据此宣称运行时资源分配遥测。

本批不执行或开放 package Worker／IPC、HTTP 包 route、隔离／失控恢复或图片／视频背景；未改 Media／Runtime 生产矩阵不重跑原完整范围。原失败、作者过程与变异证据保持独立，不并入 824；下一步 package Worker／隔离是新的合同与验收切片。

<a id="b3-package-worker-isolation-plan"></a>

## 9. B3-F：内部 package Worker 会话与失控隔离（0.74.0，有限出口已完成）

**状态：本批有限出口已完成，产品`1524625`已提交推送。** 本批接续 B3-E，仅建立内部 package Worker 会话与失控隔离；完整 B3／B4、公共HTTP包执行／package allowlist扩展、作者 Web、完整预设／媒体／字体闭包和原暂停矩阵保持未完成。

### 9.1 私有会话和能力门禁

新增显式 `WorkerPackageRenderFrameSessionFactory` 与独立私有 `PackageRenderWorkerContract`，提供 capability、open、render、close、result；旧 `hello` 合同不改。capability 必须在 open 的 package bytes 之前实际确认当前 EngineIdentity、codecProfile、PackageVersion、SDK、shader 和完整 compiler references（包括 TokenId）的实际 identity 与非空可用性。Host expected refs 必须以 Worker 部署目录中的 refs bytes 加 Host 自身 TokenId assembly actual hash 计算并核对；`ScriptCompiler.ReferenceIdentity`／`ReferenceCount` 仅暴露实际参考集身份和数量，不改编译算法。仅报告实际 read／compiler 构造成功；非空不等于完整 SDK 参考集签名证明，也不构成 immutable 保证。公开HTTP运行allowlist、旧静态SelectProfile、HTTP route／task／recovery、项目 schema、保存／历史均不扩。

### 9.2 固定输入、帧关联和诊断

open 一次固定 B3-E `package-lyrics-scene-v1` snapshot 与包闭包；净 snapshot wire 预算为 4MiB 减 24 字节（复用现有 24B header），超限在进入 Worker 前稳定拒绝。逐帧只传 session handle、source hash、request sequence、time、quality、target 与 transparent，不能逐帧重编码、重读工程目录或重新编译；sessionId 表示固定输入生命周期，没有独立 generation 字段；结果必须完整回传 input/output/session/source hash、尺寸／像素格式、帧序与 RuntimeDiagnosticSnapshot。Program reader 与同步 script 执行分离，禁止把 reader EOF、编译器诊断或脚本异常伪装成像素成功。非空 `ImageEffectDiagnostics`／`MaterialDiagnostics` 结果返回 `WorkerPackageDiagnosticsUnsupported`；compiler failure 返回 `WorkerPackageSourceDiagnosticsUnsupported`，实际编译失败及非空图片／材质诊断的拒绝已由真实Runtime回归验证；完整 source diagnostic DTO 另批。

### 9.3 失控脚本、取消和排水

在真实 runaway init／evaluate marker 之后验证 init／evaluate × cancel／deadline／Dispose（停止）的六种组合；正常 close／reopen 另列。Worker 必须强制结束并等待实际 private group／Host subreaper 观察到的全部成员排水，Host 仍可创建健康的新会话，必要时新 Worker 可恢复。deadline 返回稳定 `WorkerPackageTimedOut`，用户取消保 `OperationCanceledException`。无能力、错 identity、空 compiler refs、错 session／source／sequence／handle、旧结果或诊断错误均在进入 Runtime／发布前拒绝；unrelated 进程不受影响。正常 close／reopen 单独验证释放和重新建立；关闭、失败和重建不得泄漏旧会话、包 bytes、编译器或脚本子进程。

### 9.4 统一验证范围与独立部署边界

被忽略的 `artifacts/backend-extraction/b3-f-final-verification/verify_runner.py` 沿 B3-E runner 保留 source SHA、TRX、零警告／错误、实际进程排水、输出／中间 DLL SHA 与 artifact SHA 校验；原 B3-E 证据不重写。计划 scope：solution build、Backend.Rendering 全量、Backend.Hosting 全量、Backend.Worker 全量、Runtime.Hosting 定向（新 `WorkerPackage*`、旧 `WorkerStaticSessionIntegrationTests`、`BackendDependencyTests`）、Processes `LinuxProcessGuardTests`、linux-x64 self-contained Server 全量、Scripting.Tests 精确 `CompilerTests|EffectPackageCompilationTests|CompiledEffectPackageTests`（覆盖 ReferenceIdentity／ReferenceCount getter 的 source impact，不改编译算法）。合计八个 scope，root 已按实际 source impact 审定 `rootApproved=true`、所有 `filterPending=false` 并冻结 1618 项源输入，freeze SHA `f7e321d6d9839b6c2844d8858f099f20ca23605a7128460019a792754ae9235f`。统一八commands全部exit0，solution零警告／错误，七TRX971／971＝Rendering155＋Hosting639＋Worker70＋Runtime定向31（package20／旧static9／dependency2）＋Processes2＋self-contained Server44＋Scripting30。1618source／5849binary无漂移，37个solution输出0.74.0且与intermediate逐字节一致，448观察身份全部gone。权威manifest SHA`3c978536ed679938f9bb92368573da5a1d9ffa9ce0950c0df7116c6f921e28f8`；原HEAD`7c31865`保验证执行基线，后续产品提交不改其记录。HTTP 不在本批 scope；package Worker／隔离结果不能被写成完整 B3/B4。

真正 self-contained Worker 部署与 raw IPC 能力、实际脚本执行及 refs 缺失探测须独立保留 source／command／exit／log／实际文件 SHA 和 PID/startTicks 证据，不并入统一测试数。publish 与已发布 apphost 的 Runtime scope 同用 `Release / linux-x64 / SelfContained=true`；`MAKO_B3F_TEST_WORKER_PATH` 必须为实际发布 apphost 绝对路径，Host TokenId 与 Worker refs identity 仍严格匹配，不以配置相同代替实际 hash。统一结束后独立部署已完成：publish434文件／15身份gone，raw IPC健康及单删System.Runtime.dll副本实际WorkerPackageUnavailable／两Worker exit0与2身份gone，匹配Host的已发布Runtime20／20／47身份gone。actual refsCount168、identity`54065d5730fb15dac384bcd350321f964303aed9b75e9ce53daa378753e5d089`；三manifest及TRX／apphost SHA见[实施§29](../history/backend-extraction-progress.md#b3-package-worker-isolation-0740)，不并971。独立最终审查9482 SHA引用／18TRX／1058唯一观察身份全部gone、零mismatch；作者84与mode2及原失败另列，不并统一。

capability／open／render／close 必须绑定同一已经验证的实际 Worker 进程身份；进程变化即拒绝，不能沿用旧 capability 发送新包。同一 Worker 的 static／package Runtime 会话互斥，双向 open 都须稳定拒绝而保留已打开会话。隔离声明只限本机 Linux 实际观察的 private group 与常规后代，不构成任意恶意脚本的 OS sandbox 或跨平台保证。

取消回执必须与原请求的 input/session/source/sequence 关联，只有本次取消竞争或调用者取消已实际发生才可接受 `cancelled=true`；合法合作取消 ack 不因帧尺寸／诊断字段缺省被误当成损坏的成功帧。伪取消、错关联、含输出／错误的取消回执明确拒绝。caller cancel 保 OCE，只有内部 deadline 独赢才映射 `WorkerPackageTimedOut`。


<a id="b3-package-diagnostics-plan"></a>

## 10. B3-G：内部package三类完整诊断传输（0.75.0，有限出口已完成）

**状态：本批有限出口已完成，完整B3／B4仍未完成。** 本批接续§9，只扩内部package Worker的源码编译、图效果及材质三类诊断；统一与独立部署终态分别见[实施§30](../history/backend-extraction-progress.md#b3-package-diagnostics-0750)。公共HTTP包profile／route／正式任务、预设、外部媒体／字体闭包、完整B3／B4及原暂停矩阵不随本批开放。

### 10.1 三类完整值与固定来源身份

私有`WorkerPackageDiagnosticsDto`采用独立Version1；capability必须显式协商`PackageDiagnosticsVersion=1`，缺字段或旧版本明确拒绝，不回退成丢诊断的旧包会话。新增诊断DTO的所有字段必须显式提供，nullable值不等于缺字段；新增诊断DTO及capability JSON的未知／重复／缺失字段与非法身份拒绝，不把整个既有WorkerMessage的nullable字段改为必填或全面禁止其顶层未知字段；顶层仅特别拒绝重复packageDiagnostics容器。source、image、material三数组分开，不能伪装成既有RuntimeDiagnosticSnapshot的作者发生。

- **源码编译诊断**保完整十字段：PackageId、Revision、InstanceId、ExportId、Code、Message、IsError、Path、Line、Column。Line／Column为0合法，负值拒绝；nullable InstanceId／ExportId成对。失败使用`WorkerPackageCompilationFailed`，经`RenderSessionFailureException.SourceDiagnostics`返回真实完整源诊断，不把编译错误改为Runtime occurrence或像素成功。
- **图效果诊断**保InstanceId、ArtifactHash、Graph的PackageId／Revision／ExportId、InputId、Code。Host从本会话固定snapshot的包目录构建catalog，验证启用实例及包／图归属，再恢复原SDK资源句柄，不接受远端任意构造资源身份。
- **材质诊断**保Material资源三元身份、InputId、Code与完整owner判别联合。Script owner保InstanceId、ArtifactHash、必非null的PackageIdentity；Source owner保Path、Region、Slot、SourceMaterial三元身份、规范ParametersJson。两类字段不得混用；Host按固定catalog、实际启用实例及SourceMaterialCatalog的固定来源还原owner／slot／资源身份，未知来源拒绝。controller ArtifactHash仅核64hex格式及固定包／实例关联；直接图完整revision须等于固定实例源。Host不独立重编译或证明compiler代次DLL bytes匹配。

已有RuntimeDiagnosticSnapshot的epoch／sequence、作者诊断身份与Error禁pixels合同保持；成功包帧须显式携带完整图效果／材质数组且source为空。编译失败走独立source结果，不能用成功帧或通用transport诊断替代完整源码错误。真实源码失败与图效果／两类owner材质警告的原Runtime完整值和像素等价已验证。

### 10.2 分项及合计预算

三类各自最多256条、实际UTF8序列化数组最多64KiB；三类合计最多512条，完整诊断DTO实际UTF8序列化最多128KiB（含版本、数组和序列化结构字节）。任一条数或字节超限均返回`WorkerPackageDiagnosticsBudgetExceeded`，不截断、不只发摘要、不附带已超预算容器；合法边界与单类／合计超限须分别回归。字段／身份非法用`WorkerPackageDiagnosticsInvalid`。该预算独立于既有Runtime诊断及4MiB IPC／4MiB−24B净snapshot预算，不扩旧static协议能力。

### 10.3 原会话及失控隔离合同保持

capability／open／render／close继续绑定同一实际Process及原pipe、EngineIdentity／package profile／SDK／shader／actual refs；一次open固定包bytes，旧Worker或诊断版本不匹配在发送package open前拒绝。static／package互斥、input／output／session／source／sequence完整关联、Runtime Error禁pixels、合法合作取消ack、caller OCE／独赢deadline编号及Dispose排水保持。真实init／evaluate×cancel／deadline／Dispose六组合、Host并行健康与新Worker恢复须回归；本机Linux实际组排水不构成任意恶意脚本OS sandbox或跨平台保证。

### 10.4 统一有限验证与独立发布证据

root审定并实际完成统一六commands：正常solution build；Backend.Rendering／Backend.Hosting／Backend.Worker各全本项目；Runtime.Hosting定向`FullyQualifiedName~WorkerPackage|FullyQualifiedName~WorkerStaticSessionIntegrationTests|FullyQualifiedName~BackendDependencyTests`；linux-x64 self-contained Server全本项目。Contracts协议与旧静态兼容由对应Hosting／Worker／Runtime范围覆盖；Scripting／Processes生产源未变时暂不重复，实际scope按source impact审定，原失控六组合包含于WorkerPackage定向范围。六commands全部exit0、solution零警告错误，五TRX1001／1001（155／699／70／33／44）；Runtime33为package22／旧static9／dependency2。1623source／5849binary无漂移、37个0.75.0输出与intermediate字节一致，476观察身份gone。权威`artifacts/backend-extraction/b3-g-final-verification/manifest.json` SHA`4c5ca351816072651f79e241e34d9cce43c2af5698979cd932b0d61f65701891`，source freeze SHA`2921307de583e3aef31a46c90c164296f048a6be17482747c9ca3ba0ab802a2e`，原HEAD0549705保执行基线。

独立Release／linux-x64／SelfContained发布434文件、raw IPC健康diagnosticsVersion1及单删System.Runtime.dll副本拒绝、匹配Host下实际发布Runtime22／22与真实0.74旧诊断版本Host拒绝均green；15／2／50观察身份分别gone，旧版本raw／publish／run另1／3／2gone。legacy-final1 harness CA1869构建失败0tests保持，final2仅修ignored harness缓存options后publish／run exit0。实际apphost／refs、四manifest SHA及日志详见实施§30，不并1001。原B3-F971、独立部署20、作者84／mode2及历史失败不重跑改写为本批数字。MINOR0.75.0保持格式28／16／6、配置1、包profile2、SDK1.0.0与固定依赖；公共package HTTP仍需独立合同与验收。
