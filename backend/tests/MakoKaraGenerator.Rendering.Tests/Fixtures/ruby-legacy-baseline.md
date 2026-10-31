# 无时间注音绘制基线

`ruby-legacy-baseline.json` 是 P5 的固定比较输入，不在测试运行时生成。

- 旧绘制来源：`39e4586^:src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.Ruby.cs`。
- Git blob：`490719d70c6542ff8b0b7e3b32f1724fe70d7851`。
- 旧文件 SHA-256：`576a84e334766cd686c86747bd04fabe18567cb693f315a37f85a02f27806ce3`。
- 固定字体：Noto Sans CJK SC；实际字体数据 SHA-256
  `b76b0433203017ca80401b2ee0dd69350349871c4b19d504c34dbdd80541690a`。
- 金样 SHA-256：`501e003b09e374d9b5dbe36791b69c65c90a5add3667dced72bd500c286a7c83`。

生成时在 `artifacts/ttml-p5/legacy-probe/renderer` 独立建立同名渲染项目：引用当前
Core、Text、Rendering.Abstractions，编译当前 Skia 的其余源文件，只将 Ruby 文件替换为上述
Git blob。输出完全位于 artifacts，没有覆盖源码或正常 bin/obj。随后通过
`RubyLegacyBaselineProbe.Capture()` 收集 3 种完整注音（假名、组合音标与拉丁连字、ZWJ
emoji）、6 个基础 token 时刻、普通与透明模式，共 36 帧。

每项固定 BGRA SHA-256、PNG SHA-256、基础 Advance/墨迹/矩阵、族和注音世界角点以及绘制单元
身份。当前生产渲染器通过同一探针与固定输入逐项比较，证实本次注音分段改造对这些旧无时间
注音画面的像素、PNG 和几何零差。该证据仅比较旧 Ruby 绘制算法与当前算法；不是对整个旧版本
工程的重新构建或跨系统字体一致性保证。

2026-10-18：隔离旧算法构建零警告/错误；当前比较及 timed ruby 测试 10/10 通过，TRX 位于
`artifacts/ttml-p5/timed-ruby-legacy-baseline.trx`。
