# S3a 正文、行操作与工程设置真实 HTTP 验收

独立入口仅针对 S3a 的11个命令，不运行或启用 S3b 来源/导入命令。复用同目录 S1/S2 工具的可信启动、自有 profile、真实 Worker 音频准备、脱敏 HTTP 留档、精确投影、终态 job 等待、正常退出和进程身份排水；不修改原探针的运行范围。

使用支持 S3a 的匹配 self-contained Server＋完整 Worker，以及仓库 `artifacts/` 下**不存在的新输出目录**：

```bash
python3 tools/backend-web-editing/content_probe.py \
  --dist "$PWD/artifacts/web-editing/dist-s3a-final1" \
  --output "$PWD/artifacts/web-editing/http-s3a-final1"
python3 -m py_compile tools/backend-web-editing/content_probe.py
python3 tools/backend-web-editing/content_probe.py --help
```

fixture 都从本轮真实 HTTP 保存并关闭的模板复制，沿用实际产品/schema版本，重新计算不可变歌词 SHA 并更新 `lyricsFile` 引用。保留模板和旧歌词，不访问用户 profile、工程或素材；所有媒体只属于本轮输出。大输入采用顺序合法 `Imported` 元素和精确拼接正文，避免无关的海量缺失诊断干扰打开任务账本。少量原非法时间和ID耗尽输入为专门拒绝用例，不能解释为合法可导出工程。

验收内容：

- `lyrics.setLineText`：不 trim、不规范化 Unicode，保留空格、换行、组合字符及家庭 emoji；公共前/后缀内完整元素保原 ID/时间/状态，真实重切统计与读取结果一致。两端都改变时按原共享规则重切整个中间区域，保留真实悬空注音；同值反馈0/0、no-op保 redo。大合并元素后追加不按原grapheme数误拒；空正文同样保留原注音。
- `lyrics.setRubyText`：现有注音保持 ID、基础元素、语言、比例、间距和对齐，文字精确保留；空串删除，新增返回真实注音，反馈不臆造选择或映射，无独立发音时间。
- `lyrics.addLine/duplicateLine/removeLine`：未定时空行、新行/元素/注音稳定身份、完整复制和注音基础字重映射；副本按原行时长后移，可以超过音频且报告真实下一行重叠。建议选择、插入位置、删除身份、下邻行选择和删空来源的显式清选区均严格核对。
- 六个 `composition` 命令：奇数画布尺寸、有理帧率GCD约分、完整RGBA、超JS安全整数输出时长、显式 `null`、采用当前有效歌词末端/真实准备音频时长。修改后逐个查询/undo/redo；同值/no-op保持历史、dirty、修订、redo及事件游标。
- 输出时长 `null`/读取省略表示**尚未确定**，导出前须显式设置；采用歌词或音频时长都是显式命令。若部署export能力可用，用独立全定时、偶数画布、合法帧率输入先完成正时长导出成功终态，再仅设置null并严格断言 `exportInputRejected`，避免用其他领域/媒体/编码门禁冒充时长拒绝。此对照没有独立像素oracle；能力关闭则明确记为未运行导出门禁，不虚报该项通过。
- Prepare/CommitPrepared的HTTP可见边界：正文/注音字段自身超预算、20000项、新重切最终单行1MiB、转义字节预算均413且完整状态不变，保留redo；同样不能复制超预算旧行，但允许删除后保存重开修复。HTTP证据不单独证明内存分配次数或性能，提前分配回调和纯准备语义由对应Hosting/Application回归负责。
- 严格必填/显式null/数值/溢出/嵌套/重复/跨type字段；领域原因在PascalCase `failureCode`，`field`为真实 `command.*` 路径；未知type不开放。只读会话11type拒绝，跨client/不存在会话中立404。
- 11个type完整回执重试，覆盖各派生字段、目标、所有RGBA分量、帧率及微秒规范化、null与省略afterLineId等价、截止时间和修订；重试必须返回原完整feedback/ID，改变意图不得复用requestId。正文/时间同修订的真实并发仅一个提交；不同新增意图同修订和相同复制回执并发均严格检查一次分配/历史。
- 明确编辑FollowPrimary来源保持原主来源内容和关联；保存/关闭/打开全部等真实终态，重开后新session完整来源、正文、元素/注音元数据和标量设置与原投影一致。

`summary.json`只在全部断言、正常终止、观察到的身份消失、凭证扫描通过后原子发布；扫描包含最终summary的精确字节，计数包含该文件。`http-elapsed.json`是本机短样本，普通调用含客户端校验/日志开销，不设性能门槛或吞吐保证。失败保留原输出目录，新运行换新目录。合同工具可通过 `validate.py --http-log <输出目录>` 校验全部配对JSON留档。

本工具不代替浏览器、S3b、完整B3/B4、完整样式/场景作者、像素oracle、性能或人工平台验收。HTTP投影本身未包含的样式值由对应领域/持久化回归验证，不扩大为本工具已独立核对。
