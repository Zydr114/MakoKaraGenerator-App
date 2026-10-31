# 固定 shader 程序与 uniform ABI 实施合同

> 2026-10-03。施工依据：用户已接受的 R0 合同 A／B（ADR-0071），特别是 §6.2 和每 shader 初始 128 标量预算。本批先落实可复用的程序校验与 CPU 执行原语；完成证据在实现后补入 §5。完整 R2／R3 出口仍按路线图。

## 1. 职责和边界

- Core 保存唯一、不可变的 `ShaderProgramDefinition`：包内源码逻辑路径、固定 SkSL 内容、profile、uniform 声明及 shader child 声明。不引入新程序集或引用，不在本批修改 JSON。
- 中立 SDK 提供 `UniformValue`／`UniformValues`，只持有类型明确的帧值；没有源码编译、文件路径、Skia 对象或原生句柄。它是声明的运行时投影，不是第二套可持久化清单。
- Rendering.Skia 的 `CompiledRuntimeShader` 一次编译固定程序，原生反射检查清单，然后反复用新帧值创建 shader。脚本不能调用此后端入口。
- 本批不连接 PackageMaterial、Uniforms writer、Graph writer、文字／描边捕获或整行图，也不执行背景绕过。已有 R1 探针和生产普通样式保持各自入口；这些完整功能继续实施。

## 2. 类型和数值

| 清单类型 | SDK 构造 | 原生要求 |
|---|---|---|
| float | Float | float／half |
| float2／3／4 | Float2／3／4 | 对应向量；不接受相同字节数的矩阵 |
| int | Int | int32；布尔参数后续显式映射 0／1 |
| color | Color(ColorRgba) | `layout(color) uniform float4/half4`，直乘 sRGB RGBA |
| matrix3x3 | Matrix3x3 | 对应矩阵；SDK 按行传入九项，后端按 SkSL 列顺序打包 |

数组必须同类型、非空、不嵌套；清单显式长度与帧值完全相等。标量与长度 1 数组不同。颜色不是 float4 数据向量；色彩数据须有原生 color 标记，原始向量不得有此标记。每向量分量／颜色四分量／矩阵九分量／数组元素均计入预算。默认 128；宿主可明确传入其他正数预算，后续由 R4 持久化固定配置。

所有浮点输入有限。后端转换为 float32 时拒绝溢出和非零下溢为零，不夹值、不补缺值；正常 float32 舍入是该固定后端的数值约定。颜色沿用 SDK 的 [0,1] 校验，不预乘；shader 作者负责产生预乘输出。矩阵是一般 uniform 数据，不要求可逆，不改变文字布局。

## 3. 编译和输入校验

profile 固定为 `skia-raster-3.119.4-v1`。名称为大小写敏感 ASCII 标识符；源码路径是包内相对逻辑路径。重名、未知类型、无效长度先在 Core 拒绝。编译失败保留逻辑路径和原生诊断文字，不替换成空效果。

原生反射逐一核对 uniform 的名字、实际类型、数组标记／数量、color 标记、偏移／缓冲区边界及 child 类型；只接受 shader child。支持 color 和 raw coverage 两类输入，由后端调用者显式标明；原生只能确认其 shader 类型，不能从像素或名称猜测 coverage。完整 surface 编码和转换由后续图校验器保证。

创建帧 shader 要求所有声明都有值和对应 child，拒绝缺失、额外、错误类型／长度及已释放的输入。每次新建值缓冲，不沿用上帧残值。错误携带 code、源码逻辑路径和字段路径；取消保持取消异常。创建阶段不重新编译源码。

## 4. 缓存身份与释放

程序键覆盖源码内容、profile、固定 ABI 版本、后端版本和按 ordinal 名称排列的声明；不同声明不会串缓存。这里只给出键及可复用程序，不新增磁盘缓存或隐式全局缓存。

程序拥有编译 effect；返回 shader 由调用者释放。child 是借用输入，创建时必须有效，原生 shader 持有其引用；程序释放后已返回 shader 仍可绘制。实例创建、释放在同一锁内，释放幂等，释放后创建失败。取消和校验失败清理本批直接持有的资源。

锁定版 SkiaSharp 公共 ToShader 会内部复制 SKData，并由该内部包装的终结器释放；本批不通过反射调用其私有构造／方法。每帧直接持有的 uniforms／children 包装和 shader 仍显式释放，内部临时数据不进入宿主缓存。此上游释放时机限制不构成完整 R3 资源预算出口证据。

严格类型反射使用锁定版公开 C ABI 的两项查询，集中在一个内部适配器；不解析 SkSL 猜类型。依据：[SkiaSharp 3.119.4 的 runtime effect 实现](https://raw.githubusercontent.com/mono/SkiaSharp/v3.119.4/binding/SkiaSharp/SKRuntimeEffect.cs)、[同版本生成的 C ABI 绑定](https://raw.githubusercontent.com/mono/SkiaSharp/v3.119.4/binding/SkiaSharp/SkiaApi.generated.cs)。固定 native 子模块的[查询实现](https://raw.githubusercontent.com/mono/skia/7dbfc07dd33181f84e0958afb7ee805c6c769f0b/src/c/sk_runtimeeffect.cpp)直接复制内部结构，名称的两个存储槽与 C 头描述不同；适配器同官方 managed 绑定一样不读取这两个槽，实际名字从公共名字查询取得。首次失败和后续真实类型／像素回归验证此差异。未来升级后端必须重新验证 ABI 和像素，不顺手升级依赖。

## 5. 实施和验证

**0.9.13 已实现，schema 21／15／3 保持；本批新增 Core 10、Scripting 3、Rendering 26 项，总计 39 项。** 全量 `dotnet build MakoKaraGenerator.slnx -m:1` 为 0 警告／0 错误；`dotnet test MakoKaraGenerator.slnx --no-build -m:1` 八项目 **1863 项通过、0 失败／跳过**（Application 66、Core 850、Export 45、Media 87、Rendering 425、Scripting 127、App 179、Timeline 84）。最终日志 `/tmp/makokara-shader-build.log`、`/tmp/makokara-shader-tests.log`。

- 真实离线 Roslyn 编译只读 SDK 消费者；七类帧值在正式后端创建并实际绘制 CPU 像素，颜色 alpha 和非对称矩阵方向有数值断言。
- 每种类型的定长数组实际绘制；长度 1 的合法数组和错误的单值替代均覆盖。相同字节数的矩阵／向量、int4／float4、color 标记、数组标记／长度不符均明确拒绝。
- `layout(color)` 在 linear 输出中转换 sRGB .5 为约 55／255；普通 float4 数据保持约 128／255。真实 linear 编码图像普通 child 在 sRGB 输出约 188／255，raw coverage 保持约 128／255。测试直接写原始图像字节，避免把 Erase 的颜色转换误当 raw 数据。
- 清单、帧值和 child 必须完整，重名／未知／错误编码／已释放对象失败；默认 128 标量边界及显式调整、非有限和实际 float32 溢出／非零下溢、程序键、取消／幂等释放均验证。
- 多帧新值不污染已创建 shader；释放程序及借用输入之后，已创建 shader 仍可真实绘制。§4 所述内部临时 SKData 的终结器时机尚不能作为完整 R3 资源出口证据。

代码入口：`Core/Effects/ShaderProgramDefinition.cs`、`Scripting.Abstractions/UniformValue.cs`、`Rendering.Skia/Effects/CompiledRuntimeShader.cs` 和单个内部反射适配器。没有新增 GUI 操作、Material／Graph writer 或 JSON；原生产歌词画面不受本原语影响。下一步按已接受 R0 接上中立图／材质协议、捕获／合成与严格输出能力，完整 R2～R8 和六包成片仍未完成。

已直接启动当前 0.9.13 构建，PID **394287**，日志 `/tmp/makokara-f93b-shader.log` 显示版本 0.9.13，未见启动异常。旧版工程仍按精确应用版本门禁提示，未转换；启动不代替尚未交付脚本工作区的人工验收。

## 6. 包内固定程序声明接续（0.9.21）

0.9.13 的原生程序模型保持；0.9.21 按用户确认 A 加入独立 role:shaderProgram JSON、同包固定 SkSL 引用及全部程序真实 ABI 验证。未引用错误程序不能部分启用控制器；正式编译集合一次创建，后续帧只换值，程序／帧 shader 分开释放。资源修订与 C# Artifact 分离不变。详见 [包图补充合同 §2／§6](scripting-package-graph-semantics-contract.md)／ADR-0085；该批不等于生产文字材质、包图解析或完整 native RSS 出口。
