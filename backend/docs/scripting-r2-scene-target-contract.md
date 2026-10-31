# R2 行、组与文字标记目标合同

> 2026-10-03；**0.9.9 已实施读取及标记生产输出，整个 R2 仍未完成**。施工基线 0.9.8；继续已接受 R0／SDK，生产 JSON 不改。用户已确认：组级只公开不透明度＋变换／渲染图，不伪造已唱／未唱文字材质。

## 1. 真实目标与基线

- LineTarget 保留 LineId＋BindingId；完整源行基线按工程→分支→行继承，元素基线额外解析元素覆盖。无元素的行仍有真实行基线。
- GroupTarget 仅 NodeId；GroupStyleView 只有静态 Opacity，当前源组无静态 alpha，故为真实中性值 1。动态附着／动画透明度单独读取，不混入静态基线。组没有 StyleView 的文字填充、描边或阴影。
- TextMarkTarget 为 NodeId＋CueId＋目标 BindingId。提示组可服务多条目标句，标记节点本身不能冒充一次装载；同一节点在不同前奏的目标不同。完整标记基线按 CountdownSequenceContent.Appearance＋文字自身覆盖解析，包括不可见／禁用提示。两态分别对应 Pending／Elapsed，不制造歌词 TokenId 或 TokenStarted。
- BaselineStyles 保留元素读取，加入源行／标记／组类型化读取，各实例仍读同一固定基线；未知／错误类型 ID 失败。

## 2. 帧对象和几何

FrameMark 明确源目标行装载、程序／分支／片段、Cue、文字、Elapsed 状态及动态透明度；FrameGroup 明确真实节点、运行父节点、可见性／动态透明度。两者没有伪造演唱进度。

标记固定 glyph、Advance、墨迹／基线通过现有整行缓存测量；GroupGeometrySample 使用真实参考尺寸／世界矩阵／枢轴及后代世界角点，不拿联合框当容器位置。跟随目标句的提示组保留运行父节点，而目录中的父节点仍是持久定义。

FrameContext 同帧读取 LineGeometry／MarkGeometry／GroupGeometry。只读基本布局查询扩展真实标记与组几何：按源筛选匹配标记的目标句，组仅返回这些真实显示对象的祖先；Project 返回完整节点几何。它仍不推进历史或求作者输出。

## 3. 本批生产输出

文字标记的 Style writer 和 ApplyAffine 支持完整目标，前奏阶段及当前装载严格验证。材料／描边／阴影／alpha 沿用已有类型化协议，局部矩阵在标记的真实世界矩阵内组合；字体和排版不修改。

输出范围：Project 全部；Branch／Lines／Clips 根据目标句关联；Node 根据本帧真实标记父树；Tokens 不获得整套提示的写权限。读取完整固定目录／静态基线与输出范围分离。每帧只接受实际可见标记，伪造 Cue／装载／节点、范围外、失效阶段、未声明能力拒绝。

PreparedRenderFrame 保存有效 MarkStyles／MarkTransforms 和最终标记几何；production 使用该帧同一 glyph 缓存，Pending／Elapsed 材质读取写入后的外观，动态附着透明度与最终样式透明度各乘一次。无变化输出省略，跨实例 Set 后覆盖、矩阵／alpha 按顺序组合，失败阻止导出。

0.9.9 本批不将只读行／组目标冒充已经接通全部 writer。0.9.10 已接续整行仿射和运行父树传播，见 [层次输出合同](scripting-r2-hierarchy-output-contract.md)／ADR-0074；行 Style／组 writer、组 capture alpha、材质／渲染图／Mappings 及两个视觉阶段继续按 R2～R3。组透明度的生产合成必须与捕获边界一起实现，不能只把每个后代的 alpha 相乘而声称完成整组 capture。

## 4. 验证

真实前奏、固定／跟随定位、节点复用多 Cue、父组／槽位／单句／入场、两态标记及注音区分、完整源基线／非法类型、各类输出范围、独立编译作者、原生像素与等价静态标记、alpha／装饰／仿射一次应用、独立会话／源引用不变、版本／全量测试／Git／启动按 DoD。重叠歌词接续仍待原问题答复，不在此处选择默认。


## 5. 0.9.9 实施证据

全量构建 0 警告／0 错误，1748 项测试通过、0 失败／跳过；本批新增 Rendering 25、Scripting 7，清单与本机日志见 [R2 记录 §3.7](archive/research/scripting-r2-sdk-geometry-progress.md#37-099-场景目标与标记生产验证)。实际编译 MarkProbe 的标记外观／仿射与等价静态编辑逐字节像素一致，覆盖两种附着与动态入场；多 Cue 复用、基线／范围／阶段／准备身份门禁及空白墨迹覆盖。现有真实编码与失败中止回归通过；新脚本 UI 和组 capture 不在当前已交付范围。
