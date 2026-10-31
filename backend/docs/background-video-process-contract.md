# 背景视频进程与取消合同（0.45.0，2026-10-08）

本合同落实[审查修复F5a／R11](archive/plans/code-review-remediation-plan.md#8-f5媒体进程与背景帧r11r12b01)及ADR-0170。范围是Media的背景视频元数据探测和逐帧解码，不改帧号取整、短视频播放策略、借用像素缓冲或作者SDK。

`IVideoFrameProvider.Open`及`IVideoFrameSource.TryGetFrame`增加CancellationToken重载；Skia背景resolver/source也提供对应入口。旧同步签名保留，生产实现桥接不捕获调用方同步上下文的异步IO。接口默认桥仅在旧实现调用前后检查取消，无法打断第三方旧同步阻塞；实现阻塞IO的宿主必须覆盖新入口。

生产视频每次Open探测、每次TryGetFrame各有一个从启动前开始的10秒期限。取帧的期限覆盖重启、所有跳过的完整帧及逐字节读取，不按每个read重置。已有解码器可在后续请求继续使用，但本次失败／超时／取消终止并释放该来源拥有的进程，部分缓冲不会发布为成功帧。普通媒体错误／超时由Open返回null或TryGetFrame返回false并保留原因；调用者取消抛OperationCanceledException，不伪装成缺失背景。回收失败真实向上传播，不能当成已正常释放。

OwnedMediaProcess拥有自身Process和两个重定向管道，同时读stdout和排空stderr。元数据最多接受1,048,576个字符，超过即失败；stderr保留前32,768个字符及省略提示，之后继续排空。元数据完成还须等待真实退出和错误流结束，非零退出码明确失败。取消回调只终止自身进程树，所有者随后等待退出及错误流排空，每个释放等待上限3秒，并显式Dispose两个reader和Process。该释放余量独立于10秒操作期限，回收不能永久阻塞。

以上实现依据.NET官方[重定向输出](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.standardoutput?view=net-10.0)、[Kill](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0)及[Close](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.close?view=net-10.0)合同：Kill是异步终止，根进程退出不能单独证明全部后代已退出，Process.Dispose也不代替外部引用reader的释放。本机测试单独观测可控后代PID和继承管道；不扫描或终止其他媒体进程，不宣称能回收已脱离进程树的任意外部进程。

正式RenderRequest的取消传到resolver、打开及逐帧读取，背景捕获共用同一入口；取消打开不缓存MissingVideo。正式导出保留F1的取消及旧输出保护。主窗口视频导入捕获路径／provider，在后台完成探测与source释放，只将纯结果送回UI线程；既有工程操作门禁覆盖准备期间，不接受新建／打开／关闭造成的目标替换。失败保持原工程、dirty及历史，成功才执行原数据命令。

FfmpegMediaProbe及音频PCM的既有异步协议不由本批替换；本合同不声称所有媒体进程已统一。0.45.1按ADR-0171接通F5b：最近成功源帧及有效性与下一待读帧分开，同帧复用，覆写／失败／释放即失效；回退及远跳仍按原窗口重启，取消及借用合同保持。0.45.2按ADR-0172接通F5c：键包含媒体种类、规范完整路径、固定Hash／ByteLength及视频播放模式；适配／原名称不进入来源身份，图片忽略播放模式。来源保留至resolver关闭并分别释放，provider仍归组合根；锁外IO候选在发布时复核取消、关闭和同键胜者，未采用或迟到候选释放。已识别释放错误收集后继续尝试其他来源并报告AggregateException。不每帧hash，未固定外部同路径换内容仍须新resolver；本批不添加任意容量阈值。新增宿主取消入口按MINOR0.45.0记录，无新生产程序集、依赖或持久数据格式。实际复现、定向与变异证据见[修复验收](archive/acceptance/code-review-remediation-acceptance.md)，不代替全量、Windows、系统portal或用户人工验收。
