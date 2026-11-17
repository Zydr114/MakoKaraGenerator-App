# TTML 歌词完整导入修复计划

日期：2026-11-16。状态：已实现，待 Luker 用真实样本做最终核对。基线：`08524f1`。实现分支 `cursor/ttml-import-completion-cec9`。产品 **0.114.0**，合同 **1.55.0**。真实 56 行歌词不在仓库中，提交的是同统计形状的合成样本。

## 目标与约束

- 原始样本无需手工修改即可导入：56 行、两个歌唱者分支（主唱 53 行、和声 3 行）、113 组注音、255 个带时间的注音片段；原始正文、片段文字和时间逐项核对。
- 标准 TTML 的命名空间、agent 引用、正文、ruby 结构与样式引用、相对时间树、顺序/并行容器及可确定的帧/tick 时间语义正确进入歌词模型；以规范和独立预期断言验证，不以导入成功代替语义正确。
- “标准歌词导入”不等于 TTML 全部视听表现引擎：区域排版、动画、图像/音频嵌入及需要外部同步上下文的时钟不能伪造为歌词数据。无法表达的特性明确诊断；可表达的正文与时间不得静默丢失。最终记录逐项覆盖及剩余限制，不宣称任意 TTML 文档全量无损。
- AMLL 特定兼容独立于标准时间语义。严格项目 profile、身份、schema、原始时间往返不降级；不修改原文件或兄弟仓库。
- 工作树已有大量跨平台改动，保留并按本次路径暂存。正式合同冻结与匹配发布在本仓库的隔离工作树核验，避免把无关改动归入本次交付。

## 规范依据

- W3C Namespaces in XML 1.0 §6.2：`xmlns=""` 清空默认命名空间。
- W3C TTML2 §5.1、§8：元素命名空间和内容结构。
- W3C TTML2 §14.1.3、§14.2.1：agent 定义与 IDREFS 引用；多歌唱者不能简化为固定 v1/v2。
- W3C TTML2 §10.2.35 及样式关联规则：simple/complex ruby，base/text 容器与多组关联。
- W3C TTML2 §12.3、§12.4、附录 I：时间表达式、时间容器、有效时间区间。
- AMLL 编辑器实际解析模块用于兼容行为对照，不替代 W3C 规范。

## 实施步骤

### 1. 核心歌词语义

- 对明确 AMLL/Apple 标记的、结构可确认的空命名空间内容进行受限内存规范化，报告兼容处理；未知命名空间、冲突或项目 profile 保持严格。
- agent 定义、继承、单行/行内覆盖与多 ID 合唱映射为具名分支；保留独立计时，明确未定义引用和旁轨处理。
- 支持标准简单 ruby、成组 ruby、样式引用和现有 AMLL ruby；对模型无法无歧义表达的结构明确诊断。
- 原生 AMLL 只有注音片段时间时，按有界片段范围推导正文时间并标记 Estimated；显式项目 profile 的 Missing 状态原样往返。
- 审核标准 media/SMPTE 可确定时间、seq/par、裁剪、隐式时长及精度。保持 AMLL absolute 与标准相对时间分离。

### 2. 完整导入工作流

- 新建工程保留完整解析文档；已有工程提供明确的全部来源导入入口。
- 全部分支导入为一次原子编辑、一次撤销，完整重映射行/token/ruby/segment 与跨分支引用；保留 CAS、配额和原有单来源导入。
- 默认不静默覆盖用户已有分支；UI 明示操作结果与导入报告。

### 3. 回归和合同

- 先执行新增失败回归，再修复验证；合成样本可提交，真实完整歌词仅本机验收。
- Core：原始样本统计、文字和时间；标准 agent/ruby/style/time 用例；错误输入、DTD、预算、project 往返。
- Application/Hosting/Server：全部来源原子采用、ID/关联、撤销重做、保存重开及旧请求兼容。
- Frontend：类型检查、单元回归、实际 HTTP 工作流与浏览器导入交互。
- 递增产品/合同版本，真实 CLR metadata 生成合同、freeze/check/validate，并同步前端副本，不手改生成产物。

### 4. 主代理验收与交付

- 审阅子代理全部 diff；定向测试通过后执行后端解决方案零警告构建。
- 匹配 Server/Worker 发布；用原始样本与标准 fixture 进行 fresh HTTP、保存重开与前端验收。凭证不进入日志或 URL，仅清理本次拥有的进程。
- 记录实现、单元、合同、发布、HTTP、浏览器证据及限制，各层结论独立。
- 每步中文 `type(scope): 摘要` 提交并推送获准的 App 私有远程。

## 当前结果

2026-11-16 已在 App 仓库实现，未合并。

- 空命名空间：仅当文档有明确 AMLL/Apple 标记、且空命名空间元素是可确认的 `body/div/p/span/br` 时，在内存中恢复为 TTML 命名空间并写入兼容报告。项目 profile、无标记文档和认不出的本地名保持严格。
- 歌唱者：`ttm:agent` 的定义、继承、行覆盖、行内覆盖和多 ID 合唱映射为独立计时的具名分支。未定义引用单独警告并保留正文。`x-bg`／`x-translation`／`x-roman` 进入和声、翻译或注音来源。没有这些标记时，多个 `div` 仍合并为一个来源。
- 注音：标准简单 ruby、样式引用上的 `tts:ruby`，以及原有 AMLL container/base/textContainer/text 可导入。多个 base 无法唯一绑定时诊断 `rubyAssociationAmbiguous` 并保留正文。原生 AMLL 在基础元素没有独立时间、且每个片段都有合法时间时，用片段最小起点和最大终点推导正文时间并标为 Estimated。项目档的 Missing 原样往返。
- 时间：media 相对时间与 AMLL absolute 仍然分开。non-drop SMPTE 按帧率换算。`clock` 与 drop-frame 拒绝。
- 导入：新建工程采用完整解析文档。已有工程的 `destination.kind=allSources` 把全部分支追加为新来源，一次编辑、一次撤销，重映射行/元素/注音/片段和一起导入的跨分支关联，不替换已有来源。旧的单来源 `sourceBranchId` 请求不变。歌词页多来源文件可以选择“导入全部来源（不覆盖已有来源）”。
- 版本：产品 0.113.1 → **0.114.0**，合同 1.54.0 → **1.55.0**。合同由 CLR metadata 经 `freeze.py --write` 与 `validate.py` 生成，前端 `docs/backend/contract` 是同一份副本。

### 本机用真实样本核对

仓库不包含那份 56 行真实歌词，也不要把它写进仓库。合成夹具是 `backend/tests/MakoKaraGenerator.Core.Tests/Lyrics/Fixtures/synthetic-amll-two-singers.ttml`，只复现 56 行、主唱 53、和声 3、113 组注音、255 个计时片段的形状。

在本机对真实文件：

1. 用它新建工程，确认两个歌唱者来源、行数、注音组数、计时片段数，以及正文和时间与原文件一致。
2. 在已有工程里用“导入全部来源”，确认原有来源没被替换，撤销一次能一起拿掉新来源。
3. 定向测试：`dotnet test tests/MakoKaraGenerator.Core.Tests --filter TtmlImportCompletionTests`。

### 本环境已核对

- Core 全套 1586 项里 1585 通过。`AutoSaveStoreTests.备份比工程新才算有现场` 在整套里失败、单独运行通过，是备份和工程文件落在同一时间戳粒度上的环境现象，与这次歌词改动无关，没有改它。TTML 相关 69 项通过，其中包括合成样本的独立计数、文字和时间断言。
- Application 全套 750 项通过。其中 `TtmlImportEditTests` 覆盖全部来源的一次撤销和跨分支重映射。
- Hosting 全套 1682 项里 1666 通过、16 失败。失败都是环境：真实工具要 systemd/cgroup 或系统里的 Noto CJK 字体，真实 Worker 测试报“包 Worker 未提供当前验证所需的能力”。其中包含事先声明不修的 `BackendMediaFeatureServiceRealToolTests` 和 `BackendAuthorV2WorkflowTests`。全部来源命令本身的 2 项通过。
- Server 全套 550 项里 544 通过、6 失败。3 项 `RubyTimelineTransportTests` 要求先发布匹配的 Server/Worker，本环境没有这份发布。另外 3 项缺 `/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc`。新增的 HTTP 全部来源测试通过：导入、撤销、再导入、保存后用 `ProjectStore.Load` 读回注音关联，以及旧的单来源请求。这个夹具的音频不是可解码文件，HTTP 重新打开会话会在音频准备失败；歌词文件本身可以读回。
- 合同 `validate.py`：115 路径 / 128 方法 / 673 schemas / 4000 例（2881 反例）。
- 前端 `tsc` 与 `vitest` 单元测试 4563 项通过。合同同步后比同步前多 5 个合成例。
- 桌面 `node --test`：31 通过，2 项因非 Windows 跳过。

### 未能在此环境核对

- 用真实 56 行歌词逐项核对。
- 浏览器里点选导入。无头 Linux 没有做交互验收。
- 匹配的 Server/Worker 发布和 GPU 路径。
- 后端全量测试里原先就会失败的 4 项保持不动：`BackendMediaFeatureServiceRealToolTests`、`BackendAuthorV2WorkflowTests`、`RubyLegacyBaselineTests`、`BoundedEncoderOutputTests`（cancel: True）。

### 仍然不能称为任意 TTML 无损

区域排版、动画、图像/音频嵌入、需要外部同步的 clock、drop-frame，以及模型无法唯一表达的注音结构，仍然只诊断，不写进歌词。合唱是一个具名分支，不会把同一行复制到每个歌唱者身上。
