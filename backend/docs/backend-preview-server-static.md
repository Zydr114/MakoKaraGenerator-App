# Server 静态项目预览有限接线

> **0.69增量：** 下文首批单profile／拒分析／预算记录保留其历史范围；当前B4-E已增加完整`static-lyrics-scene-analysis-v1`，旧`static-lyrics-scene-v1`仍仅null。唯一snapshot-v1／Source自动选profile、Available canonical／Failed／NoStream完整保真，总4MiB深200k及实际wire同规则闭口，不截断。public旧sourceProfile保留并增sourceProfiles，私有export真实probe匹配EngineIdentity与双profiles，旧Worker不虚称兼容；purevideo保完整声明但禁止源音频IO，true grant重绑保Media身份，sourceSHA为原编码。preview／validation扣24／32头admission，export按实际metadata＋32在spawn/send前guard，不能承诺所有动态封包均admission拒绝。实际有限559／559及真正HTTP232请求／1864断言、三片各30完整解码帧／首帧oracle见[实施§23](archive/history/backend-extraction-progress.md#b4-analysis-static-profile-0690)／ADR-0213；当前HTTP只维护于[统一API](architecture/backend-api.md)。新profile不表示音频驱动视觉或完整B3／B4完成。


日期：2026-10-13。本切片在 Hosting registry 上连接受管理 Worker、真实 Skia PNG 编码器和现有受保护读取存储，新增以下生产路由。固定输入仍只接受 `project` 来源与当前精确工程修订；客户端不能提交快照、源码、Worker路径或任意本机文件路径。本切片不代表完整B3或Web编辑器完成。

## 路由与结果

| 方法／路径 | 请求 | 成功结果 |
|---|---|---|
| `GET /api/v1/runtime-info` | 已认证客户端 | `PreviewRuntimeInfoResponse`，实际主机配置、静态profile、预算和禁用原因 |
| `POST /api/v1/preview-sessions` | `PreviewSessionRequest`；`Source.Kind=project`、`SourceId=工程会话ID`、`SourceRevision=精确工程修订`、`Purpose=preview` | 201，直接 `PreviewSessionResponse` |
| `PUT /api/v1/preview-sessions/{id}/input` | `PreviewProjectInputRequest`；旧预览代次／来源修订＋新项目来源／修订／输出设置 | 200，直接新代次 `PreviewSessionResponse` |
| `POST /api/v1/preview-sessions/{id}/frame-requests` | `PreviewFrameCommandRequest`；来源修订、代次、单调序号、微秒、`playback/seek` | 202，直接 `PreviewFrameAcceptedResponse`，不承诺帧已完成 |
| `GET /api/v1/preview-sessions/{id}` | 已认证客户端 | 200，`PreviewSessionStateResponse { session, failure, renderFailureCode }` |
| `POST /api/v1/preview-sessions/{id}/close` | `ClosePreviewSessionRequest`；代次／来源修订 | 200，排水后的 `{ session: closed, failure, renderFailureCode }`；后续GET返回404 |

已有帧内容GET／HEAD和诊断分页路由继续使用同一存储，读取不会生成像素。所有修改请求都携带 `RequestMetadataDto`；复用原 `ProjectSessionRegistry.Receipts` 的client级回执，先检查方法／路径／规范载荷及有效窗口，再执行副作用。同body／同requestId重试返回原结果，包括seek后的原新代次；换载荷或路径重用requestId拒绝。HTTP断开只终止读取，已接受帧请求不绑定 `RequestAborted`。

## 真实执行与部署

组合根仅创建 `WorkerRenderFrameSessionFactory`，没有同进程fallback。Worker apphost由主机配置：`MAKO_BACKEND_WORKER_PATH`必须为绝对路径；未设置时取Server部署目录中的 `MakoKaraGenerator.Backend.Worker`（Windows为 `.exe`）。Server发布不会自动把Worker复制到邻目录，部署者必须显式部署Worker和其依赖，或设置受控绝对路径。

缺少或不可执行的Worker将禁用创建能力，创建返回503 `previewUnavailable`。文件存在时runtime-info标为 `configured-unverified`；这是配置事实，未把文件存在称为已通过握手。真实Worker打开／渲染失败进入当前failed状态，不提供占位PNG。Server保留原Core／Application，Skia仅在独立编码适配项目，Hosting不依赖Runtime.Hosting或Skia。

Server公布最大1280×720及4MiB裸像素预算；创建／更新先验证 `width×height×4`，并检查静态codec编码字节不超过 `StaticRenderWorkerContract.MaximumSnapshotBytes`（4MiB减去24字节open头），再接受固定输入。`PreviewLimitsDto.MaxRawFrameBytes`是兼容的新增预算字段。静态profile的包／效果／预设、分析、图片／视频及其他不受支持字段明确拒绝；音频路径和媒体身份只作固定源声明，不授权Worker读取路径。

PNG保留上限为每会话2帧／8MiB，已有读取租约有独立数量／字节配额。`MAKO_BACKEND_PREVIEW_FRAME_LEASE_SECONDS`默认30秒，正值最大300秒；runtime-info返回实际租约、快照／像素／读取预算及客户端到期策略。TTL、Range、ETag和HEAD由已有读取合同处理。

## 资源与生命周期

来源捕获在 `ProjectSession.Writer` 内检查client、关闭状态和精确修订。预览持有两层音频引用：固定输入的保留引用覆盖尚未开始渲染的会话；每次runtime Create另外取得运行引用，成功、失败、取消及Dispose都有归还出口。更新先Invalidate旧输入再释放其保留引用，旧runtime的运行引用保留到实际排水；关闭先排水再释放输入引用。工程关闭后，预览仍持有已固定来源所需的音频资源身份。

同代次连续前向playback复用已打开的Worker／Runtime；HTTP seek按合同生成新generation并调用Coordinator.Invalidate，排水／释放旧Runtime和Worker，下一帧重新open。私有Worker原生测试在同一session直接请求后退时刻，验证的是私有接口能力，不等于此处HTTP seek复用。

服务的接收操作与租约清理共用membership闸门，防止到期清理结束后旧在途create重新出现。真实客户端租约到期时，先停止／排水预览、再释放其引用；工程会话保留，恢复客户端需要重建预览。本切片未新增显式client suspend／release路由，也未改变未保存工程的处置策略。主机停止接收POST／PUT，排空已接受操作，关闭预览，再释放工程服务。

小型状态通知进入128条有界独立队列，再追加至现有客户端journal；不会在registry闸门或渲染执行者上调用外部观察者。SSE真实事件为 `preview.stateChanged`、`preview.frameReady`、`preview.failed`和 `preview.closed`，附带 `PreviewEventDto`，不携带像素或完整工程。队列饱和时可丢弃旧提示；客户端按帧序号／事件缺口重新GET权威状态。观察者失败不把已发布帧改成失败。所属工程关闭后事件仍归客户端所有。

## 实际失败诊断

中立 `RenderSessionFailureException`传递稳定内部编号与实际不可变Runtime报告，Coordinator在首次及缓存失败中保留它们。Registry不发布错误帧或调用PNG编码；GET状态／SSE以可选 `renderFailureCode`暴露稳定编号，HTTP通用错误仍使用已有错误类型，客户端无需解析中文消息。

实际报告投影保留 `OriginKind=runtime`及typed `BackendDiagnosticOccurrenceDto`：epoch、序号、实例、产物、阶段、微秒、逻辑源码位置和固定包身份。没有实际完整快照时，只发布标为 `transportFailure`的安全失败说明，Occurrence为空，不伪造Runtime发生。完整报告超过条数或64KiB投影预算时明确返回诊断预算失败，不截断为假完整页。

## 本切片定向证据

标准分析器／警告即错误开启，Hosting registry＋coordinator **21／21**、Server transport **11／11**通过，无失败／跳过；TRX分别为 `artifacts/backend-extraction/b3-registry/preview-registry.trx`与 `artifacts/backend-extraction/b3-preview-server/preview-transport.trx`。Server测试在真实本机HTTP监听器上执行，渲染工厂采用可控像素夹具，以验证回执、精确修订、PUT／CORS、寻址、真实PNG存储读取、SSE、资源引用和生命周期竞争。

本机没有ASP.NET共享runtime，Server测试采用 `dotnet test … -m:1 -r linux-x64 -p:SelfContained=true`；默认framework-dependent启动中止不算通过。独立Worker／Runtime像素对照、完整HTTP成功／失败探针由另一个验收工具归档，不用本测试夹具替代。完整包／媒体闭包、几何、持续播放性能、完整诊断生产出口、B4／B5、Web客户端和平台人工验收仍待后续。
