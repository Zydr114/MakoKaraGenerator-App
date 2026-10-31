# TTML/ruby 时间轴独立审计

日期：2026-10-18～2026-10-19
依据：[实施计划 P0–P6](../plans/backend-ttml-ruby-timeline-plan.md)、ADR-0233。  
最终生产审查基线：`8a000a33a1ac17026b4da84ec42a6774dd9851e7`；发布源码核验基线：`71d39bda05c3d977b7cd4d2dcabc9d1f4fb8a3bc`；产品与 HTTP 验证源码提交：`4fe780912e19d6c04b4214709e5658c251a46bcf`。随后入口文档闭合提交为 `e6847b4`；`38f2fa9` 修复 v17 长 ruby ID fallback 并完成 256 字符持久化回归。本次验收的生产源码基线为 `38f2fa9`，后续为文档提交。

## 1. 结论与边界

本次独立审查发现的生产缺陷与损失报告漏项均已修复并复验。最终审查范围内没有未解决的生产阻断。此结论只对应下面列出的源码检查、Core 实际执行、官方消费端解析及独立渲染定向测试；P6 的最终 solution、合同冻结、自包含 HTTP/Worker 整体验收已由主实施记录[§59](../history/backend-extraction-progress.md#ttml-ruby-timeline-0960)另行关闭，本文仍只作为独立证据。

审计者未参与业务实现；发现问题后由原业务负责人修改生产代码。本审计只新增独立 `TimedRubyComplexClusterTests.cs`、本记录及本地 `artifacts/ttml-ruby/independent-consumer/` 证据。不复制或修改官方 AMLL parser，不把产品自读自写当作外部消费证据。

00:24 后生成的三组 HTTP 目录（`39dfe4…`、`7a28d1…`、`ba265c…`）是同一发布输入的后续重跑，虽能独立通过合同校验，但未生成对应最终 summary/TRX；本审计不把它们计入权威 HTTP 关闭数字。

## 2. 已发现问题和修复复验

| 项目 | 原始实际行为 | 修复 | 独立复验 |
|---|---|---|---|
| AMLL 注音被展平 | token 普通 span 包住 ruby container，官方 parser 得到正文 `詮せん`，没有 ruby tags | `506e8c2`：AMLL 的 container 直接作为 p 子节点；project 保留身份 wrapper | 真实 Core 导出得到正文 `詮`，两个 ruby tags 及各自时间完整进入官方结果 |
| 消费端时间/文本损失未说明 | Missing ruby 消失、微秒变毫秒、base 时间被 ruby 范围替换，原 Plan 未说明 | `506e8c2`：增加对应数量与位置；XML 仍保留微秒与 base 原时间 | 正常/缺失/非整毫秒三个实际导出，消费结果与 Plan 损失逐项断言 |
| 损坏兼容投影使整个工程不可读 | v17 `ruby.text=""` 而两个 segments 有效，ProjectStore.Load 返回 Project=null，只有“歌词源材质读取失败” | `29465ef`：setter 可保留空兼容投影，工厂仍拒绝有效新建为空；mapper 保留两份数据并诊断 | 使用 ProjectStore.Save 的真实工程，按实际内容哈希发布损坏歌词修订后 Load 返回非空 Project，保留错误诊断与原 segments |
| 结构缩进破坏 seq | body seq 中由定时 p 推导结束的 div，多一个结构换行缩进就报 seq 前项时长不确定并返回空文档；compact 成功 | `e045f43`：时间树忽略 body/div 结构格式空白，保持 p/span 正文语义 | compact/pretty 同源输入均得到两行，绝对时间 0–1s、1–2s，结果完整比较相同 |
| 普通未计时元素与 JS 大整数损失漏报 | A Missing + B timed，官方 Lyric API 的 words 只剩 B；long 上沿整毫秒发生整数精度变化，原 Plan 仍没有精度损失 | `8a000a3`：增加无时间普通元素计数，消费端精度检查加入 JS 安全整数毫秒边界 | 两组真实 Core 输出经官方 API 验证；普通元素损失 1，大整数行/元素/注音精度损失分别 1/1/2 |

AMLL 的 base/container begin/end 不被该官方版本的 ruby parser 用作 word 时间，给 container 增加属性也不能改变这个消费行为。示例中 XML base 保持 27000–28000ms，消费者 word 使用 ruby 范围 27820–27950ms；该差异现在是已报告损失。Missing 段不伪造时间，XML 保留原文本，但消费者会忽略它。`WrittenRubySegmentCount` 统计 XML 实际写出的段，`RoundedTimeCount=0` 表示导出器没有量化 XML；两者不冒充消费端完整保真计数。

## 3. 官方 AMLL 消费证据

来源：[官方格式文档](https://amll.dev/en/guides/lyric/ttml)、[官方源码仓库](https://github.com/amll-dev/applemusic-like-lyrics)。直接使用 npm 官方发布包，依赖由 lockfile 固定：

- `@applemusic-like-lyrics/lyric@1.1.0`：`sha512-LZ2meyramTaaDfmCwCjqMxpPy8Vq9tw5b5ueFF85Q7NOIMovwey28Ln/04nN9TNPKp3VRKpmlvPbIXnCFWkj6w==`。
- `@applemusic-like-lyrics/ttml@1.0.1`：`sha512-xhLajMI9Jm+Etv99GbK5Fx8wqa5m9wnS3kCqOTBUy9imjsah2PrV1XyCc6LBssPhEFcQt2SrFNQlwT6GZwpY7A==`。
- `@xmldom/xmldom@0.9.12`：`sha512-5AXjrcMClTryPe9LgZrygpB1lj7s0S9E0+W+AHaVKAVyHanafK86iPSvG5xHVSp/jC+VH1UXu0TAEmY279xH7A==`。

实际调用两个官方入口：`TTMLParser({domParser: new DOMParser()}).parse(xml)` 与 `@applemusic-like-lyrics/lyric.parseTTML(xml)`；后者直接使用同一官方 TTML 库，不算两套独立实现。Node `v26.11.1`；仅注入 `@xmldom/xmldom` DOM 实现。

| 实际执行模块 | SHA-256 |
|---|---|
| `@applemusic-like-lyrics/ttml/dist/amll-ttml.cjs` | `db116b282ae6d2ec47f4647a11da0effe84b93e107d0bb8e9e7579363147d062` |
| `@applemusic-like-lyrics/lyric/dist/amll-lyric.cjs` | `7cab1e257aacb921cdbac311b11534b6b35cbdeca64c86d0f7325112b51685e0` |

### 3.1 实际样本及断言

五份 XML 全部由本地 C# producer 构造 Core 领域对象并调用真实 `TtmlAmllExporter.Export` 生成，未用手写 XML 替代产品输出。官方解析结果保存为 `*-official.json`，同时包含底层 parsed 结果及公共 Lyric API 结果。

| 样本 | 已断言的消费者实际行为 |
|---|---|
| `exported-amll` | 正文 `詮`，ruby `[せ,27820,27880]`、`[ん,27880,27950]`；base 原时间独立保留在 XML，消费者替代已报告 |
| `missing` | XML 两段均保留；消费者 ruby 只剩 `ん`；Plan 无时间注音损失 1 |
| `microseconds` | XML 27820.499–27880.500ms 原值；消费者变为 27820–27881ms；Plan 注音精度损失 1 |
| `missing-token` | XML/底层 line.text 为 AB，公共 Lyric words 只剩 B；Plan 普通未计时元素损失 1 |
| `large-ms` | XML 整毫秒原值 9007199254741001–9007199254741005ms，消费者变为 9007199254741002–9007199254741006ms；Plan 行/元素/注音精度损失 1/1/2 |

同一实际执行还断言：compact/pretty 的标准 seq 结果完全相同，以及空投影损坏工程仍可载入并保留诊断。最终 producer build 零警告/错误、producer 与 `verify.cjs` 均 exit 0。

| Core 实际 XML | 输入 SHA-256 | 官方两个入口结果 JSON SHA-256 |
|---|---|---|
| `exported-amll.ttml` | `b8e43680f972363aa4f743d6b340e83f440e273592953e9189e44813fbd2e752` | `18047e110f44f6fea6c4f005d6602b8da1c0c201442e4b5ab43e8262d2be653b` |
| `missing.ttml` | `bce43e1d269e07a9e754591f0988cc988d1f5db9ab1be0b89c6759cd704480b4` | `d3ba75e6b18cf56b85ba52aec9a4f4855ff9b1c5eb5558c3b271ef1b0ae9289d` |
| `microseconds.ttml` | `6d4350549909d9f9ee9d14996cb9d2d3bc960c9a24a4bc80b00aa9ad6b44c426` | `1d0df74765c0526036239c878769185fdf71ef225d3d14ac8aab313dd6f6053a` |
| `missing-token.ttml` | `441062d5d28049abab71394eaed53e406e3f443e8a73e25b268a66014dd931fe` | `f3ee820ea57764d016627645155c1244f24d68146eb35cf39b4ab9abacf1440c` |
| `large-ms.ttml` | `943e865e29a59e6c9376f4b39bc878709c1a6a3a4e4d2cc9dcaf6fcc77fe84b0` | `4e4bea04609b5b5b4c088aa1a0ed2c7bfb3d5b54a9c7c43ad097c0900acf16ee` |

本地完整证据：`artifacts/ttml-ruby/independent-consumer/` 的 `Program.cs`、`Producer.csproj`、`verify.cjs`、`producer-build.log`、`producer.log`、`verify.log`、`consumer-hashes.json`、`independent-manifest.json`。manifest 固定参与检查的源码、版本 props、producer 与输出哈希；外部 node_modules 不进 Git。该目录属于本地证据产物，文档中的哈希用于核验本轮留存。

重放命令（在仓库根目录；先按 amll-consumer/package-lock 安装依赖）：

```bash
flock -o /tmp/mkg-ttml-dotnet.lock dotnet build artifacts/ttml-ruby/independent-consumer/Producer.csproj -m:1 --no-restore
dotnet artifacts/ttml-ruby/independent-consumer/bin/Debug/net10.0/Producer.dll artifacts/ttml-ruby/independent-consumer
node artifacts/ttml-ruby/independent-consumer/verify.cjs
```

## 4. 独立复杂整形验收

新增 [TimedRubyComplexClusterTests.cs](../../../tests/MakoKaraGenerator.Rendering.Tests/TimedRubyComplexClusterTests.cs)：4/4，通过。固定 DejaVu Sans 的 `fi` 与阿拉伯文 `لا`，每组普通/透明。先验证真实 shaper 没有缺字，并实际生成一个 UTF-16 长度 2、包含单 glyph 的 draw unit；再让两字符属于不同时间的两个片段，按 cluster 起点归属与整段参考进行 BGRA 全字节及 ruby draw units/world ink corners 对比。取样 0、150000、200000、500000、800000、回退 200000µs，全等。

字体 `/usr/share/fonts/TTF/DejaVuSans.ttf` SHA-256：`6038a160b491e121c1f12c7bccb4a9c8730296e3adc1086a059404ed84b7451c`。

定向命令：

```bash
flock -o /tmp/mkg-ttml-dotnet.lock dotnet test tests/MakoKaraGenerator.Rendering.Tests/MakoKaraGenerator.Rendering.Tests.csproj -m:1 --no-restore --filter FullyQualifiedName~TimedRubyComplexClusterTests --logger 'trx;LogFileName=independent-complex-clusters-dejavu.trx' --results-directory artifacts/ttml-ruby/independent-consumer/test-results
```

TRX：`artifacts/ttml-ruby/independent-consumer/test-results/independent-complex-clusters-dejavu.trx`；SHA-256 `bdae3bbcb9596c317fba9b4db44a1b944a0acc109b6712bf775655ad2be93968`。此 4 项可作为最终 Rendering 集合的子集，不能重复累计。

首次尝试 Noto Sans Arabic 的 `لا` 实际生成两个单字符 draw unit，前置断言使 2/4 失败，避免错误宣称测试了跨片段连字。改用确实形成连字的 DejaVu Sans 后通过；首次 TRX `independent-complex-clusters.trx` 与日志保留，不并通过数。早期 producer 配置曾误将 `dotnet run -m:1` 传给应用参数，以及尝试序列化带无时间 token 的计算属性而抛错；之后改为 build + DLL 和显式字段投影。这些是验收脚本准备错误，不是产品测试通过或生产失败。

## 5. 其它源码检查与最终验收归属

| 阶段/链路 | 本次检查重点 | 边界 |
|---|---|---|
| P0/P1 | ADR/model 一致；Missing/Invalid 状态、全歌词 segment ID、v16→17 迁移、mapper、复制/整行平移；超 base/line 时间保留并诊断 | 无时间与损坏文本问题另有上述真实 Store 复验；未重新运行全部 Core 历史矩阵 |
| P2/P3 | 时间树相对/绝对 profile、seq/par、父裁切、raw project 往返、全部分支导出预算、无损确认、XML 有界写出、损失计数 | 官方 AMLL 消费五组独立实测；HTTP 导出租约、修订、scope 由主验收最终记录 |
| P4 | 七命令完整父链目标、纯 Prepare→最终行/累计预算→回执预算→Commit、批次原子、redo/no-op、sourceBranchId 与全局 ID 重映射 | 阅读实现及回归边界，不把源码存在或同进程测试当作外部 self-contained HTTP 通过 |
| P5 | Missing 多段各自 fallback，Invalid 不采样，Preparation 清零，聚合整形/cluster 归属、普通/package snapshot 白名单与预算、脚本只读段投影 | 本次独立执行复杂 cluster 4 项；既有 real Worker、旧 PNG/geometry 基线与脚本回归由主验收登记，不重复归为本审计新跑 |
| P6 | 最终合同、capability、manifest 与实际 wire 一致性已由当前源码冻结、匹配发布和真实 HTTP 复核 | 本记录不重复累计主流程的版本/远程 SHA 和进程排水数字；主证据见实施§59 |

本批时间轴验收仍不包含自动 ruby spacing solver、完整公共 B3/B4、B5/B6、浏览器/人工 UI、全量性能/长片/平台矩阵。最终入口与实施记录应引用主验收的当前证据，保留本记录的独立来源与子集归属。
