# 已保存工程静态图片背景的受管公共预览

设计：2026-10-22；实现/合同更新：2026-10-23。目标产品0.99.0／合同1.33.0／HTTP协议1。有限实现、本地回归与匹配发布fresh HTTP切片已完成；实施与验收结果按证据分层补录，不改变历史证据归属。

## 范围与来源能力

现有 `POST /api/v1/preview-sessions` 的 `project` 来源，以及其 input、frame-requests、seek、resize、close 路径，增加明确的 `project-image-preview-v1` source profile。仅接受由既有 ProjectStore 保存、加载并复制到后端 `loaded-input` 闭包的 Image 背景。基础工程可以是原静态、完整静态分析或直接效果包；所有固定预设、视频背景、外部字体、本机任意文件路径、validation/export 和模板候选试看均不由本批开放。

包内 sprite/graph 图片已有正式 Runtime 完整解码与实际消费，本批不重复宣称其首次接入。既有 package-resource-closure-v1 的 grant/video/font 私有检查 seam 也不由本批接到任务。本批使用独立固定内存内容，避免将一次私有检查或重复解码记作完整资源能力。

## 信任边界与不可变输入

客户端仍只提交工程会话 ID 和精确修订，不提交图片 bytes、主机路径、媒体句柄或授权 grant。Host 在唯一 Writer 下捕获不可变 snapshot 和已授权 pinned closure 的短期读引用；此引用的取得没有文件 IO、native decode 或 Worker 调用。文件读取发生在 Writer 外。关闭工程先阻止新捕获，并等待该短期读取引用排水，然后删除 loaded-input；捕获完成后预览持有独立固定 bytes，不依赖工程目录继续存在。

只允许读取 `Background.Media.RelativePath` 对应的 loaded-input 内容地址，实际 snapshot 路径必须与此路径一致。检查所有父目录/文件链接、声明大小和内容 SHA-256。原始文件名只作为既有 MediaFileRevision 的显示元数据保留，不作为路径解释。读取完整内容后再核 SHA，Worker 收到的同一 bytes 再核 size/SHA。Host 不加载 Skia、不读取图片头、不执行 native 解码。

捕获失败对外使用固定中文信息和逻辑 `background` 字段，绝不包含主机路径。源大小/内容变化为 `commandNotSupported` / `ProjectImageSourceChanged`；缺少已保存授权闭包或身份为 `commandNotSupported` / `ProjectImageSourceUnavailable`；原图或完整封包超预算为 `resourceTooLarge` / `SnapshotSizeExceeded`。该方案的具体状态映射随实现和合同同步冻结。

## 私有 IPC 与身份

新增独立 image-preview capability 与 open kind；旧 Worker 不会因只支持静态或 package 会话而被当作已支持图片背景。实际 Worker 必须匹配当前 engine、基础 profile、图片解码 profile、诊断版本及全部固定预算；open 与 capability 绑定同一实际进程。render/close 继续复用已有 package 会话的受验证消息、完整诊断及进程排水规则，避免另建平行渲染器。

open 保留原会话 ID、完整 source SHA、序号 0 及二进制会话头。头后为严格、拒绝未知/重复字段的图片 envelope：`profile=project-image-preview-v1`、engine、原基础 source profile、原 Color 背景 canonical snapshot bytes、完整背景身份/fit/playback、固定图片 bytes。背景绝对路径不进入 envelope；Worker 恢复为仅供内存 resolver 匹配的逻辑内容地址。

以 Color 替换背景仅用于复用原基础 codec 的完整源检查；effects fingerprint 必须由既有领域规则重算并核对，解码后恢复 Image 的所有 kind、fit、playback、Media 字段，composition 底色和其他源字段完整保留。不会放开原 static/package codec 的路径能力。完整 envelope 包含 base64 扩张后的开销，必须不超过扣除原 open 头后的 4 MiB Worker payload 上限；嵌套基础 snapshot 也保留原 4 MiB 与 200000 source values 门禁。原图至多 2 MiB，解码 BGRA 至多 16 MiB，单边至多 4096，明确拒绝，不截断或缩放。

## Runtime 消费及所有权

Worker 从同一已核 SHA 的 bytes 完整解码出有界 BGRA bitmap，将只接受该逻辑背景身份的内存 resolver 注入原 ProjectRenderSessionFactory。Runtime 主画布、背景 fit、底色、脚本图输入与透明处理沿用原生产规则；不会在实际绘制时重新读取原路径或 staging 文件。损坏图片为稳定 `PackageImageDecodeFailed`，尺寸/解码预算为 `PackageImageBudgetExceeded`，失败不发布黑底替代帧。

resolver/native bitmap 的所有权在 Runtime 会话；工厂必须在无效果、编译失败、准备失败、取消和正常释放时都释放。注入点使用工厂委托，每次 Runtime session 拥有独立 resolver，禁止共享 native 可变对象或把单次对象复用给多个会话。图片 bytes 随固定预览输入和绑定 factory 生命周期保留，不新建 staging 目录或持久资源副本。

既有 ReferencedFactory 保留音频声明租约；图片绑定不覆盖该包装。连续 playback 复用会话，seek/resize/透明切换及 input replacement 使用既有 generation 机制，旧 Worker 与 native bitmap 先排水，迟到帧不发布。create/input 拒绝、主动 cancel、deadline、close、client expiry、工程关闭和 Host shutdown 均需核对独立引用与 Worker 排水。

## 文件边界及验证

本批独占生产代码、必要测试和本文；版本单一来源、web 生成物、统一 API、AGENTS、roadmap、进展由根代理最终整合。生产实现优先扩展已有 WorkerPackageRenderFrameSessionFactory/PackageRenderSession 会话生命周期，不复制新的渲染调度器。

定向验证至少包括：

- codec：static/package 往返、kind/fit/playback/Media/底色与 fingerprint、一切主机路径不入封包、size/SHA/未知或重复字段/profile/预算拒绝。
- Host 授权：仅 ProjectStore pinned closure、链接或改字节拒绝、短期读取引用与工程 close 竞态、Writer 外读取、捕获后原文件变化或工程关闭不影响固定输入。
- Worker/native：普通输出同帧像素等于原 Runtime，背景反事实实际改变普通帧；透明输出保持原 Runtime；腐坏/超预算图片无成功帧；包编译失败、取消、deadline 和 close 释放 resolver/Worker。
- HTTP：现有 project 路由成功与精确修订/ACL/能力拒绝；连续帧、input、seek、resize、透明 generation、close/shutdown。独立 fresh self-contained 探针、全部进程身份排水与凭证扫描由根代理验收。

构建和测试统一使用 `flock -o /tmp/mkg-backend-b4-dotnet.lock`，dotnet 指定 `-m:1 -p:UseSharedCompilation=false`。有限结果不扩大为完整 B3/B4、B5/B6、浏览器、跨平台、性能、长片或 GPU 支持。


## 实施与分层验收状态（2026-10-23）

当前生产调用链为 `PreviewBackendService.CaptureAsync → ProjectSession.CapturePreviewInputAsync/BackendProjectImageReadLease → ProjectImagePreviewSnapshotCodec → IBackendImagePreviewFactory.Bind → WorkerProcessProxy.OpenImagePreviewSessionAsync → PackageRenderSession → ProjectRenderSessionFactory → MemoryImageBackgroundFrameSourceResolver → SkiaRenderPipeline`。静态和直接包基础源完整复用原codec；文件读引用在工程close前排水，固定内存factory继续保留音频reservation包装。新增独立image能力，原静态/package正式任务来源不放宽。

产品单一来源升为0.99.0，合同1.33.0同步统一API§22、runtime-info字段说明、27条增量合成例及Runtime/Skia/Core背景身份源码指纹。路径/方法/schema/工程命令保持87/97/481/91，例子为2848（2175反例），原1.32.1的2821例按完整SHA保持。最终metadata已从稳定当前Contracts重新编译导出（Release工具构建0警告/0错误），freeze/write/check与validate通过，结果记于 `artifacts/project-image-preview-local/contract/`；合同检查只证明离线形状与源码漂移门禁。

已完成一次有限codec变异回归：仅临时移除 Media SHA 与实际bytes SHA比较，既有wrongHash负例按预期失败（24例中红1）；精确恢复后同组24/24通过，源文件恢复前后及绿测后SHA均为 `cf0a27bb5253f594a5aec1a53b3e4098745b8da6b355e0ef9aac8db86b1c144b`。证据位于 `artifacts/project-image-preview-local/mutation/assessment.json`、`test-summary.json` 和原始TRX/log；该24例是codec定向子集，不与后续全组重复相加。

根代理最终0.99.0整合已完成solution构建0警告/0错误，Hosting预览相关152/152、Server预览相关371/371、图片Rendering定向39/39通过；这些是各自有限筛选范围，不声称完整模块或完整solution tests。上述codec24例包含在Rendering39例中，不重复相加。最终Runtime Worker兼容/图片/失控组49/49通过，四个独立测试程序集的本轮有限范围合计611/611（152+371+39+49），原始TRX/log及assessment位于 `artifacts/project-image-preview-0990-acceptance/regressions/`。此前266/10/7等局部运行和mutation组保留各自归属，不重复累加；独立Oracle预检也按其本机证据范围另列。匹配self-contained Server/Worker发布源码 `d961895`、`sourceDirty=false`；fresh HTTP `artifacts/project-image-preview-0990-acceptance/http-final6/` 通过1175请求／9760断言。static/static-analysis/直接package普通与透明各4帧及回退seek、40组原Runtime完整PNG/像素比较、Initialize/Evaluate×cancel/deadline/close六组实际Worker/child控制与排水通过；Server exit0、105个观察身份（含83个Worker身份）全部退出，2752个证据文件凭证扫描通过。

完整矩阵、Oracle输入身份、历史失败及有限边界见[实施§77](../history/backend-extraction-progress.md#project-image-preview-0990)。视频/字体、图片与preset、validation/export/measurement图片输入、完整B3/B4/B5/B6、浏览器/跨平台、性能长片和完整工程验收继续独立。

## 后续独立图片正式验证阶段

图片正式验证另按产品0.100.0／合同1.34.0的[独立计划](backend-project-image-validation-plan.md)推进，匹配发布fresh HTTP待验收；本文件的0.99.0／1.33.0预览结论和历史数字不扩大为正式验证能力。
