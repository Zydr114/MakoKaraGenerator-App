# 材质逐帧 uniform writer 接续合同

> 2026-10-04；基线0.13.2。现有包材质已可执行，`Materials.SetUniform` 尚不存在。
> 用户已确认 §2 全部三项：图节点 ID＋名字、显式部位、Set 按调用顺序清除旧覆盖。开始实施作者接口；不修改包编码。

## 1. 当前代码依据

- MaterialValue.PackageMaterial只保存固定MaterialHandle和完整ParameterValues；前奏、未唱／已唱填充及描边可分别绑定同一handle但使用不同参数。
- 材质引用完整图；同一图可有多个RuntimeShader节点、复用同一program，并合法声明同名uniform。ImageGraphBindings目前按node.Uniforms分别解析字面量、parameter和host绑定。
- StyleUpdate按调用／实例序合并，材质Set后项替换；普通alpha／阴影不改变材质的实际来源。原四参数示意`SetUniform(handle,target,name,value)`不能明确多遍节点或两态／描边位置。
- 执行器已有七类UniformValue／数组、真实Skia ABI反射和错误门禁；不需要让作者持有native shader，不增加帧内编译。

## 2. 已确认的三项

### U1：定位多遍节点

建议显式使用图节点ID＋uniform名字。例如`("refract","strength")`与`("blur-color","strength")`各自独立；不能仅用程序文件路径，因为同一程序可在图内使用两次。

备选是只允许uniform名字在整张图中唯一，遇到重名拒绝写入。这样API较短，但限制合法多遍图。两种方案都不默认为广播。

### U2：定位材质部位

建议明确选择UnsungFill／SungFill／Stroke，再给出固定handle和真实目标。基础字及注音继续使用同一族；标记两态使用同一slot协议。若希望修改多个部位，作者分别调用。

备选是同目标中所有相同handle一起修改，范围较宽、调用较少，但两态或描边可能被一起改变。

### U3：Set与覆盖的顺序

建议按调用／实例序处理：材质Set为该部位建立完整新外观，并清除此前uniform覆盖；后续SetUniform修改此时实际匹配的handle。handle不匹配／没有包材质明确失败，不自动安装材质。这样作者可先Set再修改，也可由后一个实例调整前一个实例相同修订的材质；资源和目标写权仍须满足。

备选是把uniform覆盖作为独立链末阶段：合并全部材质Set后，再把覆盖作用于最终匹配handle；材质替换不会清除覆盖，身份不匹配仍拒绝。调用顺序较弱，覆盖更容易跨过中间的材质替换。

## 3. 已确认合同保持与实施要求

只能在Evaluate写出中立帧描述，不能修改工程、全局缓存或shader程序。名称、节点、类型／数组长度、固定资源修订、完整包参数、输出声明及实际目标范围严格校验。最终错误保留实际写入者；alpha或未修改属性不抢走错误归属。覆盖值只保存当前帧，跳转与重放通过同一源码求值。

签名为 `Materials.SetUniform(handle, target, MaterialSlot, nodeId, name, UniformValue)`，target 仅支持元素族／文字标记；slot 为 UnsungFill／SungFill／Stroke。没有任意SkSL字符串或native对象入口。图定义与包清单继续是静态真值，逐帧覆盖不修改它们。若将来需要保存覆盖配方，按工程实例参数／源码保存，不持久化某一帧的结果。

样式帧保留材质 Set／uniform 的有序操作；宿主逐实例检查固定包声明、节点、类型／数组和实际 float32 范围，然后按当时外观解析身份。只写 uniform 的后实例必须拥有同一固定包资源与目标写权。后续 alpha／阴影不抢材质归属；最终区域绑定记录最后材质写入者（包括 uniform 写入者）。同一节点／名字最后值生效，但无效的早期写入不能被后续 Set 掩盖。有序帧描述与覆盖数值按内容比较，独立求值／重放不因数组或数值对象的引用改变变成不同帧。原始参数完整校验；uniform 在其对应节点覆盖字面量／参数／宿主帧值，不改参数真值或资源修订。

验收需真实多遍重名uniform、不同部位、材料替换／多实例顺序、七类值与数组、非法类型／资源／范围、透明背景跳过仍校验协议、实际像素及MP4／Alpha同帧。实现与验证完成后在此追加证据。


## 4. 0.13.3 实施证据

三项规则已进入公开SDK和正式主Render，不改包／工程格式。新增有序材料描述、逐节点不可变覆盖、共享ABI门禁和真实最后写入者；透明回退也验证全部覆盖。原有帧比较需要操作序列／uniform数值的内容相等性，初轮全量暴露七项重放回归后已修复生产比较，原断言保持。

新增Scripting9、Rendering30、Export3，共42项；全量2527项、0失败／跳过，构建0警告／错误。真实节点重名／三种部位／跨实例顺序、七类／长度1与2数组／非法协议、前奏重放、MP4／Alpha同帧和200ms失败保护旧文件通过。最终日志/tmp/makokara-uniform-build-final.log、/tmp/makokara-uniform-tests-full.log；细项与边界以roadmap §18.65为准。未完成项目保持R2～R8原范围，不把本接口视为完整工作区交付。

最终0.13.3已执行直接启动；接续时已无该进程，日志记录版本及ALSA／JACK探测警告，未见托管异常。窗口IPC自动审批服务401导致未执行，不将启动视为人工验收。
