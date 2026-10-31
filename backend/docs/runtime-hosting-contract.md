# 正式运行宿主合同（F8a，0.47.0）

2026-10-08。承接[代码审查修复计划](archive/plans/code-review-remediation-plan.md) §11／ADR-0177。本文只记录正式会话抽离；独立运行验证执行器、试样作者服务、布局应用用例及旧作者入口仍由F8b／F8c接续。

后续0.48.0已接通F8b的固定验证执行器、作者试样／媒体服务及Application布局用例，见[验证与布局合同](runtime-validation-and-layout-contract.md)／ADR-0178／roadmap §18.151；本文的会话资源边界保持，旧作者入口及编译绑定继续F8c。

`src/MakoKaraGenerator.Runtime.Hosting`组合既有Scripting、Rendering.Skia和Media后端，不引用Avalonia、SukiUI、App可执行项目或本地化控件。公开[ProjectRenderSessionFactory](../src/MakoKaraGenerator.Runtime.Hosting/ProjectRenderSessionFactory.cs)实现既有`IRenderFrameSessionFactory`，返回中立`IRenderFrameSession`。调用者显式提供参考程序集目录、编译产物缓存目录、视频提供者和ReplayReason；宿主不读取App静态状态或UI配置。

## 固定输入与独立资源

工厂先固定传入的`ProjectSnapshot`。静态路径不初始化编译器；有效效果／源材质路径继续离线编译真实包，严格验证图片、shader、图和材质，再装配各次运行自己的native程序、图片输入、预算和准备帧源。缺少资源、编译失败或取消不给出部分效果或旧修订替代。

工厂中的惰性编译器及磁盘产物可以复用；控制器状态、重放代次、绘制管线、原生图／图片和预算不能跨会话共享。预览、导出、运行验证各自创建并拥有会话。注入的视频提供者由组合根拥有，宿主只释放自己打开的背景解析和解码会话，不接管提供者本身。

`ProjectRenderSession`继续为内部实现。每次渲染先检查创建时的快照引用，链接调用参数和RenderRequest的两个取消入口，串行准备和绘制同一时间／目标／输出模式。关闭等进行中渲染到安全点，后续渲染明确报已释放；关闭幂等。返回帧仍归调用者所有。

## 释放与失败

创建失败和正常关闭继续共用既有释放策略，顺序保持：准备帧源、像素管线、借用资源目录、固定图片预算租约、图片输入包装、原生包集合、表面预算。某一步失败后仍执行其余释放，首个异常保持原堆栈，`RuntimeCleanupFailures`保留全部异常。创建本身失败时，清理异常仍附加在原失败的`RuntimeCleanupFailure`，不改写原失败原因。

[ProjectEffectCompilationException](../src/MakoKaraGenerator.Runtime.Hosting/ProjectEffectCompilationException.cs)与固定来源记录迁入宿主。源码位置记录[EffectSourceDiagnostic](../src/MakoKaraGenerator.Runtime.Hosting/EffectSourceDiagnostic.cs)由正式编译和App作者编译共用，仅持有不可变代码／消息／逻辑路径／行列；既有DisplayLabel仍是无控件的固定位置字符串。App继续显示异常及映射源码定位，不持有编译器或原生对象。

## 依赖与调用出口

| 层 | 当前边界 |
| --- | --- |
| Core | 不引用宿主、Skia或GUI；领域和闭包格式保持 |
| Application | 不引用宿主或GUI；已有命令、保存协调和库目录保持 |
| Scripting／Rendering／Media | 不反向引用宿主或GUI，继续提供现有实现和中立接口 |
| Runtime.Hosting | 组合以上后端；每次正式会话拥有运行资源 |
| Export | 只消费中立会话接口，不引用宿主或App |
| App | 组合根显式创建宿主，预览／导出／作者验证仍通过原接口消费 |

[独立宿主测试](../tests/MakoKaraGenerator.Runtime.Hosting.Tests/ProjectHostingTests.cs)只引用宿主项目，实际编译固定包并渲染普通／透明帧，比较完整诊断及回退重放，关闭预览后另一个会话仍可运行。静态快照门禁、失败源码来源、两个取消入口、关闭等待及真实图片／native／预算清理均有定向断言。[依赖测试](../tests/MakoKaraGenerator.Runtime.Hosting.Tests/BackendDependencyTests.cs)检查完整项目图与编译后程序集依赖，阻止后端反向引用宿主、GUI包或可执行项目。

版本升MINOR至0.47.0，对应新增公开正式宿主程序集及调用入口。作者SDK及中立渲染接口签名、工程／歌词／模板28／16／6、配置1、包profile2和固定NuGet版本保持；没有格式迁移。原internal会话仅向两个指定测试程序集开放，以保留既有故障注入，不构成对GUI产品代码的依赖。

构建、定向回归、变异和真实双输出证据见[修复验收](archive/acceptance/code-review-remediation-acceptance.md)／roadmap §18.150。按用户要求暂不跑全量；本批后台测试不替代57秒最终连续路线、原生物理输入、Windows或用户人工验收。
