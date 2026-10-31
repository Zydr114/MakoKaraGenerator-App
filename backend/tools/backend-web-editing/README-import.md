# S3d 再次导入与粘贴追加真实 HTTP 验收

独立 `import_probe.py` 验收 `lyrics.importAsset` / `lyrics.appendText`。复用已有可信启动、完整读取、历史和持久化辅助方法，不扩大 S1/S2/S3a/S3b/S3c 旧工具的运行范围。

```bash
python3 -m py_compile tools/backend-web-editing/import_probe.py
python3 tools/backend-web-editing/import_probe.py --help
python3 tools/backend-web-editing/import_probe.py \
  --dist "$PWD/artifacts/backend-web-s3d-0810/deployment-final1" \
  --output "$PWD/artifacts/backend-web-s3d-0810/http-final1"
```

须先得到支持 S3d 的匹配 self-contained Server＋完整 Worker 发布目录。上方目录为示例，不表示发布或 HTTP 已通过。输出必须是仓库忽略的 `artifacts/` 下新目录；失败目录保留，修工具后使用新目录。

本轮从真实上传、Worker 准备、创建、保存及关闭的模板构造自有 fixture。沿用实际产品/schema 版本，将歌词原始 bytes 按 SHA 更新不可变 `lyricsFile`，保留媒体闭包和原始输入。离线 fixture 仅为测试准备，不代表新增任意工程补丁 API。工具不接触用户 profile、工程或输入。

主要断言：

- 文件替换 existing 来源的全部行，保留名、角色、语言回退、时间策略及完整持久化样式；三种 newBranch 角色都使用原 Trim 名称和 Independent 策略。文本追加 existing 或新 Translation 来源，后者 FollowPrimary；CRLF/CR 归一、跳过空白行、其他空白和组合字符/emoji 原值保留，元素未定时。
- 八导入器：普通/增强 LRC、纯文本、SRT、WebVTT、ASS、UltraStar、TTML。逐项核对正文、元素覆盖、精确行/元素微秒和状态；八份完整报告与固定独立旧/新共享用例 oracle 逐值比较，不只比较摘要。固定值内嵌工具，无运行时 artifact 依赖；日志记录 oracle/生成程序 SHA。媒体时长固定六秒，与原 oracle 一致。
- 报告保留所有 27 个合同字段的原值（允许省略无值字段），全 issues、检测理由、候选、丢弃特征、偏移字符串、编码/BOM/换行及所有数量；计数与完整实际行/元素一致。部分合法 TTML 加结构错误仍成功，Error/零行号/rawText/message 不丢失；完整诊断回执重试精确相同。
- options 省略、默认空对象、nullable importer/encoding 和未知值自动回退；显式格式覆盖自动检测，六种切分规则与显式估算选项。单列 LRC 强制编码修复：GB18030 自动保持中文，显式 UTF-8 按实际 replacement decoding 生效；不把本次业务修正混入默认结果兼容声明。
- 必填值、目标严格 union、命令与目标组合、命名枚举、options 出现必须 object、可空字符串/不可空 bool 与 splitRule、未知/跨 type/重复字段完整拒绝。空结果返回原 emptyLyrics 与稳定原因；真实 command.* 字段和 failureCode 省略规则精确核对。
- 每个新意图一次修订/事件/历史，历史名称准确；逐次 undo/redo 恢复全部内容。新意图生成新的不冲突 ID，同回执旧修订重试保持原完整报告、选择和 ID，undo 后也不重新解析/分配；目标、角色、名称、文本、asset、options 全派生字段、截止及修订均进入回执身份。options 省略与空对象的指纹保持实际区别，未知值虽业务回退仍保其输入指纹。
- 真正同时发 HTTP：同修订不同意图仅一份提交，另一份可因无队列名额 429 或精确修订 409 被拒；相同回执并发返回相同完整结果且仅一次提交。工具不推断请求在内部 checkpoint 的先后关系。
- 两 type 的只读/跨 client 会话拒绝，外 client asset、错误 MIME 与资源完整性拒绝。只临时改变本轮上传资源副本的同大小 bytes，证明原 SHA 被核验；恢复副本后以新意图成功，原始输入从未修改。
- 真实输入覆盖 64MiB 累计逻辑工作、完整 report/receipt、单行 1MiB/20000 项、改变行累计 16MiB 的 413。所有拒绝精确比较完整内容、summary、history、dirty、redo、revision、eventCursor，重试稳定且不截断报告；随后合法长单 token 与普通命令可成功。64MiB 为逻辑准备工作配额，不是 CLR 堆或进程内存硬上限。
- 目标 ID128 接受/129 拒绝；原未触行/元素 ID129 不被追加扫描误拒，替换产生合法新 ID。保存→关闭成功终态→旧 session 404→新 session 重新打开，所有可读投影及选择所指稳定 ID保持，随后再次关闭。

为大合法来源保留完整同修订快照，复用 limit=1 行窗口读取。`inputs/` 保存原始上传 bytes，`input-manifest.json` 记录 SHA、大小和 MIME，并在结束时重新核验。授权凭证只存在内存，HTTP 留档脱敏；扫描包含最终 `summary.json` 精确字节。失败不发布 passed summary。

工具等待所有真实任务成功终态、正常 SIGTERM 排水及所观察身份消失后才完成。观察身份日志不证明每个瞬时子进程均被采到。`http-elapsed.json` 为本机短样本，含客户端校验/日志开销，不是性能、分配或吞吐验收。冻结合同生成后可使用 `tools/backend-api-contract/validate.py --http-log` 独立验证配对 JSON。

内部 checkpoint 的并发编辑/关闭、准备名额耗尽、等待方断流不取消 owner、shutdown 等待 owner/cleanup 由受控 Hosting 回归证明；本工具不把普通 HTTP 竞态或正常空闲终止扩大成这些内部时序证明。此范围亦不代替浏览器、完整渲染/像素 oracle、完整 B3/B4、波形、全量性能或人工平台验收。
