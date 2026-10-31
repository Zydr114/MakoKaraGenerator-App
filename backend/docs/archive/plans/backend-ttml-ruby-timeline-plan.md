# TTML 注音时间轴与导入导出实施计划

日期：2026-10-18～2026-10-19
状态：设计、实施与验收已完成；未覆盖边界仍按本计划明确保持独立。
实施分支：`refact/ttml-first-support`  
产品与 HTTP 验证源码提交：`4fe7809`；生产源码/发布核验基线为 `71d39bd`，最终源码构建输入为 `8a000a3`。随后入口文档以 `e6847b4` 收口，`38f2fa9` 修复 v17 长 ruby ID fallback；本次验收的生产源码基线为 `38f2fa9`，后续为文档提交。

设计决定：[ADR-0233](../decisions/decisions.md#adr-0233)。本文中的“目标”“必须”均为最终交付要求，中间实现的限制不能降低验收门槛。

### 阅读顺序

先看 §1～§3 的范围和数据模型，再看 §4～§8 的改动面；§9～§10 定义实施顺序与验收门槛。§11 单独记录现状和风险，避免把实现进度写成设计限制。文档整理阶段不重复生成产品证据；当前工作区的构建、定向回归、发布和 HTTP 结果以 §11.3 及实施记录 §59 的已登记证据为准。

## 1. 结论与范围

### 1.1 结论

TTML2 能承载 ruby 文本关联与分段时间；AMLL 也已有 timed ruby 的交换约定。因此本次采用 TTML2/AMLL 作为歌词交换格式可行。[TTML2 Ruby](https://www.w3.org/TR/ttml2/#style-attribute-ruby)、[AMLL Ruby 示例](https://amll.dev/en/guides/lyric/ttml#ruby-annotation)

具体采用两个明确的交换 profile，并将有效时间归一化到现有微秒时间类型。标准 TTML 的时间容器与 AMLL 的常见绝对时间写法要分别处理；独立时间数据也不等于任意播放器都会按本项目规则逐字高亮。工程私有身份、关联、时间状态用有版本的 `mkg` 扩展保真，其它播放器的外观与高亮不属于无损承诺。[TTML2 时间语义](https://www.w3.org/TR/ttml2/#timing-semantics)、[AMLL 时间与正文示例](https://amll.dev/en/guides/lyric/ttml#timing-and-modes)

TTML 是交换格式和时间语义基础，不直接取代项目领域模型。项目继续以以下结构保存分支：

```text
LyricsDocument.Branches[]
└── LyricBranch.Lines[]
    └── LyricLine
        ├── Tokens[] → LyricToken
        └── RubyAnnotations[] → RubyAnnotation
            ├── BaseTokenId → 同行 LyricToken.Id
            └── Segments[] → RubyTextSegment
```

`RubyAnnotation` 仍由行持有，通过 `BaseTokenId` 关联基础 token；它并非实际嵌套存储在 token 内。只扩展其注音片段层，分支、行、token 的外层组织继续沿用。

### 1.2 本批目标

1. 为一个注音组提供多个稳定 ID 的注音文本片段，每个片段拥有独立 `Time` 与 `TimingStatus`。
2. 保留注音与基础 `LyricToken` 的绑定关系；基础 token 的时间不被注音时间替代或拆分。
3. 以 TTML2/AMLL 为导入和导出的时间交换格式，保留可表达的 ruby 层级与时间。
4. 保存、重开、撤销、重做、HTTP 读写、快照 IPC、Worker 渲染和脚本投影都能看到同一份注音时间数据。
5. 对 TTML 无法表达的项目数据给出导入/导出报告，不静默丢失。

### 1.3 非目标

- 不改变 `LyricBranch` 的外层结构，不因为 `ttm:agent` 自动创建或拆分歌手分支。
- 不在本批引入独立歌手实体、和声轨实体、翻译/罗马音的新领域层或跨分支同步模型。
- 不把样式、场景树、特效包、媒体资源或脚本源码序列化到 TTML。
- 不让注音时间自动修改基础 token、行时间、分支 `FollowPrimary` 语义或现有基础歌词高亮合同。
- 不用 TTML 的任意 XML 扩展宣称通用播放器能够理解项目私有样式或完整工程。
- 不把“能够解析/写出 TTML”当作完整 Web 编辑器、完整 B3/B4 或用户界面验收。

## 2. 当前实现与问题定位

### 2.1 需求提出时的领域结构与缺口

- `LyricsDocument` 以 `Branches` 保存主歌词和其它来源，`PrimaryBranchId` 选择主来源。
- `LyricLine` 同时保存整行 `Text`、行级 `Time`、`Tokens`、`PrimaryLineId` 和 `RubyAnnotations`。
- `LyricToken` 拥有自己的 `Time` 与 `TimingStatus`，并由 `LyricSampler` 计算逐元素进度。
- 原 `RubyAnnotation` 字段是 `Id`、`BaseTokenId`、`Text`、`Language`、`RelativeScale`、`GapPx`、`Alignment`；缺少注音子片段和独立时间。
- 原验证器检查注音 ID、基础 token 关联和样式参数；本批需要增加片段身份、时间与状态检查。

主要入口：

- [RubyAnnotation.cs](../../../src/MakoKaraGenerator.Core/Lyrics/RubyAnnotation.cs)
- [LyricLine.cs](../../../src/MakoKaraGenerator.Core/Lyrics/LyricLine.cs)
- [LyricToken.cs](../../../src/MakoKaraGenerator.Core/Lyrics/LyricToken.cs)
- [LyricSampler.cs](../../../src/MakoKaraGenerator.Rendering.Abstractions/LyricSampler.cs)
- [ProjectValidator.cs](../../../src/MakoKaraGenerator.Core/Validation/ProjectValidator.cs)

### 2.2 TTML 链路需要解决的问题

通用 `ParsedLyrics` 行/token 中间结构不足以保存完整 ruby 树、父时间容器与项目分支元数据。TTML 需要专用的有界中间层；完成时间解析和 ruby 关联后，再映射到现有领域结构。外部普通 TTML 的多个 `div` 可能表示歌曲段落，不能无条件理解为项目分支；本项目 profile 则必须恢复其明确声明的分支集合。

导出器复用 Plan→Export 和损失确认流程。`ttml.project` 的最终要求是分支、身份、关联、文本和时间状态可回读；`ttml.amll` 提供单来源外部交换。工程样式、媒体和脚本不在可逆范围内，必须逐类报告损失。缺失/非法时间在项目 profile 中也必须保留原数据，不能靠跳过行来通过往返验收。

### 2.3 必须同步修改的下游

以下是原有聚合注音假设及本批目标；当前实现进度单独见 §11：

| 层 | 原有假设 | 计划中的改动 |
|---|---|---|
| 持久化 | `rubyAnnotations[].text` 是唯一注音文本 | 增加 `segments[]`，旧 `text` 作为兼容投影；歌词 schema 增量 |
| Application | `setRubyText` 替换整个注音文本 | 保留旧命令语义；带时间的注音改用显式 segment 命令 |
| HTTP 读模型 | 注音没有时间 | 增加 segments、时间和状态，扩大预算 |
| 采样 | ruby 进度来自基础 token | timed segment 使用自己的时间；无时间的兼容 segment 继续跟随基础 token |
| Skia | 一次整形、一次绘制、一次 clip | 聚合整形保持不跳动，按 segment 文本范围使用独立 clip |
| 快照 IPC | RubyAnnotation 成员白名单没有子片段 | 增加 RubyTextSegment 类型和成员 |
| Worker | 预算只统计 ruby 聚合文本 | 统计 segment 数量和文本，保持硬上限与拒绝语义 |
| 脚本投影 | `SourceRuby` 只有聚合文本 | 保留旧字段，增加只读 segment、时间和状态 |

## 3. 目标领域模型

### 3.1 外层结构保持不变

`LyricsDocument → LyricBranch → LyricLine → LyricToken` 继续是工程内的权威歌词分支结构。TTML 的 `div`、`p`、普通 `span` 只在编解码器层映射到现有分支、行和 token，不在 Core 中保存一棵通用 XML 树。

### 3.2 `RubyTextSegment`

新增不可变值对象，字段如下：

```text
RubyTextSegment
├── Id: string                         // 全歌词文档命名空间内唯一
├── Text: string                       // 片段正文，不为空
├── Time: TimeRange?                   // 独立绝对时间，缺失时为 null
└── TimingStatus: TimingStatus         // Missing/Imported/Estimated/Edited/Confirmed/Invalid
```

语义：

1. `Time` 是绝对微秒时间，不从基础 token 或行时间计算得出。
2. `TimingStatus.Missing` 必须对应空时间；`Invalid` 必须保留非法输入但不能进入可用采样；其余状态必须对应合法区间。
3. 片段顺序就是注音文本顺序；片段不要求首尾相接，空档代表该段没有注音活动。
4. 导入与显式编辑均允许同一注音内片段重叠；采样时每段独立计算，不拆分、合并或互相覆盖。
5. 若输入只有行级时间而没有注音片段时间，片段保持 `Missing`，不把行级时间复制为注音时间。
6. 外部显式时间和标准时间容器能精确求出的有效时间标为 `Imported`；通过程序启发式补出的值只能为 `Estimated` 并计数。首期不自动按相邻片段、基础 token 或行末补 ruby 时间。

### 3.3 `RubyAnnotation` 的兼容形态

保留 `RubyAnnotation` 作为绑定基础 token 的 ruby group，并增加 `Segments`：

```text
RubyAnnotation
├── Id
├── BaseTokenId
├── Text                              // 兼容投影：Segments 文本拼接
├── Segments: RubyTextSegment[]       // 新的时间权威
├── Language
├── RelativeScale
├── GapPx
└── Alignment
```

约束：

- 新建或编辑路径必须通过工厂/归一化函数生成 `Segments`，并保证 `Text == concat(Segments.Text)`。
- 旧文档只有 `Text` 时，映射为一个稳定的无时间 segment；不能把一个 `Text` 猜成按 Unicode 字符拆出的多个 segment。
- `Text` 作为逻辑上的兼容读取投影，供现有渲染、脚本和旧客户端使用；即使为兼容保留初始化入口，新编辑路径也不得把它当成第二份独立可写状态。
- 旧 `setRubyText` 同文为 no-op；改变已有独立时间或多个片段的注音时，明确拒绝 `RubySegmentsRequireExplicitEdit`。更改内容必须使用 segment 命令，禁止隐式重置时间。
- 一个 line 内仍最多一个活动 `RubyAnnotation` 绑定同一个 `BaseTokenId`，保持当前验证器与场景绑定语义。

### 3.4 ID、时间和父子关系

- segment ID 进入 `LyricsEditIdAllocator` 和全歌词唯一 ID 集合；复制行时 segment ID 必须重新分配，不能与原行共享。
- segment 属于 ruby group，ruby group 通过 `BaseTokenId` 绑定基础 token；任何 segment 时间变化都不改变关联 ID。
- segment 时间要求非负、区间有效，使用 `long` 微秒半开区间 `[startUs, endUs)`。允许超出基础 token/行时间并保留诊断，不夹紧、不延长行显示窗口；片段采样与行/场景可见性分别计算。
- 分支的 `Independent`、`FollowPrimary`、`None` 只作用于行/分支显示策略，不把注音 segment 时间自动改成主分支时间。

## 4. TTML/AMLL 映射设计

### 4.1 导入的规范范围

TTML 导入器应从当前“通用行/token解析器”拆出专用的 TTML 中间层，至少识别：

- `tt`、`head`、`metadata`、`body`、`div`、`p`、`span`、`br`；
- `begin`、`end`、`dur`，以及父容器的相对时间；
- `par`、`seq` 时间容器的有效时间解析；
- `ttp:timeBase="media"`、`frameRate`、`frameRateMultiplier`、`subFrameRate`、`tickRate`，以及 frame/tick 到微秒的换算；
- `tts:ruby="container|base|textContainer|text"`；
- `xml:lang`、`ttm:agent`、`ttm:role`、`itunes:key`、`itunes:timing`、`itunes:song-part`；
- AMLL 的 head sidecar translation/transliteration 只能进入显式的“未映射特性”报告，首期不自动变成新的分支。

XML 解析继续禁止 DTD、外部实体和外部解析器，并在分配数组、字符串和诊断前使用现有导入准备预算。未知命名空间元素不能破坏可识别的主歌词，但必须报告其影响范围。

时间解析固定如下规则：

1. 普通 TTML 默认采用标准父容器和 `par/seq` 语义；不能仅因时间文本长得像时钟值就当成媒体绝对时间。
2. AMLL 兼容解析必须识别明确标记，例如 Apple 命名空间的 `itunes:timing="Word|Line"` 或明确的导入 profile；仅声明命名空间不算标记。报告中注明所选 profile，未知/冲突声明明确拒绝。
3. 用有理数或十进制计算，最后一次舍入到整数微秒，半微秒向远离零方向舍入，并报告精度损失。检查 `long` 溢出，不经过浮点秒或 JavaScript Number。
4. 父容器裁切属于标准 TTML 有效时间计算；本项目 profile 另用 raw 微秒扩展保留领域原值。两者不得混为对领域时间的自动夹紧。
5. 无法确定 `seq` 前项终点、未知时间基、`clock/smpte`、drop-frame 等本批未支持语义进入稳定诊断；不能生成“成功但时间已猜测”的结果。为兼容现有歌词导入器，TTML 行本身缺少 `end/dur` 时可沿用媒体终点/显式兜底的 `Estimated` 行终点，并递增 `LineEndFallback` 报告；这项兼容估算只作用于行显示窗口，绝不写入没有独立时间的 ruby segment。
6. 区分 XML 未声明片段时间与按标准规则可算出的显示区间；没有独立片段时间时不把父 token/行时间持久化为 ruby 时间。报告和 IR 保留显式/推导来源。

### 4.2 行和 token 映射

1. 每个可识别的 `p` 映射为一个 `LyricLine`；`p` 的有效时间映射为行时间。
2. 普通带时间 `span` 映射为 `LyricToken`。未带独立时间的文字保持原空白和顺序；不为保持“看起来完整”而凭空生成 token 时间。
3. `itunes:key` 只有在符合稳定 ID 规则且不冲突时才复用；否则由统一 allocator 分配新 ID，并在报告中记录源 ID 到新 ID 的映射统计。
4. `br` 只在当前 Core 文本语义能够保留时写入换行/空格；无法保持原始换行时必须产生 `DiscardedFeature`，不能把它当作普通 token 时间。
5. 混合文本、嵌套普通 span、`xml:space` 和行内空白要按源顺序处理；格式化 XML 的缩进不能无故变成正文，原有有意义空白也不能被 trim。

### 4.3 Ruby 树映射

标准 TTML 的相对时间示例（命名空间声明省略）。行从 10 秒开始，两个注音片段分别位于绝对 10.1～10.4 秒、10.4～10.6 秒：

```xml
<p begin="10s" end="11s">
  <span tts:ruby="container">
    <span tts:ruby="base" begin="0s" end="1s">詮</span>
    <span tts:ruby="textContainer">
      <span tts:ruby="text" begin="0.1s" end="0.4s">せ</span>
      <span tts:ruby="text" begin="0.4s" end="0.6s">ん</span>
    </span>
  </span>
</p>
```

处理规则：

- `base` 的文本必须能与外层基础 token 的文本对应；能够唯一对应时创建一个 `RubyAnnotation`。
- `textContainer` 下每个 `text` 创建一个 `RubyTextSegment`，保留其文本、绝对时间和状态。
- 多个 `text` 的顺序保持源顺序；不存在时间的 `text` 保留为 `Missing`，不能继承基础 token 时间。
- 基础 token 跨越多个 ruby container、base 文本与 token 文本不一致、或一个 ruby 无法唯一绑定基础 token 时，保留普通歌词文本并在报告中给出 `rubyAssociationAmbiguous`，不静默错绑。
- `Language` 优先取 `textContainer`/`text` 的 `xml:lang`，其次取文档语言；没有语言时保持 null。
- 多个注音片段语言不同而当前 group 只有一个 `Language` 时，报告字段无法保真，不伪装成全部同一语言。
- `RelativeScale`、`GapPx`、`Alignment` 不是标准 TTML Ruby 时间语义。导入项目扩展属性时读取，否则使用当前默认值并记录“使用项目默认样式”，不把默认值伪装成 TTML 原值。

### 4.4 分支映射

分支仍由 `LyricsDocument.Branches` 保存。编解码器提供两个明确 profile：

| profile | 目的 | 结构 |
|---|---|---|
| `ttml.project` | 本项目歌词结构可逆交换 | 每个分支输出一个 `div`，恢复分支顺序、主分支、身份、角色、时间模式、行关联、正文、时间状态和 ruby 参数；未承载的样式明确列损失 |
| `ttml.amll` | Apple Music/AMLL 兼容交换 | 输出一个选定来源，使用标准 TTML/AMLL Ruby；其它分支、工程私有字段按导出计划报告损失 |

`ttml.project` 的 `mkg` 命名空间使用 `urn:makokara:ttml`，扩展版本从 v1 开始。必须承载以下字段，并在编码器、解码器和金样中使用同一份字段定义：

| 位置 | 必须恢复的字段 |
|---|---|
| 根节点 | profile、扩展版本、时间 profile、歌词 schema、`PrimaryBranchId` |
| `div` | branch ID/name/role/language/timingMode，以及分支顺序 |
| `p` | line ID、`PrimaryLineId`、权威 `line.Text`、raw 微秒时间与状态 |
| token span/base | token ID、正文、raw 微秒时间与状态，以及元素顺序 |
| ruby container | ruby ID、`BaseTokenId`、Language、RelativeScale、GapPx、Alignment |
| ruby text span | segment ID、正文、raw 微秒时间与状态，以及片段顺序 |

项目 profile 的原始微秒值用十进制整数文本保存；缺失/非法时间也能回读并诊断，不从标准 XML 有效区间覆盖 raw 值。普通 TTML 时间属性仍按标准有效时间输出；不能因存在 `mkg` 属性就改变通用 TTML 时间含义。

没有项目 profile 的外部 TTML 默认导入为一个候选来源，按文档顺序读取所有 `div`；段落标记不自动变成 branch。明确的项目 profile 则恢复原分支集合，未知扩展版本、重复身份或坏关联明确拒绝/诊断，不能降级为合并来源并报成功。

Core 编解码往返保持身份；将候选追加到已有工程时，继续使用既有显式目标与全局 allocator，必要的 ID 重分配须同步更新 base/PrimaryLineId 关联并报告。全文档导入不能经单来源追加路径静默合并；新建导入保留分支集合，单来源追加必须明确选择源分支。

### 4.5 多歌手与 role 的边界

TTML/AMLL 的 `ttm:agent`、`ttm:role="x-bg|x-translation|x-roman"` 能表达多歌手和旁轨，但当前工程没有独立 agent/旁轨领域字段。本批：

- 导入器解析并统计 agent、role、sidecar 的存在和作用范围；
- 不自动把 agent 拆为新的 `LyricBranch`，不覆盖已有分支；
- agent、旁轨和 sidecar 本批只进入导入损失报告，不承诺跨保存/导出的元数据保真；
- `ttml.amll` 对无法映射的 agent/旁轨出具明确损失；
- 多歌手编辑、歌手级采样和歌手级 HTTP 目标不属于本批验收。

## 5. TTML 导出设计

### 5.1 导出结构

`ttml.project` 生成可读、稳定、可回读的 XML：

- 根 `tt` 声明 TTML、TTML metadata、TTML styling、Apple/AMLL 和 `mkg` 命名空间；
- `head` 写语言、格式版本和可选项目元数据；
- `body/div` 按分支顺序输出；
- `p` 保存行时间和稳定行 ID；
- 普通 token 输出为 `span`，用标准时间属性和项目扩展分别保存有效区间与原始状态；
- 带注音的 token 使用 Ruby container/base/textContainer/text；
- 每个 timed `RubyTextSegment` 输出独立 `tts:ruby="text"` span 的 `begin`/`end`；
- 缺失时间的 segment 不写伪造的 begin/end；
- `mkg` 保存 §4.4 的完整字段，包括与 token 拼接不同的权威 `line.Text`；缺失/非法行时间不导致整行被跳过；
- 输出顺序确定、数值与区域设置无关；只在结构节点间缩进，避免改动行内空白；XML 特殊字符由 writer 转义。

`ttml.amll` 只输出一个选定分支，使用 AMLL 常见的 `itunes:timing="Word"`、`itunes:key` 和 Ruby 层级。输出明确标记所用 AMLL 时间约定，不注入项目样式/场景 XML。它必须经 AMLL 消费端解析验证；本项目解码器自读自写不能单独证明外部兼容。

官方消费端验收还要核对实际恢复的基础元素时间、注音文本及每段时间。XML 保留微秒原值，不主动量化或补造缺失时间；对消费端的毫秒舍入、无时间注音片段丢弃、以注音时间范围替代基础元素时间等已复现限制，导出计划必须分别计数、定位并要求损失确认。写入 XML 的片段数不能称为消费端成功恢复的片段数；`ttml.project` 继续保持原始字段可逆。

### 5.2 导出前损失评估

沿用 `ILyricsExporter.Plan` → `Export` 两阶段门禁，新增 TTML 专用损失项：

- 选定 profile 无法表示的其它分支或 `PrimaryLineId` 关联；
- `ttm:agent`、旁轨、歌曲段落或 sidecar 元数据无法映射的数量；
- 外部 profile 因非法/缺失行时间而跳过的行；项目 profile 保留原数据；
- 非法/缺失基础 token 时间和 ruby segment 时间；
- 无法唯一绑定基础 token 的 ruby；
- 外部 profile 无法保持的身份、时间状态、独立行正文和 ruby 字号比例/间距/对齐；两个 profile 未承载的分支/行/元素样式覆盖；
- AMLL 官方消费端对微秒精度、无时间注音片段及基础元素时间的实际降级；保留 XML 原值并报告消费端限制，不改变领域时间；
- 不支持或不安全的 TTML 时间容器/扩展；
- XML/UTF-8/4 MiB 输出预算和报告 64 KiB 预算。

损失报告必须至少包含稳定的 `Feature`、受影响数量、可读说明和行/ruby/segment 位置摘要；超出报告预算时整体拒绝而不是截断。用户未接受计划损失时不发布内容、下载句柄或不完整产物。报告数量与实际跳过/舍入/丢弃必须一致；无法确认是否有损时不能声称无损。项目 profile 没有实现的目标字段要补实现，不能长期以“已报告损失”替代最终门槛。

### 5.3 导出 API 适配

现有 `LyricsExportOptions`、`LyricsExportPlan`、`LyricsExportReport` 和 Hosting 的计划/执行/内容租约流程可复用，但需要：

- 注册 `ttml.project` 和 `ttml.amll` exporter；
- 增加显式 `scope`：省略/`branch` 保持旧请求语义，必须给 `branchId`；`document` 只允许 `ttml.project`，禁止携带 `branchId`。非法字段组合、显式 null 和未知 scope 有严格正反例；
- Plan 与 Export 响应都返回实际 scope；document 的 branchId 明确为空，包含全部分支，不再计算“未选分支”损失。单分支输出仍报告省略分支/跨分支关联的影响；
- 计划固定精确 revision、范围、格式、选项和全部损失，执行不得换源或扩大范围；
- `WithinLyricsSourceBudget` 必须遍历 `Branches`/`AllLines`，同时统计分支元数据、group/segment 数量及文本，不能只扫描主分支 `Lines`；
- 为 TTML report 增加 ruby segment 写出、跳过和损失计数；
- 同步 Server 路由、capability、OpenAPI、合同示例、manifest 指纹和真实 HTTP 失败例。

继续使用已有认证与所有权、300 秒 TTL、每 client/全局配额、4 MiB 内容和 64 KiB 报告上限；下载扩展名 `.ttml`，媒体类型 `application/ttml+xml`。超预算、过期、跨 client、旧 revision 或未接受损失均不得发布内容。

## 6. 持久化与旧数据迁移

### 6.1 文件形状

歌词文件 schema v17 增加 `rubyAnnotations[].segments[]`，目标形状：

```json
{
  "id": "ruby-1",
  "baseTokenId": "token-1",
  "text": "せん",
  "language": "ja-JP",
  "relativeScale": 0.5,
  "gapPx": 2,
  "alignment": "Center",
  "segments": [
    { "id": "ruby-segment-1", "text": "せ", "startUs": 27820000, "endUs": 27880000, "timingState": "Imported" },
    { "id": "ruby-segment-2", "text": "ん", "startUs": 27880000, "endUs": 27950000, "timingState": "Imported" }
  ]
}
```

`text` 在 v17 保留为可读兼容投影；读取时验证它等于 segment 文本拼接。不一致则保留两份原数据并报告错误，不静默覆盖任一侧；有效编辑必须恢复一致性。后续删除兼容字段另起 schema 版本。以上是磁盘形状；HTTP 的 Int64 时间继续使用十进制字符串。

### 6.2 v16 → v17

1. `LyricsDocument.CurrentSchemaVersion` 从 v16 增至 v17；工程 schema 与歌词 schema 分开，不因本次歌词改动重写工程结构。
2. 增加 `RubyTextSegmentDto`、`RubyAnnotationDto.Segments` 和 mapper 读写。
3. v16 旧注音只有 `text` 时，创建一个稳定、无时间、`Missing` 的 segment；ID 必须由 `rubyId` 派生并经过全局冲突检查，不能依赖运行时随机值。
4. v16 的 `Text` 保持原样；不按字符、mora 或 grapheme 自动分段。
5. `ProjectStore.LoadLyrics` 只对真实 v16 执行 v16→v17 迁移，再反序列化；v1～v15 及未来版本继续拒绝。禁止只改 schema 数字来伪装支持断代格式；工程 `applicationVersion` 既有门禁保持。
6. 迁移日志要区分“字段缺失按旧语义解释”和“实际创建了兼容 segment”。保存时才写回 v17 形状；失败不改变工程、dirty、历史和源文件。
7. 更新 schema 诊断、JSON 键白名单、项目备份/自动保存和保存重开测试。

### 6.3 身份和复制

- `LyricLineDuplicator` 复制注音时重新分配 ruby segment ID，保留 segment 文本/时间/状态，但根据现有复制行时间平移策略同步平移有效 segment 时间。
- `LyricsEditIdAllocator` 扫描并预留 segment ID；导入、追加、重切和复制使用同一个 allocator。
- 旧 `Text` 投影不能作为 ID 分配输入；同文不同 segment 必须有独立稳定身份。
- 行平移、整行复制、基础 token 删除/重切/合并、分支删除和导入替换都要审查调用链：关联删除同时移除子片段，关联可恢复则保留身份；溢出或歧义在提交前处理，不遗留悬空 ruby。

## 7. 编辑、撤销重做和 HTTP 合同

### 7.1 Application/Core 编辑能力

先增加无副作用 Prepare→Commit 用例，再接现有后端 HTTP 命令入口。交付七个命令：

| 命令 | 行为 |
|---|---|
| `lyrics.setRubySegmentTime` | 为一个片段设置有效起止时间，状态为 Edited |
| `lyrics.clearRubySegmentTime` | 清除片段时间，状态为 Missing |
| `lyrics.setRubySegmentText` | 更改片段文字，保留其 ID、时间和状态，同步聚合 Text |
| `lyrics.addRubySegment` | 显式位置插入新片段，返回新 ID，初始为无时间 Missing |
| `lyrics.removeRubySegment` | 删除片段；最后一个片段被删除时移除 ruby group，返回明确空选择 |
| `lyrics.reorderRubySegments` | 接收现有 ID 的完整排列，只改文本顺序，不改各片段时间 |
| `lyrics.setRubySegmentTimes` | 1～1000 项时间原子批量设置；重复目标/任一非法项整批拒绝 |

单片段目标为 `branchId/lineId/tokenId/rubyId/segmentId`；组级结构目标不带 segmentId。逐级校验真实隶属关系，不能只凭全局 ID 命中就跨组编辑。基础 token、行、分支编辑复用原用例和 ruby 绑定规则。

每次实际变化最多一次历史；no-op 保留 redo；undo/redo 完整恢复 RubyAnnotation（含 segment ID、正文、时间、状态）和实际选择反馈。旧目标、旧修订、重复 ID、非法区间和超预算均在提交前拒绝。顺序固定为：解析/准入→纯 Prepare→完整最终行与累计预算→完整回执预算→一次 Commit。

### 7.2 HTTP 读模型

在现有 `ProjectRubyAnnotationDto` 上增加 `segments`，每个 segment 至少有 `id/text/time/timingStatus`。`text` 暂时继续返回拼接投影，以兼容旧客户端；新客户端应优先读取 `segments`。

预算同时计入 ruby group 和 segment：

- 20000 项上限包含 branch、line、token、ruby group、ruby segment；
- 1 MiB 完整 JSON 窗口、64 KiB 命令回执和现有单行不截断策略保持；
- 新命令意图上限 64 KiB，受影响的完整最终行累计上限 16 MiB；先计量后提交，不能只按补丁大小估算；
- 超限 413，不能先提交再回滚，也不能返回缺少 segment 的“摘要成功”。

新增 capability 和命令类型严格分型；不把 `rubyId`、`segmentId` 塞进旧 token target。change set 增加 `lyricsRubyTiming`/`lyricsRubyContent`，`rubyResult` 返回实际目标、选择和数量。segment ID 不塞进旧 TokenIds；超过 16 KiB 的 change set 转完整刷新，完整回执仍须满足预算。

时间 wire 使用十进制 Int64 字符串；覆盖超过 `2^53` 的精确往返。未知字段、缺失/null、数值型时间、坏 target 均有严格反例。精确修订冲突为 409、预算为 413、领域拒绝为 422，并固定 `failureCode`/`field`；原认证、所有权和会话边界继续适用。

合同收口必须同步：

- `Backend.Contracts` DTO、JSON converter、错误 `failureCode`/`field`；
- `docs/architecture/backend-api.md`、`web/api-contract.md`、OpenAPI、contract-manifest、正反例；
- Hosting、Server、自包含发布和真实 HTTP 保存重开/并发/拒绝回归。

## 8. 时间轴、采样、布局和渲染影响

### 8.1 采样模型

在 `Rendering.Abstractions` 增加注音段采样结果，例如 `RubyTextSegmentSample`，包含 segment index/ID、文本范围、进度、`ProgressSource`、`IsCurrent`、`IsSung`。`LyricFrameSample` 增加按 ruby/segment 索引的结果，但不改变现有 `Tokens` 语义。

采样规则：

- 有有效 segment 时间时，进度只由该 segment 的绝对区间计算；不读取基础 token 的进度。
- 缺少独立时间的片段使用明确的基础 token fallback；旧单片段 annotation 保持旧视觉。Invalid 时间只诊断，不作为有效时间采样。
- 行级/分支级时间不能填充 segment 缺失时间。
- 同一 annotation 的 segment 可以有空档或重叠；每段独立返回状态，渲染层不合并时间。
- 行/分支 Static、FollowPrimary、None 和场景可见策略保持；Preparation 待唱画面的 ruby 高亮与原 token 高亮一起清零。
- 原 `TokenStarted`/`TokenCompleted` 和脚本 token 事件保持。本批交付只读 segment 数据；新增脚本事件或订阅另起合同，不作为本批时间轴交付前提。

### 8.2 Skia 与几何

首期保持“基础 token 家族一次整形”的布局稳定性：`RubyAnnotation.Text` 的拼接结果继续作为一个整形单元，`RubyLayout` 同时记录每个 segment 在拼接文本中的 UTF-16 范围和几何偏移。绘制时按 segment 范围和自身进度裁切 sung/unsung，而不是让每个 segment 重新排版造成跳动。

片段边界跨组合字符、代理对或连字时，按整形 cluster 的起点确定所属片段，不能拆画一个 cluster 或重复着色。测试包含 `e`+组合重音、补充平面字符、连字/复杂文字以及正放/回退。

如果 segment 的注音宽度超出基础字，必须在共享布局入口决定 overhang、最小 gap、画布溢出和命中测试规则；不得全局修改 `LetterSpacingPx` 来“碰巧”容纳注音。该 spacing solver 是独立子阶段，不能用 Ruby 时间轴已接通来宣称自动排版完成。

需要更新：

- `SkiaRenderPipeline.Ruby` 的测量、绘制、capture、几何和 source binding；
- `SceneDefinition`/`PreparedRenderFrame` 的 ruby 聚合几何（首期仍以 ruby group 为场景节点）；
- `SceneAffineProjection`、命中测试、PNG/帧测量和透明导出的一致性；
- ruby segment 相关缓存键，确保时间或文本变化失效正确。

### 8.3 Worker、快照和脚本

- `SnapshotJsonContract` 增加 `RubyTextSegment` 的严格成员白名单、时间类型和 contract 版本检查；普通/包 profile 都要覆盖。
- `StaticProjectSnapshotCodec`、`PackageProjectSnapshotCodec`、Worker IPC 预算和诊断统计都按 segment 数量/文本计数。
- `SceneMeasurementWorker` 的文本预算改为统计 `Segments` 拼接文本；旧 ruby fixture 的预算和输出必须保持。
- `ContentProjection`/`SourceRuby` 保留聚合 `Text`，新增 segment ID、时间和状态只读投影；脚本不能通过投影修改 Core。
- 脚本 timeline projection 保持现有 token/ruby group 绑定；只读新增字段与现有事件分别验证，不重解释既有事件。
- `scripts/` 目录的脚本配置、效果包和脚本源码不嵌入 TTML；只消费快照/投影中的已验证时间数据。

## 9. 分阶段实施顺序

| 阶段 | 交付内容 | 完成门槛 |
|---|---|---|
| P0 设计冻结 | ADR、模型不变量、父子时间边界、兼容 `Text` 语义、TTML profile 和损失码 | 设计评审能回答旧命令、重叠 segment、超行时间、agent、导出 profile 的处理；没有未决的隐式回退 |
| P1 Core 与 schema | `RubyTextSegment`、验证器、ID allocator、DTO/mapper、v16→v17 迁移、复制/保存重开 | 旧 v16 fixture 画面/文本等价；新 v17 segment 往返字节语义等价；非法时间、重复 ID、坏 base 关联全拒绝或诊断 |
| P2 TTML 导入 | 专用 TTML IR、完整 div/p/span、时间容器、AMLL ruby、branch 映射、导入报告 | 代表性 AMLL ruby（单段/多段/无时间/嵌套）、行级与逐字混合、多个 div、恶意 XML、预算边界全部有正反例；不再只读首 div |
| P3 TTML 导出 | `ttml.project`、`ttml.amll`、Plan/Export 损失报告和有界 XML 输出 | project 多分支/身份/关联/状态/ruby 参数完整 round-trip；AMLL 消费端可读；全文档 HTTP 范围与损失未确认不发布均有真实链路证据 |
| P4 编辑与 HTTP | segment 时间/文本/结构 Prepare→Commit、撤销重做、读模型、合同和 capability | 编辑→读取→undo/redo→保存→关闭→重开；精确 revision、no-op redo、过期目标、413/422/409 全部通过真实 HTTP |
| P5 采样与渲染 | segment sampler、Skia clip、快照/Worker、脚本只读投影 | 固定时间点逐段 progress 与预期一致；旧无时间 ruby 输出零差；timed ruby PNG/几何/Worker 与同一快照一致 |
| P6 收口 | OpenAPI/manifest/文档、完整定向回归、证据与远程提交 | solution 零警告/错误；Core/Application/Hosting/Server/Rendering/Worker/脚本/真实 HTTP 证据分层登记，不把代码存在当作产品完成 |

依赖顺序为 P0→P1；P1 后分别推进 P2/P3、P4、P5；P2/P3 编解码与 P4 HTTP 适配汇合后做端到端验收，再进入 P6。允许按切片提交，但只有 P0～P6 全部满足才能称本批完成。

每个可审查步骤单独提交并同步远程工作分支。源码、对应回归与必要行为文档同批；最终冻结工具必须从当前源码重新生成 CLR 元数据、OpenAPI、examples、index 与 manifest。产品按新 MINOR 发布，合同单独升级；版本以发布时冻结结果为准，设计阶段不写成已发布版本。

## 10. 分阶段验收清单

### 10.1 模型和持久化

- 一个基础 token 对应一个 ruby group，ruby group 可以有两个以上 segment。
- `せ`/`ん` 的 segment 时间可以分别保存、读取、采样、绘制和导出。
- 旧单一 `Text` 注音读入后只生成一个无时间 segment，不产生虚构时间。
- segment ID 在全分支复制、导入、重做和保存重开后保持唯一且稳定。
- v16 文件不被就地改写；保存后才产生 v17；v18 或未来更高版本仍明确拒绝。
- `text` 不一致、时间/状态不一致、重复 ID、坏关联都保留可读诊断；v1～v15 不因这次迁移被放行。

### 10.2 TTML 导入导出

- 覆盖 `begin/end/dur`、父时间、`par/seq`、frameRate/multiplier/subFrame/tick、微秒舍入与溢出；标准 TTML/AMLL 用分别断言绝对结果的金样，不能只做自读自写。
- 支持 TTML Ruby 的四层结构和每个 `tts:ruby="text"` 的独立时间。
- `ttml.project` 的多分支、PrimaryLineId、branch role/timingMode、各级 ID、独立 line.Text、缺失/非法/raw 时间、状态与 ruby 参数都能 round-trip；未承载的项目样式逐类计入损失。
- AMLL profile 经消费端解析，ruby 树不被展平；身份/状态、样式和省略分支损失与实际一致。
- 覆盖行内空白、XML 转义、嵌套混合文本、多个 div、未知扩展、agent/旁轨报告、DTD/外部实体、深度与输出/报告预算。
- 只读首 div、丢失嵌套 ruby、把 agent 当作普通正文、无损失报告等旧行为不再作为成功标准。

### 10.3 编辑和后端

- segment 时间编辑一次请求至多一次历史；no-op 不清 redo。
- 任何失败在历史、dirty、revision、选择、资源租约和文件内容上均无副作用。
- HTTP 窗口同时返回兼容 `text` 和新的 `segments`；预算超限不截断。
- OpenAPI、合同示例、能力声明、错误码和实际 self-contained HTTP 行为一致。
- 七命令逐项覆盖、批次原子拒绝、重排/删除最后片段、同文 no-op 保 redo、旧 setRubyText 保护、错误隶属关系和跨 client/过期资源。
- 匹配的自包含 Server+Worker 通过真实 HTTP 完成 TTML 导入→片段编辑→读取→撤销/重做→保存关闭重开→计划→下载→解析；document scope 覆盖多个分支，branch scope 保持原行为。

### 10.4 渲染和脚本

- timed ruby 在 segment 时间中逐段显示 sung/unsung；基础 token progress 保持原定义。
- 无 timed segment 的旧工程画面与基线 PNG/geometry 对比零差。
- 快照普通 profile、package profile、Worker、透明/普通帧都能传输 segment 时间。
- 脚本聚合字段与既有事件保持；新增 segment 只读 ID、正文、时间和状态来自同一快照。

### 10.5 最终证据

- 正常 solution 构建零警告/错误，运行 Core、Application、Hosting、Server、相关 Rendering、Worker 和脚本定向回归；原暂停的全量、性能、GUI、长片/平台矩阵不自动恢复。
- 单测、进程内 Kestrel、外部自包含 HTTP、真实 Worker、普通/透明 PNG 与几何分别登记，不合并重复子集数量。使用替代执行器的 fixture 不能作为真实发布证据。
- 每组记录源码 SHA、输入/发布指纹、命令、最终退出码、TRX/报告、失败与重跑范围；编译失败且零测试执行记为构建阻断。
- 验收后确认 Server、Worker 及实际观察子进程退出，更新实施记录/roadmap/入口，再核对远程分支 SHA。文档检查不能代替产品验收。

## 11. 已定方案、风险与当前状态

### 11.1 已冻结实施决定

实施依据见 [ADR-0233](../decisions/decisions.md#adr-0233)。v17 保留 `text` 兼容投影；旧整段改字对 timed/multi-segment 注音明确拒绝；显式编辑允许片段重叠和超行时间，但不延长行显示窗；仅支持 v16→v17 迁移；agent 只进入报告。项目扩展字段清单见 §4.4，全文档 HTTP 导出属于 §5.3 的最终目标；不再保留“二选一待确认”的隐含行为。

### 11.2 主要风险与处理

| 风险/决定 | 影响 | 处理 |
|---|---|---|
| `RubyAnnotation.Text` 与 `Segments` 重复表达 | 可能出现不一致 | 新编辑保证一致；损坏输入保留原值并诊断，不静默修复 |
| v17 输入缺少 `segments` 且 ruby ID 极长 | 兼容读取路径原先直接拼接 segment ID，可能越过正常 ID 长度约束 | `38f2fa9` 已通过 `CompatibilityId` 限制 fallback 长度，增加 256 字符 ruby ID 持久化回归，Core 定向 28/28 通过；缺 `segments` 沿现有兼容读取生成 `Missing` 片段，正常序列化始终写出 `segments`，其他损坏字段按现有诊断处理 |
| TTML 相对时间、`seq/par` 和 frame/tick 组合复杂 | 导入结果可能被错误平移 | 先建立独立时间解析器和金样；无法安全解析时保留原位置/文本并报告，不猜测 |
| ruby 时间超出行或 token | 数据时间与可见时间不同 | 领域保留绝对值；显示遵守原行窗口；标准 TTML 有效区间与 raw 扩展分别验证 |
| 同一注音 segment 重叠 | 编辑和视觉高亮可能复杂 | 导入与显式编辑都允许；各段独立采样，cluster 归属规则防重复绘制 |
| AMLL 旁轨、多歌手无法落入当前 branch | 外部内容可能损失 | 不自动创建分支；报告 agent/role/sidecar；后续若需要单独设计 agent 模型 |
| 项目样式不属于 TTML 核心 | 外部往返无法保持外观 | `ttml.project` 用有限 `mkg` 扩展；`ttml.amll` 明确列损失，不伪造标准兼容 |
| 旧 `setRubyText` 与 timed segments 冲突 | 旧客户端可能覆盖时间 | 同文 no-op；改字稳定拒绝并指向显式 segment 命令 |
| 共享布局仍以聚合 ruby 宽度为准 | 长注音可能溢出或错位 | 时间轴阶段先保持几何稳定；spacing/overhang 单列实现和验收 |
| 快照/Worker/脚本合同漏字段 | 真实渲染与编辑数据不一致 | P5 必须做普通/包 profile、真实 Worker、脚本投影和 PNG/geometry 同源核验 |

### 11.3 本次核对到的实施状态

P0～P6 已按本计划完成，并以最终源码、匹配发布和真实 HTTP 证据分层登记。阶段状态如下：

| 阶段 | 最终状态 | 证据 |
|---|---|---|
| P0 | 已关闭：ADR-0233、模型不变量、时间容器/profile、损失码和非目标已冻结 | [ADR-0233](../decisions/decisions.md#adr-0233)、本计划 §1～§5 |
| P1 | 已关闭：`RubyTextSegment`、schema v17、v16→v17 迁移、全局 ID、保存/重开/复制语义已接通 | Core 1314/1314、Hosting 1109/1109，最终 TRX 见 `artifacts/ttml-ruby/validation/final/` |
| P2 | 已关闭：标准 TTML 时间树、`par/seq`、frame/tick、AMLL ruby、分支导入和诊断已接通 | Core/Application/Hosting 回归；pretty/compact seq、恶意 XML、预算和多来源证据 |
| P3 | 已关闭：`ttml.project` 与 `ttml.amll` 导出、损失门禁、MIME 和官方 AMLL 消费端验证已接通 | 官方 AMLL 五组样本与独立审计见 [independent audit](../research/backend-ttml-ruby-independent-audit.md) |
| P4 | 已关闭：七个 segment 命令、原子 Prepare→Commit、undo/redo、严格 wire、读模型和导入追加已接通 | Application 695/695、Server 387/387、真实 HTTP 3/3 |
| P5 | 已关闭：segment sampler、聚合整形/cluster 归属、普通/package snapshot、真实 Worker、PNG/BGRA/geometry 和脚本只读投影已接通 | Rendering 100/100、Skia 9/9、Worker 80/80、Runtime Worker 6/6、P5 分层 254/254 |
| P6 | 已关闭：合同 1.27.0、OpenAPI/manifest、匹配 self-contained 发布、真实 HTTP 和入口文档已收口 | 合同 80/89/449/91/2648/2040；发布 834 输入、806 文件；产品/HTTP 源码提交 `4fe7809`，入口文档提交 `e6847b4`，v17 长 ID fallback 修复与本次验收生产源码基线为 `38f2fa9`；后续为文档提交 |

最终定向回归合计 **3929/3929**，solution 构建 0 warning/0 error。三组外部 self-contained HTTP 共 **183 请求、180 组 JSON exchange、9 个 job 全部 succeeded**；3 个 Server 和 3 个 Worker 的 6 个观察身份全部退出。冻结合同工具对三组 `http-exchanges` 逐组复核通过，报告为 `artifacts/ttml-ruby/validation/final/contract-http-validation.json`。过程中的夹具路径错误、旧分支参数错误和修复重跑均保留在 `published-http-*.log/.trx`，不并入最终通过数字。00:24 后生成的 `39dfe4…`、`7a28d1…`、`ba265c…` 三组目录是同一发布输入的后续重跑：它们可独立通过合同校验，但没有对应最终 summary/TRX，故明确标为非权威重跑，不改变本节关闭数字或权威目录列表。

本批仍不包含自动 ruby spacing solver、完整公共 B3/B4、B5/B6、浏览器/用户验收、性能/长片/GUI/平台矩阵；这些边界不因 P0～P6 关闭而扩大。

## 12. 参考规范与当前代码

- [W3C TTML2](https://www.w3.org/TR/ttml2/)：TTML2 的 timed text、content vocabulary、时间和 Ruby 相关规范。
- [AMLL TTML Format Overview](https://amll.dev/en/guides/lyric/ttml)：AMLL 的 `itunes:timing`、`ttm:agent`、Ruby 四层结构和 timed ruby 示例。
- [当前统一 API](../../architecture/backend-api.md)：现有歌词窗口、时间命令、导出计划/执行和预算合同。
- [当前 Web 编辑计划](backend-web-editing-plan.md)：现有 branch、line、token、ruby 读写及精确 revision 约束。

本计划定义本批目标、实施顺序和完成门槛。实施时各阶段单独提交代码/合同/验收证据，并在当前入口文档中标注实际完成状态；“TTML 可表达”、代码存在、真实链路通过分别记录。
