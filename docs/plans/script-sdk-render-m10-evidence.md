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

## 第二片：七原量面积内核（产品0.112.17）

`MediaMomentKernel`从真实canonical RGBA32F像素逐cell积分到32×18七量，整数有理重叠
权重明确覆盖全部源像素；独立计算Yp²／aYp／a²，不以均值平方冒充二阶量。uint16量化
使用固定half-up，非法／非预乘／隐藏透明RGB／非有限／负零先拒绝；不做隐式clamp。
单帧贡献上界`(W+31)×(H+17)`，数值scratch固定224B，原像素及destination仍由owner另计。

SourceNormalized分数矩形按七量网格的分段常量面积积分。对每个量同时补偿累计direct
与complement，再用较近端点的等价表达式求均值，使全0／全1在double面积舍入下精确保留，
没有通过容差截断掩盖错误。grid外坏值仍拒绝；取消原样传播、partial destination不可发布。
这是相对于固定网格近似的积分，不宣称恢复任意子矩形的原始像素细节。

`build-moment-kernel-02.log`完整solution0警告／0错误，`moment-kernel-focused-01.log`为
23/23；相邻Media全套`moment-kernel-media-full-01.log`为232/232。独立BigInteger有理oracle覆盖1×1、2×1、17×13、37×23的全部量化分量；另有
相同均值的棋盘／纯灰二阶差异、透明混合、分数／极小矩形、全1端点反例、预算与取消回归。
首轮构建的可取消Solid overload未列入服务接口，触发CA1822；补完整可取消接口而非关闭
分析器后通过。尚无decoder、轨codec、任务、SDK、GPU或作者运行证据。

## 第三片：SDR 转换与正交／SAR 几何（产品0.112.18）

`MediaRasterGeometry`保持encoded像素网格，先镜像再quarter-turn精确重排；SAR正约分只改变
显示度量，720×576的16:15显示为768×576、旋转90°后576×768，无整数宽度舍入或隐式
重采样。`BackgroundPlacement`新增double显示入口，现有int入口完整方法保持原计算顺序
与最终float，未知fit的旧行为不变；新入口严格拒绝未知fit。contain／cover／original及
canonical source↔canvas映射共用新入口。

`MediaSdrFrameConverter`从明确packed/planar原生分量转换为linear-premul RGBA32F，
验证完整固定来源、位深、正stride／padding、10bit高位、输出长度与输入输出不重叠。
RGB8/16、BT709 full/limited YUV8/10 420/422/444和明确JPEG601/full/sRGB按固定样本中心、
bilinear chroma及EOTF执行；唯一规范saturate在YUV矩阵后，不掩盖非法标签或alpha。
颜色用double运算、每通道一次float写入，alpha0输出正零；缓冲所有权及native强制封套
仍由调用者控制，不把本片容量算术当真实解码器已受限。

`build-presentation-color-01.log`完整solution0警告／0错误。几何及既有placement定向
`presentation-geometry-focused-01.log`为20/20；`presentation-color-focused-01.log`为27/27。
独立8方向像素表、精确SAR／三fit、legacy矩形，以及独立Kr/Kb矩阵、样本中心tent权重和
Exp/Log EOTF oracle均通过；还覆盖奇数尺寸／padding、透明／opaque、颜色分段边界、
短plane／负stride／高位／额外plane／别名／取消和容量等号／+1。
同构建Core全套`presentation-core-full-01.log`为1520/1520，Media全套
`presentation-media-full-01.log`为259/259。
这些是受管转换功能证据，没有据此声称真实PNG/JPEG/video decoder、媒体背景呈现或GPU已接通。

## 第四片：Canvas 面积查询与最终底色合成（产品0.112.19）

`MediaCanvasMomentQuery`以double反映射当前placement的媒体交集，按完整查询面积平均七原量，
合法留白贡献透明源；随后`MediaMomentComposer`统一source-over底色，再计算预乘亮度二阶量
与方差。先统计再求方差，保留aYp和a²关联；唯一负方差归零界为13δ＋1e-12，不扩展到
颜色、网格或GPU误差。现有Source float入口精确提升，复用同一个内部double积分内核。
实际SDK的Rect2坐标为double，本片不要求作者先降为float；SDK服务仍未接线。

真实定向回归先暴露旋转非方SAR显示右边缘被反映射为1.0000000000000002；新增独立
端点回归`canvas-placement-endpoints-red-01.log`为3/3失败。明确边界对应修复后，第二轮
发现未裁切源经乘除舍入仍可能宽于单位区间，`canvas-placement-endpoints-red-02.log`为
3通过／1失败。现在完整未裁切源保持原extent，正反映射的声明边缘精确对应；非边缘点
仍用原仿射式，无容差clamp，legacy int placement方法未改。像素oracle及容差未放宽。

`build-canvas-moments-04.log`完整solution0警告／0错误；最终几何／legacy定向
`canvas-placement-focused-01.log`为24/24；Media全套`canvas-media-full-01.log`为275/275，
含16项Canvas／合成独立像素oracle及原Source回归；同构建Core全套
`canvas-core-full-01.log`为1524/1524。首轮定向为32通过／1失败，上述几何修复后通过；
首轮构建因测试数组切片触发CA1832，改AsSpan后通过，未关闭分析器。
本片仍不包含GPU统计纹理、实际背景绘制、SDK、任务或HTTP验收。

## 第五片：MFT1 流式编解码与时间查询（产品0.112.20）

`MediaFeatureTrackCodec`接严格F/I串行builder、有界三section spool、流式writer及完整校验
reader；builder只留一个待配对量化grid，不留借用像素。所有数组在owner账准入后分配，
交接write source前同时计入两份活跃buffer。spool＋candidate磁盘账由调用方预留；本片
只定义接缝，尚未实现生产文件store／Job发布，writer返回身份不等于已经提交制品。

reader先校验完整hash、唯一布局、保留字段、全部raw rational与canonical区间、逐帧
stream timebase及重算prefix，再接管只读stream。重新hash不能隐藏错误布局或中途
timebase变化；最终可信endpoint允许另一约分timebase。输入外部不可变及授权lease由
运行owner保证；CanWrite=false本身不是外部文件不可变证明。

查询按64帧prefix差及两侧最多各64帧做整数累计，零窗按半开区间选帧，跨零保持首帧，
freeze尾部保持末帧，loop整圈相乘；Int128先计算窗口起点，接受long两端而不溢出。
静态图没有虚构duration。取消后可重查；reader Dispose与在途查询串行排水，失败open
不夺走caller stream，成功open才转所有权。

`build-track-codec-03.log`完整solution0警告／0错误；`track-codec-focused-02.log`为50/50，
同构建Media全套`track-codec-media-full-01.log`为325/325。独立手写wire／SHA／prefix及
逐微秒oracle覆盖65帧非均匀duration的builder→writer→reader全字节、64帧边界与尾块、
long极值、短源两百万圈有界I/O、512帧prefix路径、重新hash的坏结构与生命周期负例。
首轮48通过／2失败是video夹具只预留100000B，少于一帧spool＋candidate的147904B；
修为200000B后触达timebase／取消目标并通过，生产准入未放宽。首轮构建的两处CA1859
仅在测试变量改用已知具体类型后解决，未抑制分析器。尚无真实decoder／SDK／GPU／HTTP证明。
