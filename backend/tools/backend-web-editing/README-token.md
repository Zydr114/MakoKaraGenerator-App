# S3c 元素结构真实 HTTP 验收

独立 `token_probe.py` 覆盖拆分、合并、重排、手工重切及来源规则重切五type。使用S3a可信启动和自有闭包fixture，不扩大S1/S2/S3a/S3b旧工具的运行范围。

```bash
python3 tools/backend-web-editing/token_probe.py \
  --dist "$PWD/artifacts/backend-web-s3c-0800/deployment-final1" \
  --output "$PWD/artifacts/backend-web-s3c-0800/http-final1"
python3 -m py_compile tools/backend-web-editing/token_probe.py
python3 tools/backend-web-editing/token_probe.py --help
```

须使用支持S3c的匹配self-contained Server＋完整Worker，输出为仓库忽略的 `artifacts/` 下新目录。fixture由本轮真实保存并关闭的模板构造，沿用实际产品/schema版本，新歌词按原始bytes SHA寻址更新 `lyricsFile`。不改用户profile、素材或工程；失败现场保留，重试使用新目录。

主要验收：

- 五type逐次完整读取→undo→redo，历史名称和一次动作一次历史严格核对；再整体undo五步/redo五步比较原/最终全部内容。no-op保dirty、修订、历史、redo与事件游标，`tokenResult`统计为真实零且省略选择。
- merge保左ID、相邻文本、时间并集与同状态；不同状态为estimated，任一Missing则结果Missing；不自动重绑原右元素的悬空注音。merge/reorder不伪造切分统计。
- split按Unicode文本元素中点，左ID保留、右ID新建、两个estimated；固定微秒和5/2 ties-to-even边界。emoji/组合字符不拆坏；完整原注音保留。
- reorder必须显式选择重分时间槽，false时原时间顺序冲突明确拒绝；true按旧合法槽分配到新顺序、改正文与状态，原Missing仍Missing。
- 手工段精确拼回正文且须完整Unicode边界；不估算时旧元素首段可继承完整原时间/ID、其余Missing；估算按原UTF-16文本长度比例分配，新ID与完整原端点精确核对。合并片段沿原规则会新建ID，不能因文字相同假定身份保留。
- none/mixed/perTextElement/latinWords/byPunctuation/byWhitespace六规则使用固定文本/预期段序列；明确来源一次提交，不逐行制造历史。来源 `changedLineCount` 与完整固定前后快照一致，统计按原具体规则判据核对。
- 必填bool/Int32/目标/数组/精确枚举、未知和跨type/重复字段400；20000内非string项400，20001项先413 `command.segments`；无效拼接/空片段/Unicode边界422，稳定failureCode与真实command路径。
- 单行完整1MiB/20000项及来源改变行累计16MiB在提交前拒绝，完整内容/历史/redo/修订/游标不变；20000段加一行仍超过总项预算。旧超大行可以删除修复，40000字旧合并token可合法拆成两个，不能按grapheme数误拒。整个来源累计操作拒绝后，单行同类操作仍可成功并保存重开。
- 五派生DTO全部字段进入回执身份，完整tokenResult/实际新ID重试保持，旧修订重试先于冲突；segments准入后客户端数组修改不能改变已完成回执，修改片段/estimateTimes/重排选择/rule/来源/目标/修订/截止必须复用拒绝。HTTP用例只证明已传输快照和回执身份，真正排队期间外部数组修改由Hosting受控回归覆盖。
- 同修订不同意图真实同时发HTTP只有一个成功；同回执并发只分配一次新token与一次历史。五type只读会话/跨client拒绝，保存关闭重开均等真实成功终态，新session完整正文、ID、时间、注音、来源及设置一致。

为大合法来源保存完整状态，工具在同一精确修订下读取真实limit=1行窗口并组装完整快照；不把默认整页超限误认为整个工程不可用。旧超大行的原始内容使用保留的兼容读取核对，之后删除修复。没有通过修改产品预算或跳过失败来取得成功。

`summary.json`仅在全部断言、完整任务终态、正常终止、观察身份消失及凭证扫描通过后原子发布；扫描包含最终summary精确字节。`http-elapsed.json`仅记录本机短样本，包含客户端校验/日志开销，不作为性能、分配次数或吞吐保证。冻结合同的 `validate.py --http-log` 可独立校验所有配对JSON留档。

此范围不代替浏览器、全量渲染/像素oracle、完整B3/B4、再次导入、性能或人工平台验收。fixture离线构造不表示新增任意对象补丁能力。
