# 静态样式真实 HTTP 验收

独立 `probe.py` 复用已有编辑探针的可信启动、实际 Worker、自有持久化闭包、历史和正常排水辅助方法，保留全部旧工具的原运行范围。验收单目标 `GET .../style` 与 `styles.patch` / `styles.restore` 两命令。

```bash
python3 -m py_compile tools/backend-style-http-probe/probe.py
python3 tools/backend-style-http-probe/probe.py --help
python3 tools/backend-style-http-probe/probe.py \
  --dist "$PWD/artifacts/backend-static-style-0830/deployment-final1" \
  --output "$PWD/artifacts/backend-static-style-0830/http-final1"
```

只接受仓库忽略的 `artifacts/` 下新输出目录，失败证据不覆盖。须使用主目录的匹配 self-contained Server＋完整 Worker；上方路径仅是运行示例，不能据此声称 HTTP 已通过。仅使用标准库与本机 `/usr/bin/ffmpeg`；正常 HTTP 不需要 .NET SDK 或编译额外 fixture helper。

每次运行先真实上传音频/歌词、Worker 准备并创建工程，再保存、关闭和重新打开模板。从本次运行实际保存且关闭的模板克隆自有 fixture，沿用其真实 application/schema/template 版本；不会复制旧版本工程后改版本绕门禁。新增歌词 bytes 按 SHA 更新不可变 `lyricsFile`，媒体保完整闭包。不接触用户 profile 或素材。

## 原始覆盖与继承预期

工具含独立 Python 属性模型，不调用产品样式解析器生成预期。它维护 project baseline 和 branch→line→token 的原始属性 presence，分别断言完整 `baseline`、`local` 和 `staticHierarchy`，并核对 session/revision/target、协议和 `resolution`。project 的 local 必须空，其他层只包含实际设置成员；显式字体 null、关闭描边/阴影的 null 必须保留 value。

自有来源含 primary、secondary、同可见正文的其他来源、129 字符 ID 及带前后空格的来源/行/token。长 ID 合法，trim 后 ID 不得匹配。token fixture 只使用原允许的材质、描边、阴影和 opacity，避免依赖既有非法 token 字号来证明新能力。

主要覆盖：

- 四层与全部 24 属性；字体族/回退链/字重（含合法非整百值）/字距、双填充 canonical solid/gradient、完整或局部描边/阴影、位置、对齐、透明度及缩放。元素不支持的字体、位置、对齐和缩放明确拒绝。
- 每次 patch/restore 完整读取→undo→完整读取→redo→完整读取；一个 HTTP 意图只一份历史。局部 width、blur 等不冻结上层材质或其他成员；未选样式及全部正文、时间、注音、关联、来源、设置、已准备 waveform 身份保留。
- 显式 null 关闭效果、restore 关闭对象、关闭对象的 leaf restore no-op、之后写 leaf 重新成为部分继承；字体 null 与移除覆盖有不同原始 presence。恢复最后成员后不留下空覆盖对象。
- 渐变保全部几何、coordinate/colorSpace/huePath 和 RGBA/stop 次序，覆盖 linear/radial 及各枚举。相等 offset 合法；坏端点、逆序、少于两 stop、非法径向几何拒绝，不排序或修补。
- 新数组值相等的 gradient/fallback、等值 scalar、已恢复字段和关闭对象的 leaf restore 都保真 redo、dirty、revision、history 和 eventCursor。工程 baseline 无变化保 preset-origin 标签，真正变化才清标签。
- 严格 target/property/value union、必填 value/null 语义、同 kind 唯一目标、空/重复/父子冲突、未知或跨 type 成员、精确枚举和 JSON 数值类型；无效领域值/第二目标使整批拒绝。所有错误比对完整可读状态及真实 field/failureCode。
- 原回执旧修订重试、undo 后重试、目标/顺序/数组/渐变 stop/阴影/restore 变化、截止或修订改变复用拒绝；不同意图同修订真正并发仅一个成功，相同回执并发只一份历史和相同完整结果。HTTP 不假称控制了 Writer 内部排队数组变更时序。
- 四层读取跨 client 拒绝，两写命令跨 client/只读拒绝，只读读取成功；精确旧修订的读取和新意图写入拒绝。

## 预算与旧数据

新输入限额以 canonical command JSON 为准：64KiB，不含 request metadata。UTF-16 字体名 256（含 astral 边界）、32 回退链、256 色标都有真实正负对照；1000 个真实合法目标成功与 1001 形状拒绝分列。取其中 600 自有合法来源使用 64KiB 内意图，但 changes 数组字节数×目标数超过 16MiB，必须原子 413；1000 目标小 patch 批量成功、undo/redo 后完整 ID 反馈仍真实。400 个自有 Unicode 来源触发 16KiB changeSet 完整刷新兜底，保 areas、清空全部 ID 数组，不回部分 ID；响应及完整回执仍有界。

旧合法 257-stop gradient 可完整 GET、patch 别项、恢复继承及保存重开，不套新输入限额。另有原始大字体和大 stop 集合，完整 GET 分别触发 1MiB/20000 预算；失败、无变化恢复意图及新写入拒绝不能损伤历史/redo/dirty/revision/events，真正移除大覆盖可修复并重开。对这些本来就读不出的旧目标，状态记录真实 413，使用保留的原始 fixture 和其他完整投影核对边界，不把 413 伪装成完整样式读取。

## 高级来源保留与预览输入衔接

高级 fixture 从本次模板生成，复制仓库 tracked `samples/ScriptEffects/GlassInk` 的完整资源闭包，仅将清单可读 name/label 改为确定 ASCII。逐文件核 SHA，按 `EffectPackageContents` 长度前缀规则生成内容修订，所有包字节进入自有工程。固定修订已用既有发布 Core 的独立离线 `ProjectStore.Load` 核实合法；该离线对照不替代本次匹配版本的真实 HTTP 打开。

fixture 保一份高精度参数 `0.12345678901234567` 和合法固定 static preset application；GET 只读 package identity 不暴露参数，`hasPresetApplications` 只提示工程存在快照。独立模型仍只解析四层静态样式，不冒称实际 preset/node/clip 最终外观。字体/局部几何修改、明确选中槽整体替换及 undo、保存关闭重开后，未选 package 参数精度、全部包文件、固定 preset、场景、效果和分析原字段完整保留。此流程不编译/执行包，也不代表开放 package 上传或编辑。

普通静态工程另实际创建 preview，styles.patch 明显改变填充颜色后显式 `PUT .../input`，请求并完整解码 PNG，证明像素确有变化；undo 后再次显式更新 input，同一时刻像素必须精确恢复。原始/修改/恢复 PNG 与 RGBA SHA 留档，所有 binary 请求响应成对。这里只证明固定静态渲染输入衔接，没有独立 Runtime 像素 oracle，也不替代新 profile、上下文渲染、浏览器或完整 B3/B4 验收。

## 证据

所有真实 job 等成功终态，preview 显式关闭，最后 SIGTERM 正常排水并核 launcher/server 退出及所观察 PID+startTicks 身份消失；观察列表不声称覆盖每个瞬时子进程。bootstrap/access/resume 保内存，HTTP 留档脱敏。`input-manifest.json` 结束时重新核原始上传/包输入 SHA；`evidence-manifest.json` 固定此前文件，凭证扫描包含 manifest 和最终 `summary.json` 精确 bytes，全部通过才原子发布 passed summary。

冻结合同 ready 后可用 `tools/backend-api-contract/validate.py --http-log` 独立校验全部 JSON，PNG 的 MIME/完整 SHA/大小/解码另核。`http-elapsed.json` 是包含客户端校验/日志开销的本机短样本，不是性能、吞吐、分配或总堆上限证明。内部受控生命周期/排队竞态仍由 Hosting 回归归属。
