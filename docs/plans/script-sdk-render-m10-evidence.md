# M10 媒体轨实施与验收记录

2026-11-05，`dev/script-sdk-render-upgrade`。**实施中，M10未关闭，media.features能力门禁仍关闭。**
本轮功能优先，性能门槛不参与验收；身份、预算、数值、像素、取消、生命周期及真实作者链仍须验证。
证据根为`.local/script-sdk-m10-20261105/`，各失败轮保留。

## 第一片：固定来源、精确时间与容量（产品0.112.16）

Core新增媒体来源／颜色／工具／profile／制品只读值与边界验证；Rendering.Abstractions新增
只读presentation frame、interval、index、callback借用合同。构造不授予信任：接受边界重算
嵌套SHA和工具／颜色绑定，不因外层重新hash放行矛盾来源；未接HTTP、Worker任务或SDK。

- 复合身份使用domain分离、little-endian固定顺序、hash原始32B和严格UTF8。工具清单以
  UTF8 byte顺序编码，每份最多1024项、单logical ID≤256B、总ID≤64KiB，分配有界snapshot
  和排序scratch前分别核数量／文本；不把路径、mtime或任意JSON作为身份。
- 原始PTS保留signed64 ticks和正约分int32 timebase，用BigInteger精确比较与数学ceil转微秒，
  每个运算中间量≤192bit，末结果checked long；不会将相邻巨大ticks经double合并。
- 固定SDR组合含RGB8/16静态图、RGB8视频、BT709 planar YUV8/10及受控JFIF BT601/sRGB
  图片解释；非方SAR正约分保留，仅quarter-turn交换canonical尺寸。颜色authority实际来源、
  解码工具加载闭包和真实pixel／PTS关联仍由后续inspector负责，当前结构校验不证明它们。
- MFT1布局单一真源：256B头、64B interval、576格×16B／帧、64帧块及32272B prefix。
  54000帧制品528390096B，spool＋candidate1056779936B；分配／落盘前按全部section准入。
  视频durationUs必须不少于frameCount；完整interval、prefix和实际bytes留给codec验证。
- decode账为所有native强制限额＋受管活跃buffer reservation≤256MiB。16M像素RGBA32F
  本身已经占满该值，加1B即拒绝；尺寸上限不代表所有更小输入均可运行。无可证明native
  封套返回CapacityUnsupported，本片只实现算术准入，不声称已强制整个Worker物理RSS。

`build-foundation-03.log`完整solution0警告／0错误；`foundation-focused-01.log`为35/35。
相邻Core全套`foundation-core-full-01.log`为1506/1506，含媒体能力仍拒绝的既有门禁。
包括9个独立规范身份向量、UTF8补充平面顺序、错绑定／非法默认值、工具文本容量、signed ceil、
最大时间乘积、颜色组合、SAR、制品精确布局与内存准入。前两轮编译失败分别是清单方法尚未
纳入身份服务接口触发CA1822，以及测试expected顺序／构造表达式丢弃的analyzer诊断；修正
实际接口／测试写法后构建通过，没有禁止或抑制分析器。这些编译失败不记作红绿业务测试。

后续顺序：七量面积kernel与MFT1流式codec；受控原生封套／同链PTS解码与SDR颜色／SAR；
任务store与租约；SDK／typed GPU查询；固定输入换代与作者链；导出等待／钉住轨；T01样例。
静态合同、匹配发布、fresh HTTP、浏览器和性能分别登记，当前没有据此宣称媒体能力已可用。
