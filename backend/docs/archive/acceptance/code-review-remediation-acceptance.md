# 代码审查修复实施与验收

本记录承接[修复计划](../plans/code-review-remediation-plan.md)。用户已授权在 `dev/code-review-repair` 按 F1～F8 顺序实施；每批独立提交并推送该工作分支。2026-10-08用户要求暂时不跑全量，后续采用零警告构建、相关定向回归及必要的变异／真实出口验证。只有实际证据覆盖的批次记为通过，原 0.43.10 人工验收不扩展为本次修复验收。

## F1 · R01：导出事务（0.44.0，2026-10-08）

`IVideoEncodingSession.CompleteAsync` 现在仅完成暂存文件，并通过 `VideoEncodingResult.Artifact` 把独有文件的所有权转交调用者。释放已完成编码的会话不会删除该文件。`VideoExporter` 探测暂存路径、构造最终路径的报告，释放渲染及编码会话，再执行最后取消检查和同目录发布；失败及提交前取消只清理自身暂存文件。`Overwrite=false` 在实际发布时仍受检。

`Committing` 是短暂的不可取消发布阶段。完成通知失败和提交后的取消不会把已经发布的成片报告为取消或失败。验证阶段保留取消动作；后台进度中继先记录真实提交阶段，再把通知送到调用方线程。旧请求的排队通知不能覆盖最终状态。探测失败仍按既有告警处理，报告把物理暂存路径替换为最终路径。

本批改变公开编码宿主协议，按 ADR-0050 升 MINOR 至 **0.44.0**；工程／歌词／模板 **28／16／6**、脚本配置 **1**、包 profile **2**及依赖保持。精确程序版本门禁继续适用，不转换旧工程。

### 可复查证据

- [导出边界测试](../../../tests/MakoKaraGenerator.Export.Tests/ExportCommitBoundaryTests.cs)：真实 Skia／FFmpeg／媒体探测。先生成内容 A 的真实旧片，再用内容 B 替换；MP4、透明 MOV 在验证开始取消的两项回归已在未修复代码上失败，断言真实旧片字节被覆盖。素材内容必须不同，避免确定性编码的相同字节掩盖提前覆盖。
- 新增 **52项导出边界用例**：两格式、存在／不存在旧文件，渲染、封装、编码结束、验证开始、验证读取前后、渲染及编码会话释放后的取消；故意忽略取消的探测替身仍须受最后检查保护。全部断言旧目标字节或目标缺失、自身暂存清理及每会话一次释放。
- 成功用例确认报告最终路径、真实媒体流与透明通道，提交／完成时注入取消仍成功；完成观察者异常不改写结果。覆盖真实进程解码失败、渲染及写帧失败、两类资源释放失败、提交时并发出现目标及目录、禁止覆盖、探测告警和产物所有权交接。
- 真实损坏音频用例进一步暴露编码进程提前退出后写帧队列仍开放，生产者永久等待容量的问题。管道消费退出时现在关闭队列，等待写入返回真实编码失败并由会话回收。本轮首次全量在该等待处停止，不记为通过；修复后52项定向全部通过。用例设10秒外部取消期限，要求提前得到IOException，不能用到期取消伪装正常失败。
- [应用状态测试](../../../tests/MakoKaraGenerator.Spike.App.Tests/ViewModels/ExportStateTests.Commit.cs)：新增 **2项**，通过可阻塞探测和真实导出检查验证时可取消、提交时取消禁用、用户取消保护旧目标，以及最终成功／取消状态不被迟到通知改写。既有导出状态回归合计 **12项通过**，保留通知确实在调用方线程执行的断言。
- 变异检查：提前发布使原两项旧片断言失败；去掉最后取消检查使32项取消用例中的24项失败；恢复“导出中始终可以取消”使新应用状态用例失败。均为运行断言失败，变异代码已按原始文件内容恢复，没有提交故障版本。
- 第四项变异恢复未关闭消费队列，两格式真实进程失败用例均在10秒期限返回取消而未返回IOException，运行断言失败；修复后两项正常结束，无该测试进程或编码进程残留。

本机细日志及 TRX 位于 Git 忽略的 `artifacts/code-review-repair/f1/`，长期回归保存在上述正式测试中。恢复全部变异后的solution构建零警告／错误。重新构建后的全量测试已按用户要求停止，退出码143，测试及编码进程均已退出；停止前Application105／Core1207／Export154／Media89／Rendering944／Scripting235通过，Spike.App未完成、Timeline未执行，不能记为全量通过。日志及已完成项目TRX保留，不重跑该全量。

本批未操作用户 0.43.10 窗口；此处真实出口是实际编码文件与调用方线程状态测试，尚未宣称专用原生窗口或用户手工连续制作通过。最终原生连续路线与人工边界按计划 §13 单独验收。F2～F8及最终集成仍待实施。

## F2a · R02：工程音频身份驱动媒体会话（0.44.1，2026-10-08）

`AudioSessionCoordinator`位于无Avalonia依赖的Media程序集，独占播放器、波形、流信息和准备请求；`AudioSourceIdentity`比较规范路径、已接受修订hash及字节数。工程会话或来源改变增加代次，普通编辑和显示名变化不重载音频。导入先准备候选，成功后一次纯数据命令及资源交接；准备失败保留旧播放，领域提交后的取消如实成功。撤销／重做、打开／恢复经同一入口采用来源，立即停止并释放不匹配的旧曲；无音频进入Empty，失败进入Unavailable，目标工程和历史保持，播放栏提供明确重试。恢复不执行导入命令，不把工程标脏。

预先准备的新工程交接到同一协调器，不重复加载；迟到解码或播放器准备按请求及会话身份拒绝，释放候选。关闭幂等，即使后端忽略取消也不能重新接入；旧播放器释放错误保留真实诊断，不回滚已采用目标。新播放器安装后夹取并同步实际播放头，加载中不读取旧曲位置。程序PATCH0.44.1，格式28／16／6、配置1、包profile2、作者SDK及固定依赖保持。

### 可复查证据

- [音频历史测试](../../../tests/MakoKaraGenerator.Spike.App.Tests/ViewModels/AudioProjectHistoryTests.cs)中的撤销真实源及空工程两项已在F1代码上失败：模型A而播放器B、空工程残留波形。新增App用例共10项，涵盖异步撤销／重做、相同路径换工程、缺失／无流、重试、重复等待、普通编辑不重载、迟到准备与关闭。
- [协调器测试](../../../tests/MakoKaraGenerator.Media.Tests/AudioSessionCoordinatorTests.cs)新增8项，覆盖修订变化、显示名反馈、提交后取消、领域拒绝、忽略取消的恢复、旧失败、当前／候选清理异常及提交期间换工程。8项全部通过。
- App音频生命周期、历史、播放、循环和新建合计45项通过，包括原必填音频＋主歌词准备门禁及迟到演示保护；最终再加入F1导出状态12项，合计57项通过。真实双WAV用例通过实际媒体解码核对不同1秒／2秒波形、峰值、模型和传给播放器的源路径；后端控制由测试替身观测，不宣称人工听辨已通过。
- `bash tools/ui-suki-compatibility/layout-check.sh --audio`只运行音频定向Headless流程：760／1440宽、深浅主题的重试按钮、失败原因、后台准备回到UI线程、波形绑定、历史保持及关闭共33项通过。真实点击路由被执行；不连接原生桌面，不操作用户窗口。
- 四项变异均触发实际运行断言：移除历史媒体协调4项失败；忽略同路径新工程代次1项失败；移除媒体修订身份1项失败；领域提交后仍按取消拒绝候选1项失败。变异已恢复；最初历史变异因未使用字段编译失败，不作为证据，修正变异后才计入上述断言结果。

本机日志及TRX位于`artifacts/code-review-repair/f2a/`，定向Headless隔离profile与构建位于其日志首行目录。最终当前源码构建零警告／错误，恢复变异后的定向结果分别记录；按用户要求没有运行全量。F2b的EOF、K幂等暂停及过期结束回调尚待接通，物理键盘／IME与试听人工验收另记，F3～F8及最终集成继续。


## F2b · R05／R06：播放结束与键盘语义（0.44.2，2026-10-08）

Media层协调器统一开始、幂等暂停、停止、定位及轮询。音频线程的结束回调只入队，按播放器引用、工程代次和播放段消费；UI线程轮询实际状态及位置，静音回退也推进结束状态。无循环EOF停在末尾，后端归零时仍保留末尾播放头；播放按钮／L／Space从0重播。有效循环出点到达时从入点继续，出点超过音频只在运行时夹取，不改变选区；J保持后退并正放及允许回到入点前的规则。K反复按下不会启动停止／暂停的音频；Space按下沿只切换一次，松键或失焦清除记录。

SoundFlow适配器按实际内层SoundPlayer身份消费完成通知；被替换解码段的后台回调不能改写新段起点／状态。关闭先取消订阅及脱离当前段，再释放资源。已完成段重播重建解码，普通暂停／继续避免无意义的Seek重建。手动Stop释放不可定位的FFmpeg解码段，下一次从0重建；不能靠内层Stop的无效Seek制造界面归零而流未复位。固定SoundFlow1.4.1源码的事件sender约定见[官方固定提交](https://raw.githubusercontent.com/LSXPrime/SoundFlow/16aa7783cf512cb2c89b11f0b774407104b4361a/Src/Abstracts/SoundPlayerBase.cs)。不新增包、程序集、数据字段或迁移；PATCH0.44.2，格式28／16／6、配置1、包profile2及作者SDK保持。

### 可复查证据

- [播放结束回归](../../../tests/MakoKaraGenerator.Spike.App.Tests/ViewModels/PlaybackCompletionTests.cs)最初的两种后端EOF用例已在0.44.1失败；窗口路由最初在“停止时K”失败。增加14项App用例，覆盖静音时钟与事件后端、EOF重播／J、K重复、文件末尾及超出音频循环、无事件末尾、后端归零、定位后的旧通知、换源／重复关闭及后台不直接通知VM。与F2a相关音频／播放／循环／新建及F1导出状态合计71项通过。
- [原生完成桥测试](../../../tests/MakoKaraGenerator.Media.Tests/PlaybackCompletionBridgeTests.cs)2项执行生产私有回调及消费路径，验证旧内层解码段身份与后台只入队。测试用未初始化对象作身份令牌，没有创建音频设备，不能记作实际发声验证。与协调器8项／Silent既有11项合计21项通过。
- `bash tools/ui-suki-compatibility/layout-check.sh --playback`定向133项通过，真实MainWindow键盘／按钮路由、后端／播放头／按钮文案一致，760／1440及深浅主题、文本焦点、重复按键及失焦清除。两次Space按下的新增用例先在未去重代码失败；初次三次按下会因奇数翻转漏检，已改为两次并取得实际失败，未把初次通过当去重证据。原音频重试Headless33项继续通过。
- `bash tools/ui-suki-compatibility/run.sh --native --product --playback`专用隔离原生窗口147项／5图通过，实际缩放1.25。复用同一有限命令流程，播放器使用可控时钟的Silent实现；截图核对窄窗播放栏及当前0.44.2标题。另外生成真实2秒WAV，以SoundFlow／miniaudio真实设备和FFmpeg解码、零音量验证推进、重复K、Stop释放旧段／归零重播、EOF重播及文件末尾循环。该真实后端证据不代替声音听辨。自动结束后窗口及测试进程退出，不操作用户已有0.43.10窗口；物理按键／IME、声音听辨及多屏／Windows另记，不由路由检查推定。
- 五项变异均成功编译后触发运行断言：K恢复Toggle在停止入口失败；移除VM轮询使两种EOF用例失败；禁用播放中循环重启使4项循环用例失败；取消内层解码段身份校验使2项桥测试失败。恢复Stop只保留旧不可定位解码段使真实原生停止断言失败。变异按原文件字节恢复，没有提交故障版本。第五项初次复用runner缺少截图目录环境，在截图处失败，不计为证据；补全隔离环境后才得到真实停止断言。

当前源码重新构建零警告／错误，恢复全部变异后App71项、Media21项通过；日志、TRX及变异记录为`artifacts/code-review-repair/f2b/`，原生截图路径见`native.log`首行。按用户指示没有运行全量。F1／F2a已提交推送；本批随后提交推送，再继续F3～F8及最终集成。试听、物理键盘／IME和最终连续人工验收保持未完成，不能据此关闭整个修复目标。

## F3 · R03：库仓储与并发提交（0.44.3，2026-10-08）

F2b已以`ce70061`提交并推送工作分支。TemplateLibraryRepository／ScriptConfigurationRepository采用同一提交原语，Core保持各自闭包校验与schema；读取一次实际索引字节并返回SHA-256修订，缺失、空库、JSON或UTF-8损坏不混为同一修订。生产保存、删除和模板导入不再绕过仓储。提交在OS范围锁内读取完整库、严格检查expected revision，资源先发布，索引最后检查及原子替换；成功返回自身发布字节对应的固定快照，锁外通知一次，提交后取消或观察者异常仍成功。

锁文件持久保留且不替换；所有权由OS持有，进程退出释放。进程内额外门禁避免同进程文件描述符锁语义造成互斥失效。同步保存遇到忙锁明确拒绝，异步导入等待可取消。不宣称能够锁住任意外部编辑器：检测到的变化拒绝旧修订，最后检查和替换之间的非协作写入仍存在边界。已有包不覆写、不删除；准备失败可能留下无引用不可变包，索引及旧内容保持。

两个作者冲突时保留原始字段、源码、参数错误及作者历史。刷新只采用目录和修订；原对象变化或被删除后普通保存继续受阻，显式副本取得新ID并以新读取修订提交。明确放弃采用最新保存对象，已删除则返回基线／空白；非法草稿不能借副本绕过门禁。模板归档重复ID规则保持，分享拒绝库锁路径，固定分享不修改库。

### 可复查证据

- [双工坊回归](../../../tests/MakoKaraGenerator.Spike.App.Tests/ViewModels/LibraryConcurrencyTests.cs)的空库旧快照用例先在0.44.2失败，断言后保存应拒绝却返回true。新增16项覆盖新草稿重试／直接副本、同对象改写／删除、非法输入及历史保留、放弃采用当前版本、陈旧删除、刷新后旧配置不得删除新版和独立脚本配置路线。相关工坊传输、草稿和脚本配置／参数共92项通过；分享库根用例明确断言仅新增归档和持久锁文件，所有原资源字节保持。
- [仓储回归](../../../tests/MakoKaraGenerator.Application.Tests/LibraryRepositoryTests.cs)与[真实子进程](../../../tests/MakoKaraGenerator.Application.Tests/LibraryRepositoryTests.Processes.cs)14项全部通过。其中7项创建独立worker进程，分别持有自己的初始快照，覆盖同刻／先后新增、同ID编辑、删除与真实.mktpl导入两种竞争顺序、OS持锁期间取消及强制退出后重试、独立配置竞争；第二份旧修订均整次拒绝，未把单进程双VM代替真实并发证据。worker每次有期限，finally终止自身进程，不遗留等待循环。
- 仓储其余用例覆盖缺失／空库／坏UTF-8字节身份、资源写失败、真实Linux目录权限导致索引发布失败、索引准备后的外部字节复查、返回自身固定修订、通知只一次及观察者取消／异常。最后复查用例直接调用持锁Core checked Save，以陈旧修订验证即使新资源已准备也不覆盖当前索引；不宣称测试封闭任意外部编辑器的最后竞态。既有Core模板／配置库23项继续通过。
- `bash tools/ui-suki-compatibility/layout-check.sh --library`定向Headless57项通过：实际文本／按钮／选择绑定、刷新后的清洁旧对象和非法参数保留、副本ID与两份提交、损坏库修复，以及760／1440宽和深浅主题。`bash tools/ui-suki-compatibility/run.sh --native --product --library`专用隔离原生64项／8图通过，实际1.25缩放，所有刷新／副本按钮位于视口。注入文本与路由点击不等同物理键盘／IME或最终用户人工验收。第一组截图发现窗口局部主题与应用共享资源不一致，已修正测试设置并增加应用颜色断言；最终新隔离profile图像已复查，不把初次浅色截图记为正确主题证据。
- 四项变异成功编译后分别触发实际断言：去掉初始及最后修订检查导致双工坊第二次保存错误成功；去掉OS范围锁导致持锁进程期间等待不再取消；去掉Core最终复查导致外部新索引被覆盖；刷新同步样式字段把非法原文改为72。首次修订变异仅遇CA1822编译门禁，不计为证据；保持实例访问后重新执行才得到运行失败。全部变异按原字节恢复，最终0.44.3重新构建及App93／仓储14／Core23、Headless57／原生64通过。最后UI复核构建期间遇磁盘耗尽，Headless明确报告No space left on device，原生构建同时中止并报告Stack overflow；两次均未进入验证，不计为产品运行证据。只清理本批旧生成build目录，保留日志／截图／配置，再复用原隔离目录重新构建与执行，取得最终通过。

日志、TRX及变异记录为`artifacts/code-review-repair/f3/`；最终Headless隔离目录`artifacts/ui-headless-layout.Rj2Re8`，原生`artifacts/ui-suki-compatibility.oJk6i8`。当前solution构建零警告／错误，按用户要求未跑全量。PATCH0.44.3，工程28／歌词16／模板6、配置1及包profile2保持，无新生产程序集或依赖，不转换旧工程。ADR-0166和模板传输合同、使用指南及入口状态已同步；本批提交推送后继续F4～F8，库目录缓存及异步保存属于F6，整个修复目标及最终连续人工验收仍未完成。

## F4a · R04：原始作者输入与合法预览（0.44.4，2026-10-08）

F3已以`52b1885`提交并推送。工坊作者快照现在分别保存最后合法的LyricStyle、每字段原始文本／解析及外部错误／写入选择、复合编辑器控件输入、各材质槽参数及配方表单。合法字段只更新自己的值及确实变化且没有未完成输入的派生字段；不会全量同步其他输入。非法输入参加dirty、离开检查与独立作者历史，普通保存、副本和传输继续受阻；撤销／重做恢复原文和错误，明确放弃、载入另一个草稿、明确清除该项才重置相应输入。

渐变几何／停止点、描边宽度／颜色／连接和阴影距离／模糊／角度使用共用的会话内控件输入编码；不写入工程或模板格式。保存直接读取当前控件，覆盖TextChanged尚未投递的情况。未改动的建议描边／阴影不会因失焦或保存而启用。宽度修改保留已有渐变材质。滑块Thumb及方向盘自身的捕获丢失结束手势；连续一次手势只产生一次撤销。

材质参数逐项基于最后合法覆盖提交，其他非法数字／颜色／曲线原文与错误保持，隐藏槽位的错误同样参加门禁。同一固定包／导出中的普通提交保留参数对象和输入控件，不因重建失去输入位置。新默认值不制造覆盖，改回默认删除本项覆盖；修正错误后返回既有相同合法值不改写旧覆盖。文字、枚举和图片字符串读取为实际字符串，不把JSON引号再次写入；整微秒和整数仍使用Int64，沿用脚本参数面板的既有精度规则。配方未提交表单也参与dirty及历史；读取已有配方本身不制造修改。

### 可复查证据

- [原始输入回归](../../../tests/MakoKaraGenerator.Spike.App.Tests/ViewModels/TemplateWorkshopInputDraftTests.cs)新增9项。最初四项在0.44.3上实际失败，分别捕获字号原文被透明度提交清除、纯非法颜色不标脏、撤销丢失原文和渐变外部错误被解除。新增覆盖同材质合法颜色与非法数字／曲线共存、隐藏槽门禁、配方表单恢复、单项明确清除及大整数／时长9007199254740993不经浮点、实际文字保存重载与控件对象保持。与工坊、配置／参数、库并发及画面应用相关App112项全部通过，零失败／跳过。原12项旧断言把未提交输入当作“未修改”，按本批已接受规则改为dirty；其合法预览、旧磁盘字节、传输拒绝和明确放弃断言仍保留。
- `bash tools/ui-suki-compatibility/layout-check.sh --draft`定向Headless129项通过，覆盖760／1440、深浅主题，实际TextBox／选择／保存按钮、控件重建、撤销／重做、隐藏错误、尚未投递文本的同步保存读取，三类滑块、方向盘及同字段捕获丢失后的新编辑。第一次阴影滑块流程在Headless和原生都发现建议值失焦多产生一次撤销；修复无操作提交后当前流程通过，旧失败日志保留。
- `bash tools/ui-suki-compatibility/run.sh --native --product --draft`专用隔离原生160项／8图通过，实际1.25缩放。实际正式异步画布返回匹配当前合法快照的Ready；非法材质数字／曲线保持而合法颜色可以提交，取消离开保持原文。注入文本与路由指针不等于物理键盘／IME、用户人工连续制作或Windows验收。初次截图发生视口重新定位，不能据此声称图中对应输入已可见；探针改为先准备专用浮动视口、唯一窗口标题，并在实际截图前重新定位和检查输入，最终图像单独复查。测试材质增加颜色／曲线时也补齐图参数声明及文件hash，不把早期未补齐声明的图像记为正式预览成功。
- 五项变异均成功编译后引发实际断言：恢复全字段同步、撤销时同步合法值、忽略复合控件原文恢复、Thumb捕获丢失不结束手势、一个合法参数清除其他参数错误。没有将编译失败或仅检测代码形状当证据；全部源码按原始字节恢复，恢复hash及运行断言保存在`artifacts/code-review-repair/f4a/mutations/`。

当前0.44.4 solution构建零警告／错误，恢复变异后定向结果复查通过，原库刷新／副本Headless57项继续通过；日志／TRX位于`artifacts/code-review-repair/f4a/`。本批复用自身两份UI runner，保留前批日志、截图和配置，未操作用户0.43.10窗口，专用窗口运行后退出。按用户要求未跑全量。PATCH0.44.4；格式28／16／6、配置1、包profile2、作者SDK及固定依赖保持，无新生产程序集或迁移。F4b迟到包导入、F4c任务命令、F5～F8及最终连续／人工验收尚未完成；本批不关闭整个修复目标。


## F4b · R08：包导入属于发起作者（0.44.5，2026-10-08）

F4a已以`7235e5a`提交推送。本批先在0.44.4通过可控picker与读取任务补齐确定性复现，目录及`.mkfx`分别在picker返回前／读取中切换新草稿，四项都实际污染新目标候选包与撤销状态；失败日志保留，不能把此前静态调用链结论替代本次运行证据。

共用[作者请求会话](../../../samples/TechnicalSpike.App/Workspaces/AuthorRequestSession.cs)持有会话内稳定草稿GUID、代次、独有取消源与读取完成出口。[工坊](../../../samples/TechnicalSpike.App/Views/TemplateWorkshopWindow.axaml.cs)在picker前获取请求，picker返回时检查目标后才读取，读取完成再次检查后只导入原草稿。新建、选择另一模板、明确放弃及离开使旧请求失效；普通字段编辑、撤销／重做和首次保存获得持久模板ID不会改变作者目标。过期结果与错误不改新草稿；失败释放请求槽并显示原目标的错误。成功先结束当前输入手势，候选包另占一次独立历史，不覆盖名称、原始非法输入及最后合法预览。复查实际配方控件又捕获目录刷新以普通选择setter回填默认表单的问题；刷新现在保护ItemsSource变化并恢复实际选中项，保留名称、非法JSON及表单历史。该新增失败日志和一次中间修复仅保留模型选择而控件丢失选择的日志也保留，最终以实际控件选中项断言为准。

同一作者的包导入与`.mktpl`传输只持有一个请求；独立作者并行。包导入保留普通编辑，`.mktpl`仍沿用清洁草稿检查及实际传输期间冻结。离开／关闭取消可取消读取，并等待自身读取完成及释放，即使读取忽略取消也不能提前完成关闭；不可取消的系统picker不参与关闭等待，晚返回不启动IO。[脚本配置窗口](../../../samples/TechnicalSpike.App/Views/ScriptConfigurationWorkspaceWindow.axaml.cs)复用相同协议；普通配置编辑接受候选包但保持当前包／导出与参数，不用普通编辑修订拒绝整个导入；源码／运行门禁保留。

### 可复查证据

- `bash tools/ui-suki-compatibility/layout-check.sh --package-import`运行[有限异步流程](../../../tools/ui-suki-compatibility/PackageImportFlow.cs)，Headless99项通过。覆盖目录／`.mkfx`在picker／读取阶段换新目标及已保存模板、同目标实际TextBox名称／非法字号和合法透明度、候选包单步撤销／重做、首次保存变化持久ID、空picker取消、原目标失败与过期失败隔离、协作取消和忽略取消的读取、关闭／离开等待释放、独立作者、脚本配置字段及关闭、`.mktpl`互斥及过期picker释放。两种正式读取分别使用实际GlassInk目录与本批完整`.mkfx`归档，保持固定修订；不以控制器单元比较代替生产入口。
- `dotnet test tests/MakoKaraGenerator.Spike.App.Tests --no-build -m:1 --filter 'FullyQualifiedName~TemplateWorkshop|FullyQualifiedName~TemplateAuthorFlow|FullyQualifiedName~ScriptParameterPanel|FullyQualifiedName~ScriptConfiguration|FullyQualifiedName~ScriptSource|FullyQualifiedName~LibraryConcurrency|FullyQualifiedName~Design|FullyQualifiedName~Appearance'`相关119项全部通过，零失败／跳过。保持原模板闭包传输、库冲突、原始输入／精度及脚本源码保存规则。
- `bash tools/ui-suki-compatibility/run.sh --native --product --package-import`专用原生100项通过；普通字段和配方表单保持、实际选中项、独立作者与关闭读取同样验证，实际视口1.25缩放。另用`--scripts`在独立配置重跑现有脚本源码／关闭流程24项及2图，确认源码失败保存、关闭取消、普通编辑接受候选及关闭后迟到picker；当前专用窗口均退出，不操作用户窗口。注入picker与字段不推定系统portal或物理输入。探针重建时曾把独立profile中故意非法的已保存C#源码当作runner编译输入，测试脚本现明确排除config／state／cache／build目录；该编译失败不计为产品失败或变异通过。
- 六项变异成功编译并触发真实运行断言：忽略草稿切换、过期picker仍启动读取、读取完成不检查目标、普通名称编辑错误地更换作者身份、关闭不等待读取释放、恢复目录刷新时的普通选择setter以重置配方表单。变异前／后hash一致，全部源码按原始字节恢复；结果与断言在`artifacts/code-review-repair/f4b/mutations/`。

日志、TRX及旧失败证据位于`artifacts/code-review-repair/f4b/`；恢复后的当前solution构建零警告／错误；原字段Headless129项及库流程57项继续通过。按用户要求未跑全量，未操作用户0.43.10窗口；Headless与可控picker不证明系统portal、物理键盘／IME、Windows或最终人工连续路线。PATCH0.44.5，格式28／16／6、配置1、profile2、作者SDK与固定依赖保持，无新生产程序集／迁移。F4c任务命令及F5～F8继续，整个修复目标保持未完成。


## F4c · R07：当前任务保存命令（0.44.6，2026-10-08）

F4b已以`f9e23a5`提交推送。先在0.44.5运行[真实入口流程](../../../tools/ui-suki-compatibility/TaskCommandFlow.cs)，有／无工程共取得五项预期断言失败；有活动工程时，工坊菜单保存实际改写`project.json`并清除工程dirty，模板仍未保存。Ctrl+Shift+S也实际执行普通模板保存。记录同时核对磁盘字节及两位作者状态，不把菜单标签或调用计数当作保存目标证据。

[任务命令上下文](../../../samples/TechnicalSpike.App/Workspaces/TaskCommandContext.cs)位于App层，每个作者拥有明确目标、动作、标签、快捷键、显示／可用判定、当前输入提交及执行互斥。Main选择首页／工程／工坊上下文；菜单、快速按钮、工坊按钮及键盘统一执行对应动作。歌词与画面保存工程，Ctrl+Shift+S仍实际另存为；工坊保存当前模板，隐藏工程另存为与导出，不把Ctrl+Shift+S映射普通保存，编辑草稿后可点击“复制为新预设”保留原对象并生成新ID。首页无活动作者时保存／导出禁用且不提示保存快捷键。没有全局事件总线或新持久化状态。

独立脚本窗口Ctrl+S与“保存脚本配置”按钮共用配置动作，非法参数或未保存源码保持既有配置保存门禁；源码用明确的修订保存动作，不因配置Ctrl+S隐式保存、编译或采用新源码。未提供Ctrl+Shift+S动作，按下不回落配置保存或后台工程。离开／关闭等待时同一作者命令禁用，内部确认中的明确保存仍执行既有保存流程；取消恢复输入和命令状态。动作执行前同步读取当前控件，再检查实际门禁，覆盖尚未投递TextChanged和键盘未失焦的情况。文本Ctrl+Z保留控件内部撤销优先级，新增／打开仍执行已有工作区离开检查。

### 可复查证据

- `bash tools/ui-suki-compatibility/layout-check.sh --task-commands`定向Headless93项通过。有／无活动工程下检查菜单、按钮、聚焦文本Ctrl+S、Ctrl+Shift+S及显式复制的实际模板库、工程字节和dirty；核对标签、InputGesture与显示状态。覆盖当前非法字号、关闭时合法／非法草稿、取消全局打开、实际文本撤销及窗口焦点路由；独立脚本配置库重读、非法JSON、源码修订保存／不采用、取消关闭和后台工程保护均通过。歌词Ctrl+S、快速保存、画面菜单保存和真实工程另存为对话及新包写入也通过。
- 相关App133项全部通过，零失败／跳过：工坊、作者、参数、脚本配置／源码、库并发、Design／Appearance及PlaybackCompletion。原Headless`--draft`129、`--library`57、`--package-import`99和`--playback`133继续通过。没有运行完整solution测试。
- `bash tools/ui-suki-compatibility/run.sh --native --product --task-commands`专用原生94项／2图通过。只调整本进程的自有窗口到1440×700，实际1.25缩放；同一命令流程验证真实控件与焦点、模态确认和工程写入。截图已复查当前0.44.6标题、工坊对象、保存模板及明确复制按钮；完成后专用窗口和runner均退出。使用路由输入不推定物理键盘／IME、系统portal、窄窗全面外观或用户连续制作人工验收；未操作用户0.43.10窗口。
- 五项变异成功编译并触发实际运行断言：菜单误用工程上下文、Ctrl+Shift+S普通保存、跳过离开门禁、子窗口Ctrl+S误存源码、窗口抢文本撤销。源码全部按原始字节恢复并核对hash；恢复后重新构建并执行上述定向出口，不把编译失败作为变异证据。

当前0.44.6 solution构建零警告／错误，日志、TRX、原失败、最终Headless／原生截图及变异记录位于`artifacts/code-review-repair/f4c/`。PATCH0.44.6，工程／歌词／模板28／16／6、配置1、包profile2、作者SDK及固定依赖保持，无新生产程序集。按用户要求暂不跑全量；F5～F8和最终连续／人工验收继续，本批不关闭整个修复目标。

## F5a · R11：背景视频期限、取消与进程回收（0.45.0，2026-10-08）

F4c已以`7af85bc`提交推送。先在0.44.6对实际自有测试进程运行三项旧入口复现：stdout永不关闭、stderr写满及只返回半帧均超过12秒外部护栏，三个真实TimeoutException失败保留在`f5a/reproduction.log`／TRX。旧进程只由自身PID标记清理，不扫描／终止其他媒体进程。

按[进程合同](../../background-video-process-contract.md)／ADR-0170拆出Media内部OwnedMediaProcess和单操作deadline。生产探测和一次完整取帧各共用10秒期限，异步读取与退出均接受同一token；双流同时排空，元数据输出超限拒绝，错误文本有限保留后继续排空。取消终止自有树，等待退出及错误流后显式释放两个reader和Process。新增句柄断言先捕获仅Process.Dispose不能关闭外部引用管道，修复后八次真实重复关闭及幂等Dispose通过，该初次失败不算最终通过。

宿主取消重载从RenderRequest进入resolver、Open和逐帧读取，背景捕获共用同一入口。取消抛出，不缓存Missing或按缺失背景继续出片；普通错误／超时保留原因，不发布半帧。旧同步生产入口有限且不捕获调用方同步上下文；第三方旧实现的默认桥只能在调用前后检查，不能打断其阻塞IO。主窗口后台探测并释放source，只回传纯结果；准备期间工程门禁保持，失败不改Project／dirty，成功才提交原命令。

最终0.45.0验证（均零失败／跳过）：

- Media新增21项，覆盖无期限输出、错误洪流／退出码／非法JSON／元数据超限、半帧、迟到帧、持续小块与多完整帧共用期限、准确caller token、取消／自然退出、同来源重试、可控后代继承管道且独立媒体进程不受影响、拒绝同步上下文及八次管道释放。原真实FFmpeg视频9项继续通过，实际打开、顺读／跳转及像素保持；合计相关Media30项。
- Rendering29项，包括4项正式Render／真实背景Capture在Open／GetFrame取消的token路径，以及既有背景绘制与整行图捕获。
- App22项，包含两个正式导出实际背景管道取消用例。先真实编码可识别旧MP4，再使新输出在probe或半帧阻塞，取消后抛OperationCanceledException、旧片逐字节相等、没有自身暂存视频、对应进程退出。其余覆盖静态／包材质／控制器独立会话、编译门禁及导出UI状态／界面纯度。
- Export22项真实普通／透明输出回归通过。
- Headless视频导入12项通过：实际菜单发起探测，UI dispatcher仍处理回调、pending阶段领域数据保持、项目操作门禁保持、失败／成功结果准确且source释放；自有窗口关闭。原任务命令93项继续通过，不操作或新开用户0.43.10窗口。
- 六项变异均编译成功并触发实际断言：不启动deadline、停止stderr排空、吞caller取消、丢失Render token、不Dispose管道及UI同步探测；按原始字节／SHA-256恢复后重新构建及上述检查通过。

可重复入口：

```bash
dotnet build MakoKaraGenerator.slnx --no-restore -m:1
dotnet test tests/MakoKaraGenerator.Media.Tests --no-build -m:1 --filter 'FullyQualifiedName~VideoProcessDeadlineTests|FullyQualifiedName~FfmpegVideoFrameTests'
dotnet test tests/MakoKaraGenerator.Rendering.Tests --no-build -m:1 --filter 'FullyQualifiedName~VideoBackground|FullyQualifiedName~SceneBackgroundCapture|FullyQualifiedName~BoundSceneImageEffect'
dotnet test tests/MakoKaraGenerator.Spike.App.Tests --no-build -m:1 --filter 'FullyQualifiedName~正式导出在真实背景管道等待期间取消|FullyQualifiedName~静态会话不依赖编译器|FullyQualifiedName~编译失败包含真实源码诊断且不发布DLL取消不建立会话|FullyQualifiedName~模板试样包材质经同一异步工厂|FullyQualifiedName~保存重开纯源材质或真实控制器|FullyQualifiedName~ExportStateTests|FullyQualifiedName~UserInterfacePurityTests'
dotnet test tests/MakoKaraGenerator.Export.Tests --no-build -m:1 --filter 'FullyQualifiedName~VideoExporterTests|FullyQualifiedName~TransparentExportTests'
bash tools/ui-suki-compatibility/layout-check.sh --background-video
```

当前solution构建零警告／错误，日志、TRX、变异及原字节恢复记录位于`artifacts/code-review-repair/f5a/`；测试worker为版本管理的BCL-only apphost，不调用shell或操纵桌面。按用户要求未跑全量。本机Linux／.NET10及可控后代的通过不推定Windows、已脱离树的任意外部进程、系统portal或用户人工验收。MINOR0.45.0对应宿主协议扩展，无新生产程序集／依赖，格式28／16／6、配置1、profile2、作者SDK及借用缓冲／帧号语义保持。FfmpegMediaProbe和音频PCM既有协议未重写；F5b重复帧缓存、F5c缓存身份、F6～F8及最终连续／人工验收继续，整个目标未完成。

## F5b · R12：最近成功背景帧缓存（0.45.1，2026-10-08）

F5a已以`f2eeb66`提交推送。真实30fps／64×48／3秒H.264以60fps请求`0, 16666, 33333, 50000, 66666, 83333μs`：0.45.0像素断言通过，但自有BCL worker转发真实FFmpeg记录四次decoder启动，`Assert.Single`真实失败；证据位于`f5b/reproduction.log`／TRX。fixture最初分析器错误及未匹配旧二进制的空运行不作为复现／通过证据。

按ADR-0171将最近成功帧号及缓存有效性与`_nextFrameIndex`分开。同一源帧先检查调用者取消／释放后直接返回原借用缓冲；前进顺读，真正回退或超过原窗口才重启。开始覆写与任何进程释放使缓存失效，只有完整成功后发布新帧号。失败后请求旧时刻也须重新解码，不返回已被半帧覆盖的旧缓冲。每source独立拥有缓存；既有测试也补齐source及stderr reader的明确释放。

最终0.45.1相关Media23项通过（新增6项、原真实视频9项及期限／取消等8项），App实际背景阻塞取消导出2项、Export普通／透明22项通过，均零失败／跳过。真实六次请求严格对照独立顺序来源像素且只启动一次；继续顺读到末帧、五次冻结保持一次启动。可控进程验证回退／远跳才有新启动、成功后半帧超时再请求旧时刻、缓存命中取消后重试、重复释放及同提供者两个source的独立缓冲；原像素下一次调用失效的借用语义保持。

三项变异编译成功并触发实际断言：绕过命中回到四次启动、保留失败缓存返回99开头的半帧及不重启回退返回错误帧。恢复原始字节／SHA-256后solution构建零警告／错误及定向复查通过；日志、TRX、变异及恢复记录位于`artifacts/code-review-repair/f5b/`。长期回归入口为Media的`VideoFrameCacheTests`／`VideoProcessDeadlineTests.Caching`及自有测试worker，不依赖shell包装器；实际FFmpeg由worker子进程转发，计数没有生产插桩。

按用户要求未跑全量，本批无UI改动、不新启动或操作用户0.43.10窗口；不推定原生、Windows或人工验收。PATCH0.45.1，格式28／16／6、配置1、profile2、作者SDK、宿主取消入口及固定依赖保持，未改变VFR或帧号／seek约定。F5c缓存身份、F6～F8与最终连续／人工验收继续，整个目标未完成。

## F5c · B01：背景缓存身份（0.45.2，2026-10-08）

F5b已以`d0abf26`提交推送。在0.45.1同一个resolver、同路径的一秒视频／2.5秒时刻，Loop→Freeze错误继续请求500000μs，Freeze→Loop错误继续请求999999μs；真实10fps／64×48／一秒H.264的冻结像素也与独立参考末帧不同，三项真实断言失败。正式快照每代重建resolver本就限制了影响范围，本批修复复用API／静态管线路径，不把原问题扩大为所有正式预览均错误。初始测试fixture字段／分析器构建错误及旧二进制运行不作为通过证据；旧失败及最终日志各自保留。

按ADR-0172将缓存所有权单独提取到BackgroundFrameSourceResolver.cs；键包含媒体种类、规范完整路径、固定Hash／ByteLength和视频播放模式。适配／显示名称不参与，图片忽略播放模式；不每帧hash文件。不同模式独立拥有来源，外部无修订的同路径换内容仍须新resolver，不由本批猜测mtime或静默升级工程固定应用。

缓存保留至resolver关闭，没有新增任意容量／淘汰阈值；清空逐条释放来源而不释放组合根provider。IO在锁外，候选由发起调用者拥有，锁内复核取消／关闭及已发布同键；未采用、迟到取消或关闭后的候选释放。关闭阻止再次发布；已识别的IO／状态／取消释放错误仍尝试其他来源后作为AggregateException报告，不伪装成成功清理。

最终0.45.2相关Rendering42项通过，新增身份／生命周期11项及原视频、取消、背景捕获、整行图和受管媒体回归；真实一秒视频循环500000μs和冻结999999μs像素严格对照独立来源，再切回循环正确。包含原名称／适配忽略、相对／完整／点段规范路径、同路径hash／长度改变、图片／视频分离、两resolver独立释放、两个并发候选只保留一个、忽略取消provider的迟到结果、探测途中关闭及某来源释放失败后其他来源仍释放。App正式静态／保存重开材质与控制器／真实背景取消8项、Media真实视频及缓存15项、Export普通／透明22项通过，均零失败／跳过。

五项变异均编译成功并触发实际断言：忽略播放模式、忽略固定修订、使用原路径字符串、遗留未采用候选及关闭后继续发布。恢复原字节／SHA-256后solution构建零警告／错误及定向复查通过。证据位于`artifacts/code-review-repair/f5c/`，长期入口为Rendering的BackgroundResolverIdentityTests及Lifetime，真实视频由测试自行生成；无需桌面／临时生产插桩。

按用户要求未跑全量，本批无UI改动、不启动或操作用户0.43.10窗口；路径大小写规则按既有宿主规则实现，不推定Windows实机验收。PATCH0.45.2，格式28／16／6、配置1、profile2、作者SDK、宿主取消重载、固定依赖及生产程序集保持。F5a／F5b／F5c定向范围已覆盖，F6～F8及最终连续／人工验收继续，整个目标未完成。

## F6a · R13／A03：后台保存及实际快照完成（0.45.3，2026-10-08）

F5c已以`a71c87b`提交推送。当前[后台保存合同](../../project-save-queue-contract.md)／ADR-0173将写入收敛到Application的[ProjectSaveCoordinator](../../../src/MakoKaraGenerator.Application/ProjectSaveCoordinator.cs)：UI捕获不可变输入，后台不读取可变ProjectWorkspace。每工作区一个写入者、一份最新自动待办和受命令门禁保护的手工请求；手工边界优先，另存为之后的待办使用实际目录。saved引用与lastBackedUp均使用实际写出的Project；新编辑继续dirty，同一手工已写快照不会因UI完成尚在排队而再备份，撤销到旧备份实例则仍建立更新的现场。

数据与恢复标记分别处理。自动标记失败保留重试资格；手工根入口已经发布后，清理旧标记失败只追加RecoveryError警告，不谎报数据失败。放弃先使Epoch失效并取消待办，异步等当前完整资源事务到安全点，再按AutoSaveSession比较清理自有标记。旧Epoch／Generation不更新新工程UI或保存引用。关闭等已接受手工保存结束，再确认并释放预览／音频；OnClosed不再补写。手工写入期间文本与撤销继续可用，文件操作仍互斥；状态明确区分已保存快照和新编辑。最近列表也在后台使用捕获的输入，警告与工程提交分开。

### 可复查证据

- [协调器回归](../../../tests/MakoKaraGenerator.Application.Tests/ProjectSaveCoordinatorTests.cs)新增13项，相关Application47项通过：阻塞写入时最新合并、最大一个写入者、实际引用／dirty、旧Generation拒绝、手工优先和另存为目录、失效后待办取消、标记发布竞争、数据／标记失败重试、根入口成功后的清理警告、手工成功后撤销再备份、界面完成待办中不重复备份、外部现场保护、观察者错误及100次空闲交接。既有workspace／修订／自动保存继续通过。
- Core备份／标记／媒体分析存储46项，相关App新建／音频工程历史／最近列表17项通过；均来自当前零警告构建的定向filter，不记为完整solution通过。
- [有限Headless保存路由](../../../tools/ui-suki-compatibility/PersistenceFlow.cs)23项通过：真实源文本控件在备份／手工写入阻塞期间仍能编辑，dispatcher继续处理输入；中间快照合并、手工只采用实际写出的快照、写入期间新备份按落盘目录执行；放弃关闭等待当前写入并清掉现场、关闭期间手工保存继续到完成、回首页等待旧备份安全点、复用原工作区只备份新Generation。实际存储替身记录全部写入／标记／清理都不在dispatcher。相关任务保存命令93项、音频恢复33项继续通过。
- 六项变异均先成功编译再触发实际断言：把saved改为完成瞬间的当前Project、只保留第一份待办、停止不递增Epoch、根入口发布后的标记错误混入数据失败、在UI内同步执行写入、关闭期间忽略已接受手工保存。前四项由保存引用／实际输出／旧结果／提交判据拒绝，后两项分别由dispatcher延迟和真实关闭路由拒绝。全部按原字节恢复；最终构建及定向回归在恢复源码上执行。
- 真实媒体基准使用300秒／48kHz／双声道PCM16 WAV（57,600,044字节）及60秒／640×360／30fps H.264（5,802,265字节），总计63,402,309字节；工程含三个固定包，库含100个普通模板／71,843字节。CPU为AMD Ryzen AI 9 H 365／20逻辑线程，CachyOS／.NET10.0.12，固定同一份媒体、包修订和模板，前后各五份真实独立备份。下表记录输入优先级dispatcher任务投递至执行的延迟，不是物理输入／绘制延迟。

| 基准 | UI保存请求耗时ms | 输入P50／P95／P99／最大ms | 独立备份 |
| --- | --- | --- | --- |
| 0.45.2修复前 | 281.51、192.45、197.51、189.38、190.62 | 43.9604／196.9522／262.9089／278.1772 | 5 |
| 0.45.3复测 | 1.2803、0.4400、0.0422、0.0746、0.0850 | 0.0053／0.0312／6.1172／21.9149 | 5 |

EventPipe／Speedscope的修复前保存链在UI线程947659出现`AutoSaveNow → ProjectAutoSave.Save → AutoSaveStore.Save → ProjectStore.Save → ProjectAssetCollector.Collect → ContentAddressFileStore.PublishMedia／Inspect → LiteHashProvider.HashStream`。复测相同ProjectStore／hash／写入链位于独立后台线程977104／977107／977133／977135，UI线程977087没有该保存链；结合阻塞存储的dispatcher断言与生产调用边界，确认保存IO已迁出。采样不等同CPU或完整native阻塞统计。初次复测P99为3.4914ms，恢复源码后复测6.1172ms，均低于该固定Headless基准50ms检查；不推定所有机器／原生／IME延迟预算。普通歌词编辑的模板库IO属于F6b，仍未修复。

- 完整独立备份搬至新目录，临时移开全部外部音频／视频后，ProjectStore.Load仍严格通过完整hash／长度／三个包，路径均指向搬移后的受管媒体；重建恢复服务也取得同一现场。[独立进程恢复入口](../../../tools/ui-suki-compatibility/PersistenceCrashRecovery.cs)另外启动专用后台保存子进程，在真实63 MB闭包和恢复标记发布后由父进程SIGKILL该子进程（实际exit -9）；外部素材不可用时新进程成功恢复全部媒体和三个包，PID不同。父子均有期限和清理，不留等待进程。这覆盖进程崩溃恢复，不冒称断电或用户人工验收。
- 初次基准受演示初始化干扰，只写四份并出现一份跳过，已排除：改为首页初始化，显式断言五次编辑都写出五份，以上只采用合格基准。第一次测试编译被CA1861阻止、首次Headless错误控件查找出现NullReferenceException，均不是运行通过证据。相关任务路由曾在85项后失败，发现saved标记先变干净而最近列表尚在写入；现将UI完成置于后台收尾之后，并保护已写出的同一实例免于重复备份，最终93项通过。

本机日志、TRX、线程栈、机器记录、变异、基准及崩溃恢复保存在`artifacts/code-review-repair/f6a/`，不提交媒体／库／编译缓存。当前solution与有限Headless构建零警告／错误；按用户要求未跑全量，未操作或新开用户0.43.10窗口，未声称原生、物理键盘／IME、Windows或完整连续人工验收。PATCH0.45.3，格式28／16／6、配置1、profile2、作者SDK、固定依赖及生产程序集保持。F6b、F7～F8及最终连续／人工路线继续，整个修复目标仍未完成。

## F6b · R13／A03：只读模板目录与后台失效复核（0.45.4，2026-10-08）

F6a已以`51a03c8`提交推送。按[模板目录合同](../../template-library-catalogue-contract.md)／ADR-0174，Application的TemplateLibraryCatalogue接受ITemplateLibrarySource，由组合根共享并显式注入画面控件。工程编辑、对象选择仅过滤内存；进入任务获得租约，后台对照真实索引字节，首次／修订改变／资源失效才展开完整闭包。单读取者合并请求，最后租约释放后不再读取，旧代次结果丢弃；视图合并通知并读取最新状态。文件通知仅提示失效，资源变化即使索引相同仍完整检查；缺失库监视最近存在的上级，不创建库或递归监视整个仓库。

F3保存通知释放提交锁后可能乱序。本进程发布先展示固定内容并标过期，后台核对实际索引后才允许新应用，旧通知不能把旧修订认作当前。失败保留上次成功内容，明确显示错误／过期并禁用应用和更新，路由调用同样受门禁；确认期间的目录代次变化拒绝旧预演。已有工程应用可以启停、移除或复制自身快照，库刷新不升级它们；作者草稿、原始文本与来源修订继续由F3作者模型拥有。

### 可复查证据

- 新增11项，相关Application27项通过：激活／隐藏零读取、只读快照访问、100次失效合并、单读取者、迟到旧读取、本进程发布和乱序旧通知、读取期间索引改变、错误保留／重试、多租约／观察者失败及异步释放。两个真实文件监视用例包含缺失目录中另一个进程新增／修改／删除，以及索引相同的包资源损坏、恢复和索引损坏；旧固定模板值保持。
- Core模板库／配方18项、App库冲突／工坊／固定应用／UI纯度40项通过，均零失败／跳过；没有将定向filter记为完整solution回归。
- [实际Headless目录路线](../../../tools/ui-suki-compatibility/TemplateCatalogueFlow.cs)29项通过：首页及隐藏区不读目录，实际歌词文本和选择变化零读取，显示显式注入的独立库，所有全库／索引读取不在dispatcher；真实确认建立固定应用，目录修订改变不升级工程且确认期间变化拒绝旧应用；损坏反馈、禁用和路由防绕过、已有应用可启停、用户刷新恢复、加载中切任务及关闭订阅均覆盖。新刷新按钮使用共享尺寸。相关Headless布局332、反馈22、保存23、任务命令93、作者库57项继续通过；两种主题及既有宽窄布局保持。
- 六项变异均先编译再触发真实断言：忽略迟到读取代次造成多余读库、错误时丢弃最后成功内容、允许过期／错误目录应用、只监视索引而漏资源损坏、把旧发布通知直接标当前，以及画面控件绕回静态App路径同步读库。最后一项由独立注入库的实际控件断言捕获，避免读取计数替身被绕过后误记通过。原字节／SHA-256恢复后重新构建和上述回归通过。

### 固定工程前后基准

复用F6a同一份300秒WAV／60秒640×360、30fps H.264，共63,402,309媒体字节，工程三个固定包修订。目录为100个实际引用GlassInk材质的模板，完整库101,980字节；不是F6a的100个普通模板。CPU为AMD Ryzen AI 9 H 365／20线程，CachyOS／.NET10.0.12。显示有限Headless窗口，分别隐藏／可见各20次真实选择和文本编辑；编辑测量在窗口及作者模型建立后开始，未把独立工坊初始建立作者草稿的读取计入目录循环。

| 测量 | 修复前0.45.3 | 最终0.45.4 |
| --- | ---: | ---: |
| 测量中目录全库读取 | 199，全部UI线程 | 1，首次激活在后台 |
| 隐藏编辑阶段全库读取 | 98 | 0，全库及索引均0 |
| 可见编辑阶段全库读取 | 100 | 0，全库及索引均0 |
| 输入优先级任务样本 | 1933 | 547 |
| 排队P50／P95／P99／最大，ms | 4656.5546／9128.6948／9545.9833／9642.2226 | 587.5139／1345.8969／1456.9587／1482.5511 |

修复前UI线程1042079的管理线程采样显示`RefreshTarget → RefreshApplicationObjects → RefreshApplicationSelection → ReloadPresets → 实际旧读库委托`，结合真实读库计数确认反复IO。较深Core／native帧部分未解析，不宣称完整native阻塞栈或CPU耗时。最终UI线程1063301没有目录读库链，实际读取在后台1063320，Source同时断言CheckAccess为false。前后媒体、包修订和库字节一致，阶段零全库／索引IO是硬断言。

首次修复后P99为767.6535ms，最终复测为1456.9587ms，两份原结果均保留；剩余UI排队延迟没有宣称解决。这里测的是带窗口Headless中输入优先级任务投递至执行，不是物理输入／绘制；不套用不同F6a保存基准的50ms检查，也不以某次较低结果设置交互预算。实际人工输入与其他UI热路径仍须后续验证，F6b完成边界为目录IO、失效和固定快照保护。

重复入口：

```bash
dotnet build MakoKaraGenerator.slnx --no-restore -m:1
dotnet test tests/MakoKaraGenerator.Application.Tests --no-build -m:1 --filter 'FullyQualifiedName~TemplateLibraryCatalogueTests|FullyQualifiedName~LibraryRepositoryTests|FullyQualifiedName~PresetApplicationTransactionsTests'
dotnet test tests/MakoKaraGenerator.Core.Tests --no-build -m:1 --filter 'FullyQualifiedName~StyleTemplateLibraryTests|FullyQualifiedName~TemplateRecipeStorageTests'
dotnet test tests/MakoKaraGenerator.Spike.App.Tests --no-build -m:1 --filter 'FullyQualifiedName~LibraryConcurrencyTests|FullyQualifiedName~LineWorkshopTests|FullyQualifiedName~PresetApplicationAppearanceTests|FullyQualifiedName~PresetApplicationSelectionTests|FullyQualifiedName~UserInterfacePurityTests'
bash tools/ui-suki-compatibility/layout-check.sh --catalogue
```

证据位于`artifacts/code-review-repair/f6b/`：基准／最终复测、实际线程记录、机器／固定素材hash、TRX、六项变异及恢复后回归。solution、Headless及原生验证工具构建零警告／错误；原生工具只做编译检查，本批未启动原生窗口、不操作用户0.43.10窗口。初始UI夹具未等待文本绑定稳定，不能把其误判的Project变化记为应用成功；改为实际应用数断言后通过。PATCH0.45.4，格式28／16／6、配置1、profile2、SDK、固定依赖及生产程序集保持。按用户要求未跑全量，不推定物理输入／IME、Windows或用户连续人工验收。F7～F8及最终路线继续，整个修复目标未完成。

## F7a · R09：一次校验及只读历史前缀（0.45.5，2026-10-08）

F6b已以`3a1c8d8`提交推送。每个重放代次建立带固定原因的一次校验事件表；历史对象只持共享表、可见数量和边界，同刻事件逐条纳入后再派发。内置查询不经过公开数组投影，旧作者快照保持不变。`Records`仍返回原`ImmutableArray<EventRecord>`，显式访问时复制可见前缀并缓存；公共构造严格校验仍在，受控路径为SDK内部friend类型，没有作者可信输入开关。回退、取消后重建、模式／目标变化及独立AnchorQuery保持，未改120Hz、精确事件或文字／粒子／视觉次序。

### 可复查证据

- 新增9项，Scripting历史／固定投影／共享语义／CursorTrail相关59项通过。包含未物化查询、固定表共享、旧History不增长、并发Records缓存、非法公共输入、Roslyn作者访问限制、同刻两字逐事件、取消后Seek及输出InputsChanged、24／30／60fps。既有未来／乱序／重复／来源、筛选与精确事件断言保持。
- Rendering82项加单独端点计量1项通过：真实Skia后继与过去／未来独立几何、不同FPS、模式／密度、跳转、取消／Error及主历史隔离；连续／桥接粒子出生、拖尾、预算和真实像素保持。
- App正式预览／运行验证／实际编码46项通过。包括双文字及真实50Hz音频、PreviewFrameCoordinator缓存／输入变化、桥接MP4／Alpha、24／30／60fps混合持久包每个编码输入帧与独立预览逐字节相同、完整解码全部43／54／108帧的顺序／像素及音轨、回退后全帧再比对和失败保护旧片。短片真实出口不替代暂停的57秒完整solution路线。
- 六项变异均先编译后执行，捕获实际断言失败：可见数量改为全表2项失败；前缀立即物化1项；内置二分读取Records1项；公共构造跳过校验1项；首个同刻事件使用完整同刻前缀3项；重建保留旧原因4项。每次恢复完整原字节并记录SHA-256，恢复后再构建／运行历史回归，没有把编译错误算作检出。最终0.45.5再通过同组历史59项及正式桥接／MP4／Alpha6项；较大App46及Rendering82为同一生产逻辑在仅统一版本升级前的定向结果。

### 固定基准和未解决成本

机器AMD Ryzen AI 9 H 365／20线程、CachyOS Linux7.2.8-2、.NET10.0.12。前测`3a1c8d8`／0.45.4，后测本批F7a实现，测量时统一版本仍0.45.4。四模式×10／30／180秒×三个新进程，前后各36个样本；每次同模式2秒预热排除，固定30fps从0至终点，计入会话创建／推进／完整诊断消费／释放，编译和固定音频夹具排除。无同时运行的构建或其他测试。实际每次JSON、PID、独立插桩源码和计数、原字节恢复、采样方法及完整12格中位数见[历史前缀合同](../../scripting-history-prefix-contract.md#3-固定基准与实际结果)。

| 180秒中位数 | 累计分配字节，前→后 | 推进ms，前→后 | F7a后实际计数 |
| --- | ---: | ---: | --- |
| 空效果 | 493,635,648 → 492,596,320 | 569.4731 → 569.1236 | 事件0／宿主复制0／兼容复制0 |
| 50Hz无Records | 11,377,934,376 → 673,289,400 | 15510.1978 → 754.8942 | 校验9000／宿主复制0／兼容复制0 |
| 每Evaluate一条Warning | 6,101,564,864 → 6,100,486,792 | 5956.6182 → 5892.2670 | 完整诊断21601，消费者访问58,336,201条 |
| 50Hz且每Evaluate读Records | 11,377,906,256 → 1,711,375,200 | 15847.6100 → 815.0663 | 校验9000／宿主复制0／兼容复制129,623,400条 |

旧50Hz完整校验／宿主复制为10／30／180秒526,550／4,729,650／170,127,900条，新路径每记录只校验一次500／1500／9000条；每个后测样本断言精确计数及非数组作者零兼容读取／复制。空作者仍有其他语义对象分配。显式数组作者仍按不同前缀物化，Warning仍保留累积完整诊断和重复消费，不能把这些路径虚报为线性或R10已解决；F7b将从该Warning基线接续。

峰值仅`GC.GetTotalMemory(false)`每2ms的最大观测占用，不保证绝对峰值，不是native或RSS。计数器仅在隔离测量构建，成功校验一次累计数量、兼容投影记录实际复制，无每记录原子操作；最终生产源无插桩。普通基准CLI可复测分配／时间，私有计数字段null，不伪造为0。

独立端点另测：真实两行／四字、1.6秒Skia夹具，一次完整预热排除，三次顺序样本；过去399999μs／未来500000μs各创建一个后端，中位5,315,272／5,772,064字节、13.6196／14.3709ms。主会话预先441666μs，首次桥接450000μs新边界创建两个后端，中位10,962,096字节／28.5664ms；后续458333μs新边界命中0个后端、67,568字节／0.2512ms。含边界推进的缓存观测不是同帧准备对象直接复用，也不把命中成本归给独立端点重放；没有新增检查点或跨代次缓存。

重复入口（先当前成功构建，再按需选择）：

```bash
dotnet build MakoKaraGenerator.slnx --no-restore -m:1
dotnet test tests/MakoKaraGenerator.Scripting.Tests --no-build -m:1 --filter 'FullyQualifiedName~HistoryPrefix|FullyQualifiedName~TimelineHistory|FullyQualifiedName~TimelineProjection|FullyQualifiedName~SemanticSession|FullyQualifiedName~CursorTransitionProjection|FullyQualifiedName~CursorTrail|FullyQualifiedName~Bridge'
dotnet test tests/MakoKaraGenerator.Rendering.Tests --no-build -m:1 --filter 'FullyQualifiedName~CursorSuccessorSessionTests|FullyQualifiedName~ContinuousParticleTests|FullyQualifiedName~VisualStageTests'
dotnet test tests/MakoKaraGenerator.Spike.App.Tests --no-build -m:1 --filter 'FullyQualifiedName~完整桥接包持久重开|FullyQualifiedName~持久真实桥接经MP4|FullyQualifiedName~真实桥接飞行中|FullyQualifiedName~持久双文字音频在同画布|FullyQualifiedName~R6混合持久包每个编码输入帧|FullyQualifiedName~PreviewFrameCoordinatorTests|FullyQualifiedName~ScriptRuntimeValidationTests'
```

日志、TRX、72个样本、独立前后测量源码／二进制、恢复SHA及六项变异保存在`artifacts/code-review-repair/f7a`。最终solution构建零警告／错误，按用户要求未跑全量，无UI改动或窗口启动，不操作用户0.43.10窗口。PATCH0.45.5，公开作者／宿主签名、格式28／16／6、配置1、profile2、生产依赖及程序集保持；不推定Windows实机、物理输入或用户连续人工验收。下一项F7b持久诊断与增量消费，再F8及最终路线；整个目标仍未完成。

## F7b：完整诊断快照、发生身份与正式增量消费（0.46.0）

F7a已以`80f3831`提交推送。F7b按ADR-0176／[诊断快照合同](../../scripting-diagnostic-snapshot-contract.md)新增Rendering.Abstractions的串行追加Buffer、共享不可变分块Snapshot及增量Cursor。加入时校验记录及Error标记，未满尾部最多31条复制、完整块32条，旧块共享；同文重复实际调用逐条保留，没有截断或Warning预算。已发布对象不持可变尾部、作者实例、异常或native表面；原完整Diagnostics／SceneDiagnostics数组签名保持，明确读取才物化并缓存。

共享120Hz／精确事件日志与分数求值／绘制后缀分别固定；重复边界／像素缓存／失败缓存重用发生身份，分数时刻重新实际求值仍有新发生，未来边界不含旧局部报告。首次远时刻取得完整日志，回退、取消中途后的实际重建、模式／目标及新会话有新代次，旧快照稳定。正式Scripting、Skia、缓存、预览、导出与运行验证使用新路径；图绑定及组透明度克隆也不访问数组。公共独立端点结果仍完整，内置bridge仅取固定Cursor；未扩大内部出生几何临时Warning的传播范围。

Error被作者捕获仍在原阶段失败，成功准备帧检查Error标记，不扫描历史。失败带当时完整日志及原包／修订／实例／阶段／整数时刻／可选源码位置；普通加载、输入／required音频、粒子／bridge及释放异常也显式传入日志，第二实例早期失败保留第一实例初始化报告。普通异常不伪造作者位置，取消不写假Error。预览失败缓存共享完整失败快照，旧失败入口缺少Error值时只追加具有原来源的错误报告；验证失败保留报告且仅一条错误汇总。

导出返回完整`DiagnosticOccurrences`，Warnings按原实例／artifact／code／message规则去重；运行验证的完整Diagnostics保留输出模式，Issues继续原汇总规则。效果预览发生列表和验证汇总均每页最多50项，完整数据可读取；上一页／下一页、重跑和输入／DataContext切换不删结果或污染新输入。静态配置保存、源码编译和正式验证仍独立，没有新增持久数据。

### 当前代码的回归与真实出口

新增32项正式回归，恢复变异后的0.46.0相关Scripting63／Rendering144／App69／Export58全部通过，零失败／跳过。当前solution构建零警告／错误；编译后的测试才运行，不使用失败构建后的旧产物。

- [快照回归](../../../tests/MakoKaraGenerator.Scripting.Tests/RuntimeDiagnosticSnapshotTests.cs)5项：10000条逐次发布／消费、旧前缀、相同文本完整保留、分数与绘制后缀、并发兼容数组、首次远快照、空／非法数组及两个成功帧入口的Error门禁，反射检查正常路径未物化数组。
- [正式语义回归](../../../tests/MakoKaraGenerator.Scripting.Tests/DiagnosticSnapshotSessionTests.cs)16项：0／1／2／5秒仍1／121／241／601条；全部七阶段、单帧重复报告、分数重求值、同一边界、回退／模式及实际中途取消重建、会话释放及释放失败、Error被捕获和早期双实例加载／音频门禁日志。实际来源包含实例、artifact、阶段、时间及源码位置；正式持久包来源另外由App原回归覆盖。
- [消费者模型](../../../tests/MakoKaraGenerator.Spike.App.Tests/ViewModels/DiagnosticConsumerTests.cs)2项及[运行验证](../../../tests/MakoKaraGenerator.Spike.App.Tests/ViewModels/ScriptRuntimeValidationTests.cs)新增2项：重复发布只消费一次、50／50／21页保留121项、换模式／新结果、双输出1440次真实同文发生分别保留而提示仅两项、完整失败日志及唯一错误汇总。720个假像素帧都未物化数组；其真实固定包／双输出720帧／错误、透明回退及关闭取消原回归也通过。
- [真实缓存及导出](../../../tests/MakoKaraGenerator.Spike.App.Tests/Rendering/ProjectRenderSessionTests.Diagnostics.Snapshots.cs)新增7项：Skia缓存持同一快照，释放原会话和像素后仍可完整读取，重复缓存零新增。24／30／60fps的普通MP4／透明ProRes4444按现有总帧数四舍五入规则各7／9／18帧，共68个真实编码输入帧与独立正式会话逐字节相等，每帧完整诊断相等且兼容数组未物化；实际成片再完整解码全部帧，顺序／像素／alpha容差及流终点通过。Info／Warning与无效果基线像素相同，报告与独立游标内容相等、每个发生身份唯一，Warning汇总保持；成功完成／释放和所有帧释放受检。六项增强出口在当前最终测试构建另通过，证据为`all-encoded-frames`及`all-encoded-frames-count-fixed`日志／TRX；测试曾误按上取整要求24fps为8帧，原有生产规则始终四舍五入7.2为7，纠正夹具后的明确计数和完整解码通过，原失败保留。此前F7a完整混合包音轨证据保持，本批有限诊断短片不含音轨，不替代57秒最终连续路线。
- 原持久包七阶段Error／普通异常、多包修订／导出／共享DLL同文来源、required音频、聚合释放、主Render材质／组图门禁及200ms编码失败旧输出保护通过。Rendering相关准备帧／组／材质／图元／Cursor与缓存144项、Export错误和事务边界58项继续通过。
- `tools/ui-suki-compatibility/layout-check.sh --diagnostics`的当前生产运行验证控件Headless11项：实际运行按钮、绑定50／50／21行、前后页动作／启用、末条结果、再次运行及DataContext切换，且不保存配置。隔离runner为`artifacts/ui-headless-layout.Rj2Re8`，最终复制当前受版本管理源码，构建零警告／错误；只使用Avalonia内存平台，未连接原生桌面，不操作用户窗口。

10项变异全部成功编译后触发真实失败：丢共享前缀、暴露可变尾部、重复消费旧前缀、按消息去重、生产提前物化数组、缓存丢快照、移除作者捕获Error门禁、验证／导出每帧重新读取旧记录、required音频错误漏前一实例日志。原始／变异／恢复SHA及实际断言保留；最后一项在早期异常日志补充后执行，其余已验证结构保持。全部源码原字节恢复后重新构建并跑上述当前定向回归。

### 实际成本与界限

前测独立检出`80f3831`，后测F7b主体；计量统一0.45.5，随后升MINOR0.46.0并补早期异常上下文，正常基准不执行该分支。前后四模式×10／30／180秒×3新进程各36样本，另9个明确完整数组Warning样本共81；每个同模式2秒预热排除，固定30fps含0和终点。创建／推进／选择的诊断消费／释放计入，编译和固定50Hz夹具排除；测量未同时运行构建或测试。每个样本核对实际生产复制／校验、兼容数组复制、ReadRun及foreach访问；插桩仅隔离二进制，生产源原字节恢复。

| Warning时长 | 分配字节，前→正式增量后 | 复制／校验，前 | 后尾部复制／校验 | 消费访问，前→后 |
|---|---:|---:|---:|---:|
| 10秒 | 45,060,984 → 28,125,120 | 各2,165,403 | 19,689／1,201 | 180,901 → 1,201 |
| 30秒 | 238,648,856 → 84,146,640 | 各19,456,203 | 59,289／3,601 | 1,622,701 → 3,601 |
| 180秒 | 6,100,579,896 → 504,299,176 | 各699,937,203 | 356,401／21,601 | 58,336,201 → 21,601 |

180秒推进中位数7241.3954→599.2017ms，所见托管占用43,465,296→25,283,496字节。正式完整数组复制0，实际记录21,601全部保持。明确完整数组后测仍58,336,201条复制／访问，分配1,015,810,176字节、推进3123.8993ms；显式Records历史复制仍129,623,400条、分配约1712 MB，不能记为全路径线性。空作者／50Hz音频分配仍约494／674 MB，其他语义对象未消除；时间变化不统一归因于诊断，不设通用耗时预算。GC累计分配precise计量，自有采样线程每2ms读托管占用并停止／Join，所见最大值不等同绝对峰值、native内存或进程RSS；F6b剩余UI排队延迟不由此宣称解决。完整12格中位数、方法、公式与每次机制断言见[诊断快照合同](../../scripting-diagnostic-snapshot-contract.md)。

日志、TRX、81个样本、插桩源／恢复和二进制SHA及10项变异位于`artifacts/code-review-repair/f7b/`，不提交缓存／媒体。MINOR0.46.0对应新增公共宿主Snapshot／Buffer／Cursor与完整导出记录，作者报告方法及旧数组签名、工程／歌词／模板28／16／6、配置1、profile2、固定依赖及生产程序集保持，无迁移。按用户要求未跑全量，未启动或操作用户0.43.10窗口，不推定Windows、原生物理输入或用户完整连续制作人工验收。F8a独立运行宿主、F8b应用用例、F8c旧入口与编译绑定及最终路线继续，整个目标未完成。

## F8a · A01：正式运行宿主抽离GUI（0.47.0，2026-10-08）

F7b已以`c6fcf62`提交推送。本批新增`MakoKaraGenerator.Runtime.Hosting`，迁入正式快照工厂、像素会话及固定来源编译异常；共享源码位置记录不再属于App。工厂显式接收参考／产物目录、视频提供者及ReplayReason，App组合根只创建工厂，现有预览、导出和运行验证继续通过中立接口消费。Export不引用新宿主，其他后端不反向引用宿主或GUI。静态路径、资源装配、每次运行独立状态、渲染／关闭串行和全部释放顺序保持。合同见[正式宿主](../../runtime-hosting-contract.md)／ADR-0177。

### 可复查证据

- [无GUI宿主测试](../../../tests/MakoKaraGenerator.Runtime.Hosting.Tests/ProjectHostingTests.cs)及[依赖检查](../../../tests/MakoKaraGenerator.Runtime.Hosting.Tests/BackendDependencyTests.cs)新增9项，当前全部通过，零失败／跳过。测试项目只引用宿主，实际离线编译固定C#包和真实shader／图，普通／透明各六次帧请求含回退重放；完整像素及来源诊断保持。关闭一个会话后另一个仍可渲染，不共享可变native资源。静态快照身份、失败源码固定包／实例／行列、预取消、两个进行中取消入口、关闭等待与幂等，以及真实图片包装／native集合／预算回收和全部清理错误均受检。
- 依赖检查覆盖所有正式后端的完整项目图和宿主的传递程序集引用，拒绝GUI包／可执行项目，以及后端反向引用宿主。独立宿主测试进程未加载Avalonia或App；两个指定测试friend仅用于故障注入，不是产品程序集引用。
- 当前App63项通过：PreviewFrameCoordinator19、EffectPackageCompiler6、正式会话21、源码持久化1、独立运行验证14、工坊作者会话2。保留普通／透明各360帧的正式验证、取消等待和来源定位；24／30／60fps普通MP4／透明ProRes4444六个0.3秒短片，全部68个编码输入帧和完整实际解码继续通过。这里沿用现有最近整数总帧规则，不更改视频时序。
- 额外原执行体对照：从`c6fcf62`取原工厂／会话完整源码，只隔离命名空间，在同一0.47.0后端／SDK下编译；普通／透明共12次相同快照、时间和目标的请求，完整像素逐字节、运行诊断各字段、图／材质诊断与新宿主相等。工厂／会话规范化还原命名空间、工厂可见性及新增文档后，原源码SHA与当前源码完全一致。该对照验证迁移执行体，不冒充旧版本SDK或之前的全量结果。
- 四项成功编译的变异分别把快照引用身份改成值相等、漏释放native集合、漏释放准备源，以及让宿主引用并实际使用Avalonia类型，均触发真实断言失败。最初直接删除快照检查因主构造参数未读而未编译，不计为变异通过；有效替代改变比较语义后成功编译并失败。源码／项目原字节恢复，GUI探针移除、依赖重新恢复后，当前solution零警告／错误构建，正常宿主9项复查通过。

日志、TRX、原执行体对照、四项变异及源码恢复证据位于`artifacts/code-review-repair/f8a/`。MINOR0.47.0对应新增公开正式宿主程序集及工厂／结构化来源入口；作者SDK和中立接口签名、工程／歌词／模板28／16／6、配置1、profile2及固定NuGet版本保持，无格式迁移。

按用户要求未跑全量，没有启动或操作用户0.43.10窗口，不推定Windows、原生物理输入或用户连续人工验收。F8b仍需抽离验证执行器／试样作者服务和布局用例；F8c共享作者模型、旧入口及编译绑定尚未完成，最终完整连续路线另行验收。整个修复目标保持未完成。

## F8b · A01：固定验证、作者试样与布局用例（0.48.0，2026-10-08）

F8a已以`1adf71f`提交推送。按ADR-0178／[验证与布局合同](../../runtime-validation-and-layout-contract.md)，固定快照／帧计划双输出执行器和结构化报告位于Runtime.Hosting；作者试样、真实媒体和目录租约位于Authoring模块。App保留原请求／VM结果，只映射标签和格式化来源。布局变换、四种几何及片段引用映射迁入Application，生产仍只引用Core；显式输入新节点名称并返回结构化错误，App沿原Loc键提供名称、失败文案及确认。固定试样、预设投影、精确选区和一次撤销保持。

### 当前回归与实际出口

- 新增16项正式回归，恢复变异后当前宿主16／Application布局21／App56项通过，零失败／跳过。原App布局17项迁至Application，全部几何、未选成员真实像素、调度时钟、直接绑定、固定相对实例／seed、不能拆分范围配方、闭包保存重开及一次撤销断言保留；App另保留实际EditorViewModel的过期／无变化／名称映射及历史入口。
- [无GUI验证测试](../../../tests/MakoKaraGenerator.Runtime.Hosting.Tests/RuntimeValidationTests.cs)真实编译固定C#包并渲染两种输出的原生图，完整编译失败保留包／修订／路径／行列。纯执行器验证320×180／24fps明确三帧计划，时刻0／41666／83333μs；12次同文报告完整保留，Issues仅各模式一条。作者服务各360帧、固定seed不写配置、帧与会话全部释放且会话释放先于目录删除受检。普通／透明进行中取消等待忽略取消的后端返回，再释放；媒体并发关闭共享一个等待任务，生成未结束不清理，关闭后拒绝加载，仅删除自己的目录，邻居保留。
- App原运行验证14项继续真实普通／透明各360帧，涵盖晚时刻作者Error、编译来源、取消／关闭、材料回退及完整实际发生。相关工坊作者、材质、预演和对象选择通过；24／30／60fps普通MP4／透明ProRes4444六短片全部68编码输入帧与独立正式会话逐字节相等，完整实际解码保持原顺序、像素、alpha容差及端点。有限短片不替代暂停的完整57秒最终路线。
- Headless通用界面332个断言通过，包含首页独立工坊、模板试样、材料／脚本控件、任务切换和宽窄布局；新增[BasicLayoutFlow](../../../tools/ui-suki-compatibility/BasicLayoutFlow.cs)29个断言使用实际四种预设控件及影响对话框，确认／取消、精确拆分、保留原ID、未选成员和一次撤销通过。原诊断分页11个断言验证实际运行按钮、50／50／21记录、前后页启用、重跑及DataContext切换。均为有限Avalonia内存平台流程，无原生窗口或桌面交互。
- 四项成功编译的变异分别删去最后一帧、按同文去重实际发生、删除未选调度上下文、未等待媒体生成即清理，都触发真实断言失败；不是编译失败检出。每次恢复完整源码字节，SHA及实际失败保留；恢复后solution构建零警告／错误，再通过同组93项定向。

证据位于`artifacts/code-review-repair/f8b/`。首轮Headless构建因工作盘空间耗尽失败，未执行交互；仅删除本批`ui-headless-layout.WvC7pC/build`未完成输出，保留原日志和源码并移至`/tmp/makokara-f8b-headless-WvC7pC`。随后尝试禁止构建依赖时缺少临时ref程序集，亦未运行旧产物；改为临时盘完整构建引用项目，零警告／错误后完成上述有限检查。最终日志与源码清单另复制到本批证据目录，既有证据／用户文件未清理。最后去掉仅供旧探针反射的私有目录属性，探针改为读取实际媒体租约；当前solution再次构建零警告／错误，工坊预览／布局入口15项复查通过（属于上述56项，不重复计为新增）。原生验证工具在`/tmp/makokara-f8b-native-build`编译零警告／错误，只做编译检查、不启动它。

重复入口（先当前成功构建，再选择定向；磁盘足够时可直接使用Headless脚本）：

```bash
dotnet build MakoKaraGenerator.slnx --no-restore -m:1
dotnet test tests/MakoKaraGenerator.Runtime.Hosting.Tests --no-build -m:1
dotnet test tests/MakoKaraGenerator.Application.Tests --no-build -m:1 --filter 'FullyQualifiedName~BasicLayoutTests'
dotnet test tests/MakoKaraGenerator.Spike.App.Tests --no-build -m:1 --filter 'FullyQualifiedName~ScriptRuntimeValidationTests|FullyQualifiedName~TemplateAuthorFlowTests|FullyQualifiedName~TemplateAuthorSessionTests|FullyQualifiedName~TemplateWorkshopPreviewTests|FullyQualifiedName~TemplateMaterialEditorTests|FullyQualifiedName~AppearanceWorkspaceTests|FullyQualifiedName~BasicLayoutEntryTests|FullyQualifiedName~PresetApplicationSelectionTests|FullyQualifiedName~正式双输出导出仅访问实际发生'
bash tools/ui-suki-compatibility/layout-check.sh --layout
bash tools/ui-suki-compatibility/layout-check.sh --diagnostics
```

MINOR0.48.0新增公开执行器／作者服务／Application布局用例及枚举迁移，作者SDK／中立渲染接口、工程／歌词／模板28／16／6、配置1、profile2及固定依赖保持，无格式迁移。按用户要求未跑全量，不操作或新开用户0.43.10窗口，不推定Windows、物理输入或用户连续人工验收。F6b剩余UI排队延迟仍保留原边界；下一项F8c共享作者模型／旧入口／编译绑定，再最终连续与人工验收，整个目标未完成。

## F8c1 · A04：作者模块、旧入口与首批类型绑定（0.49.0，2026-10-09）

F8b已以`e3e448c`提交推送。按ADR-0179／[作者模块与绑定合同](../legacy-ui/authoring-module-and-bindings-contract.md)，分别检索生产C#、AXAML、资源、工具和历史测试；旧EffectWorkspaceWindow与ProjectBaselineStyleWindow没有当前导航消费者，独立脚本配置的当前模型不引用旧实例／源码模型。本批删除旧窗口、专属模型、无订阅请求和直接静态编辑命令；保留正式ProjectEffects事务与所有底层闭包／渲染能力。当前共享样式字段、接口、原始输入／三个编解码器和EffectTargetCatalog迁入App.Authoring，LivePreview／SupportsProperty代替具体类型判断。合成区已有的画面任务导航明确改名。

历史LegacyAuthoring夹具只在测试程序集，保留真实包、实例、来源、保存重开、像素和历史输入；R7模型由明确目标／时间构造，不再虚构产品事件入口。部分旧静态命令测试通过夹具反射执行真实Editor历史方法；产品没有增加兼容命令，当前预设应用另有生产入口验证。历史夹具不是当前作者UI端到端验收证据。

### 实际证据

- App定向133项全部通过，覆盖独立脚本配置／图形参数／真实运行验证、工坊材料与预览、作者输入、预设应用及历史领域回归。后端定向19项全部通过，保留真实文字／源材质普通与透明像素、源码接受／Error保存重开、撤销重做、R7模板和容器真实组出口。两组不重叠，共152项，不把历史模型的UI能力记为当前生产能力。当前普通／透明各360帧的独立脚本运行验证仍包含在相关定向中。
- [AuthoringBindingsFlow](../../../tools/ui-suki-compatibility/AuthoringBindingsFlow.cs)15个有限Headless断言实际创建StylePropertyRowView：切换工坊FontSize／Composite位置字段、标签／范围提示和禁用状态、非法原文／错误与CanSave、55字号实时合法预览、场景Opacity显式一次提交、不串写旧作者、null清除与重新激活。自定义IStyleDraftContext也通过实时写入，证明没有依赖具体工坊类型。首批四视图明确x:DataType／局部x:CompileBindings；复合控件主要C#接线，不虚报AXAML绑定数量。
- Headless草稿129、通用布局313、任务命令93、诊断分页11全部通过，加作者15共561个断言。通用原332中的19个旧窗口检查已随无入口窗口删除，当前工坊及独立脚本仍受检。均为Avalonia内存平台有限流程，无桌面连接。原生工具针对当前来源编译零警告／错误，未执行或新开窗口。
- 三项变异分别恢复产品旧窗口类型、放开Composite几何属性、关闭工坊LivePreview，每次成功构建且零警告，然后触发实际架构／控件断言失败。另将Label故意改为MissingF8cLabel，AVLN2000编译错误证明属性行确实走编译绑定；这是编译门禁，不记为成功编译后的运行变异。源文件按完整字节／SHA恢复，临时类型删除，最终solution构建零警告／错误，再复查同组定向和作者／草稿Headless。
- 首轮真实后端19项中4项旧诊断断言失败。取e3e448c旧模型及源码编辑执行体，仅隔离测试命名空间和补当前共享类型引用，在同0.49.0后端／SDK下运行原两个测试方法的6个参数用例：同样2通过／4失败。两项运行Error在完整列表但不在第一个50项页；另两项失败报告含8条实际发生，旧断言只允许一条。临时原执行体存档后从编译路径删除。当前断言逐项核对完整报告、Initialize／OnEvent／Evaluate保留、唯一Error严重性及包／修订／来源，并按实际Error序号翻页定位源码。未改变生产报告或分页语义，保留原失效帧、保存、恢复和释放断言。此对照不等于旧SDK完整测试。

引用前行号、退出源码SHA、原执行体SHA／源码、首轮失败及修正后TRX、三项变异／编译门禁、Headless和最终构建位于`artifacts/code-review-repair/f8c1/`。沿用本目标临时盘Headless输出以避免重复大产物，未清理既有证据或用户文件。最初两次构建分别因迁入文件缺using／分析器形参及R7遗留局部变量失败，未在失败后运行旧产物；修复后才执行上述检查。最后草稿复查首次漏设验证工具的MAKO_SUKI_SOURCE_ROOT，读取示例包前即失败；原日志保留，补齐仓库根环境并使用新隔离配置后129项通过，不记作产品失败。

重复入口（先成功构建，再选择定向；Headless脚本需要足够磁盘）：

```bash
dotnet build MakoKaraGenerator.slnx --no-restore -m:1
dotnet test tests/MakoKaraGenerator.Spike.App.Tests --no-build -m:1 --filter 'FullyQualifiedName~EffectWorkspaceTests|FullyQualifiedName~EffectWorkspacePreviewTests|FullyQualifiedName~EffectWorkspaceDirectGraphTests|FullyQualifiedName~EffectTargetCatalogTests|FullyQualifiedName~DiagnosticConsumerTests|FullyQualifiedName~LineWorkshopTests|FullyQualifiedName~AppearanceWorkspaceTests|FullyQualifiedName~TemplateWorkshopInputDraftTests|FullyQualifiedName~TemplateMaterialEditorTests|FullyQualifiedName~TemplateAuthorFlowTests|FullyQualifiedName~ScriptConfigurationWorkspaceTests|FullyQualifiedName~ScriptRuntimeValidationTests|FullyQualifiedName~ScriptParameter|FullyQualifiedName~TemplateWorkshopPreviewTests'
dotnet test tests/MakoKaraGenerator.Spike.App.Tests --no-build -m:1 --filter 'FullyQualifiedName~真实上下文文字效果|FullyQualifiedName~纯源材质上下文|FullyQualifiedName~上下文作者失败|FullyQualifiedName~工作区混合真实包|FullyQualifiedName~失败源码可接受|FullyQualifiedName~R7工坊配方|FullyQualifiedName~真实容器相关'
bash tools/ui-suki-compatibility/layout-check.sh --authoring
bash tools/ui-suki-compatibility/layout-check.sh --draft
```

MINOR0.49.0对应退出旧公开UI类型／命令及共享作者类型位置变化；作者SDK、中立接口、工程／歌词／模板28／16／6、配置1、profile2及固定依赖保持，不迁移旧格式。全量测试按用户要求暂停，不操作或新开用户0.43.10窗口，不推定Windows、物理输入或人工连续制作通过。F8c2剩余稳定视图／动态例外和全局默认尚未完成，整个F8c及最终连续／人工路线继续；F6b剩余UI延迟保留未解决边界。

## F8c2 · A04：剩余稳定视图、动态数据绑定及最终默认（0.49.1，2026-10-09）

**实现及原始失败：** 当前33个生产AXAML／29视图共317处数据Binding、17个DataTemplate有准确模型或项目类型，12处C#数据绑定使用CompiledBinding.Create；11个命令动作初始化使用独立空范围。泛型选项只共享内部只读Label，Value仍为实际类型；子上下文和父控件读取明确。所有稳定视图交互通过后删除App全局false，`compiled-default.txt`记录固定12.1.3当前生产MSBuild解析为true。当前未登记生产反射例外，资源及无模型绑定控件不虚报迁移数量。

`branch-observation-bindings.log`实际捕获命令初始化把EditorViewModel转换为TaskCommandAction，以及未选行SelectedLine.Text空路径；现使用明确命令DataContext与CurrentLineText。`visible-material-bindings.log`记录模型为已唱填充而下拉仍为未唱填充，修复只替换内容变化的MaterialSlots／Targets，三槽逐次检查界面／模型／运行请求一致。`full-typed-before-default-bindings.log`捕获只读范围ConvertBack及Suki7.0.1 BiggestItemListBoxConverter读取包材质纯色的警告；配方Run.Text明确OneWay，StyleTemplate.ToString只返回Name，不遍历包闭包／材质类型限定访问器，原纯色门禁、相等性和持久值保持，不修改上游主题。

**定向及实际界面：** App135（含两项新增策略）／Core模板57项全部通过，零失败／跳过。零警告solution构建后执行，源码恢复后再次构建并复查同组，证据为`solution-build-restored.log`、`app-restored.trx`及`core-restored.trx`。Headless八个有限流程共665个断言，最终明细为`final-current-results.json`（绑定以`sample-source-results.json`53项取代此前49项，其余七流程沿用相同未改动行为的基线）：

| 流程 | 实际断言 | 本批覆盖 |
| --- | ---: | --- |
| 编译绑定 | 53 | 最近工程替换／清空／打开、三种新建基线、行／逐字／分支模板、播放上下文切换、包／泛型选项、非法参数实际门禁、报告、真实Roslyn诊断、工坊材质父编辑／错误、配方、个人库；Binding Warning以上为零 |
| 通用界面 | 313 | 当前生产窗口／工作区、布局、深浅主题及宽度，不含已退出旧窗口 |
| 语义 | 22 | 来源、多行多选、窄窗入口、实际预设应用及一次撤销 |
| 共享作者 | 15 | 字段上下文替换／清空、非法输入、实际作者实时写入与显式场景提交 |
| 草稿 | 129 | 隐藏错误、全部相关复合控件、保存门禁、历史与手势 |
| 诊断分页 | 11 | 实际报告完整来源和分页 |
| 任务命令 | 93 | 菜单／按钮／键盘同目标，配置／源码保存独立，离开及文本撤销 |
| 基础布局 | 29 | 实际可视布局选择、影响确认与撤销 |

新增`CompiledBindingsFlow`从开始到结束收集真实Avalonia Binding日志，只有零警告才通过；报告消费的结果替身明确只验证UI显示／请求，不冒充普通／透明各360帧执行，正式执行器仍由App相关回归验证。临时配置置于探针源码目录之外；夹具／泛型标注／缺失using及配置源码误入编译glob等失败保留，不计为通过，`slots-fixed`旧输出记录作废。最终基线及恢复后的当前构建才作为新代码证据。

**变异：** 六项均成功编译后触发预期失败，不用超时或编译错误代替运行断言；`mutations.json`及`policy-and-compile-gate.json`记录结果。

| 变异 | 实际失败 |
| --- | --- |
| 去掉命令初始空DataContext | Binding警告捕获错误EditorViewModel→TaskCommandAction |
| 恢复每次重建材质选项 | 已唱槽的实际下拉选择／显示与模型不一致 |
| 恢复模板自动字符串 | 个人库包材质触发BiggestItemListBoxConverter纯色访问警告 |
| 枚举展示Label改Value | 包参数下拉未显示作者提供的标签 |
| 标量父IsScalar改IsColor | 实际数值子控件不可见 |
| 恢复项目全局false | 已编译策略回归失败，另一个模板策略保持通过 |

另将PlayheadLabel拼为不存在属性，AVLN2000在编译阶段准确指向PlaybackBarView，不计入六项编译后变异。每次临时修改在finally按原字节恢复并核对；`final-source-sha.json`记录最终源码。恢复后solution、Headless及原生工具编译零警告／错误；先复查绑定49／命令93及App135／Core57。提交前将三个C#试样绑定集中到生产私有接线方法，以静音对象通过同一方法实际检查初值、重启时间／标签及Loop双向更新；当前绑定53项零警告，solution-final／Headless再次构建通过，相关12项复查已包含在135内，不叠加重复计数。原生工具未执行，不连接用户桌面。

可复查入口：

```bash
dotnet build MakoKaraGenerator.slnx --no-restore -m:1
dotnet test tests/MakoKaraGenerator.Spike.App.Tests --no-build -m:1 --filter 'FullyQualifiedName~CompiledBindingPolicyTests|FullyQualifiedName~ScriptConfigurationWorkspaceTests|FullyQualifiedName~ScriptRuntimeValidationTests|FullyQualifiedName~ScriptParameter|FullyQualifiedName~TemplateWorkshopInputDraftTests|FullyQualifiedName~TemplateMaterialEditorTests|FullyQualifiedName~TemplateAuthorFlowTests'
dotnet test tests/MakoKaraGenerator.Core.Tests --no-build -m:1 --filter 'FullyQualifiedName~StyleTemplateApplicatorTests|FullyQualifiedName~StyleTemplateLibraryTests|FullyQualifiedName~TemplateRecipeStorageTests|FullyQualifiedName~TemplateEffectRecipeTests|FullyQualifiedName~CompositePresetDefinitionTests|FullyQualifiedName~StrokeTemplateTests'
bash tools/ui-suki-compatibility/layout-check.sh --bindings
bash tools/ui-suki-compatibility/layout-check.sh --task-commands
```

第一条App复查入口为主要受影响子集；本批135项的完整filter及TRX保存在上述证据目录，不把子集命令称为135项。PATCH0.49.1保持工程／歌词／模板28／16／6、配置1、profile2、作者SDK／中立接口、程序集与固定依赖。按用户要求未跑全量，不启动／操作用户0.43.10窗口；不推定Windows、物理输入／IME、人工试听及完整连续制作通过。F8c定向范围收口，最终连续／人工路线及F6b剩余UI延迟继续，目标未完成。

## F6c · R13性能跟进：整形回退重复查找（0.49.2，2026-10-09）

F8c2已以`dea76c9`提交推送。按ADR-0181复测原F6b媒体、三个固定包和100个真实包材质模板，普通编辑仍无目录IO；当前主线程栈确认`RefreshShapingDiagnostics → Shape → ResolveFontEntry`反复匹配并重开同一回退族。修复只在SkiaTextLayoutEngine：主字体／显式链继续先检查，自动回退成功与缺字结果按完整元素缓存，1024项／64个UTF-16代码单元硬边界，FIFO淘汰；已持有族复用，字体选项、引擎／线程和拥有关系隔离。整形输出、缺字诊断及绘制保持，不更改预览质量、工程历史或作者数据。

### 固定环境与全部测量结果

AMD Ryzen AI 9 H 365／20线程，CachyOS，SDK10.0.112／运行时10.0.12；固定Suki7.0.1／Avalonia12.1.3。300秒WAV与60秒640×360／30fps H264共63,402,309字节，100模板库101,980字节；GlassInk、WordEmphasis、CursorTrail修订与F6b相同。媒体与DejaVu Sans、Noto Sans CJK SC、Noto Color Emoji文件SHA及当前FFmpeg／kernel保存在`artifacts/code-review-repair/ui-responsiveness/environment.json`。媒体SHA仍为WAV `be33313a11514bd2901858d99c3d91baeb46d929f6610fdad60e6df96b72a23c`、MP4 `ce1c424e72400bb9b9aee223d4b880d9bfc13f133e59404c16e34f5f4a34ab64`。

沿用`--catalogue-benchmark`实际MainWindow／歌词选择／文本提交路径，隐藏／可见各20次；producer每5ms投递Input优先级任务。五次全部保留，不挑较小结果：

| 执行体 | 样本 | P50 ms | P95 ms | P99 ms | 最大 ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| 当前修复前0.49.1 | 335 | 283.4590 | 660.6320 | 716.1953 | 731.5285 |
| 修复后0.49.2 | 270 | 291.0444 | 812.0592 | 867.6291 | 881.9219 |
| 修复后重复 | 311 | 375.6322 | 920.8064 | 984.0088 | 1003.4850 |
| 精确原整形执行体，同0.49.2宿主 | 531 | 531.7212 | 1314.6912 | 1430.1211 | 1455.6601 |
| 原执行体重复 | 340 | 294.5591 | 672.1694 | 718.0215 | 733.2747 |

全部首次目录完整读取1次／索引2次，目录来源的UI读取0；隐藏／可见普通编辑的完整及索引读取增量均0。强化基准门禁同时断言完整与索引增量，原序列及样本投递保持。

采样工具使用本机已安装dotnet-trace的`collect --profile dotnet-sampled-thread-time --format Speedscope`。精确选取各summary所列PID的UI线程，每个时间区间对匹配调用链只累计一次。`ResolveFontEntry`修复前累计796.023ms，同宿主原执行体1249.210／806.268ms，修复后28.674／31.062ms；`RefreshShapingDiagnostics`对应807.078、1266.523／820.156和44.905／38.836ms。**这是整条有限序列的累计采样墙钟时间，非CPU时间、单次动作时间或物理输入／绘制延迟。** 剩余栈仍包含控件重建、Avalonia字体／布局及GC相关路径；整体P99没有稳定改善证据，继续保留F6b排队边界，未制定或宣称达成绝对交互预算。

原执行体来自`git show dea76c9:src/MakoKaraGenerator.Rendering.Skia/SkiaTextLayoutEngine.cs`，只替换这一执行体，用当前其余宿主／探针编译运行，非旧SDK／全仓库版本回滚。SHA和版本边界见`baseline-control-source.json`；全部原字节恢复后重新构建及验证。初次trace误选仅适用于collect-linux的cpu-sampling，启动子程序之前失败；保留`baseline/profile-not-applicable.log`，不计产品失败。

### 当前出口与复查入口

Rendering定向76项、App实际演示注音／整形画笔并发5项通过，零失败／跳过。新增6项通过真实Skia调用计数、完整字形／诊断序列和实际RGBA像素检查重复回退、缺字、同族复用、字体选项独立、引擎释放、淘汰及超长元素；与全新引擎相等。三项成功编译后变异均由实际断言捕获：取消元素缓存、重复打开已有族、放宽容量；首个无实例访问的变异被CA1822阻止，未计成功变异，恢复后调整变异重新编译。证据目录为`artifacts/code-review-repair/ui-responsiveness/`，含TRX、完整build／run日志、五份trace／Speedscope／summary、comparison、原执行体SHA及变异记录。

```bash
dotnet test tests/MakoKaraGenerator.Rendering.Tests/MakoKaraGenerator.Rendering.Tests.csproj -m:1 --filter 'FullyQualifiedName~TextFallbackCacheTests|FullyQualifiedName~SkiaTextLayoutEngineTests|FullyQualifiedName~ShapedDrawUnitTests|FullyQualifiedName~TextElementRulesTests|FullyQualifiedName~StyleDesignRenderTests'
dotnet test tests/MakoKaraGenerator.Spike.App.Tests/MakoKaraGenerator.Spike.App.Tests.csproj -m:1 --filter 'FullyQualifiedName~DemoRubyTests|FullyQualifiedName~ShapedLabelPainterConcurrencyTests'
bash tools/ui-suki-compatibility/layout-check.sh --catalogue
bash tools/ui-suki-compatibility/layout-check.sh --bindings
```

普通界面313／语义22／绑定53／目录29，共417个Headless断言通过，绑定Warning为零；当前solution及Headless构建零警告／错误，不将内存平台窗口称为原生／人工验收。PATCH0.49.2保持公共整形API、作者SDK／中立接口、生产程序集、格式28／16／6、配置1、profile2与固定依赖。按用户要求未跑全量，不启动／操作用户0.43.10窗口；本批只收口重复整形查找，最终连续／人工验收及整体排队边界继续，目标未完成。

## 连续路线复查第一批（产品0.49.2，2026-10-09）

F6c已以`aadcd04`提交推送。本批只修改验收工具与记录，产品执行体、版本和公开合同保持；不是新增一轮产品修复。当前编译后的专用原生作者链**328个断言／14图通过**，退出码0；七组Headless**567个断言**、仓储／保存**29项**和导出最后提交边界**52项**全部通过，零失败／跳过。Headless与原生工具当前构建均零警告／错误，未跑完整solution测试。

### 当前路线及证据边界

| 计划§13路线 | 当前自动证据 | 尚未由这些证据覆盖 |
| --- | --- | --- |
| 1：新建、音频身份与播放 | 原生必填新建／真实初始音频准备／逐字时间与注音；Headless音频33／播放133核对A/B身份、撤销／重做、失效回调、EOF、循环、J/K/L/Space与失败恢复 | 人工听辨、物理键盘／IME及完整人工连续制作 |
| 2：工坊、库和任务保存 | Headless库57／草稿129／迟到导入99／任务93；当前Application29项包含真实双进程仓储冲突与保存队列；原生非法字号保留、修正／一次撤销与固定库保存 | 系统文件选择器、物理快捷键与人工连续制作 |
| 3：脚本配置、应用与闭包 | 原生保存玻璃／逐字强调两配置，通过实际运行按钮调用默认正式验证器，各普通360＋透明360帧成功；Composite制作、全局／局部应用、继承、撤销／重做、双槽布局；实际首次保存对话框／后台写入，搬移后删除专用外部媒体及库再重开，固定资源／实例ID／seed保持 | 自动注入控件值／路由点击不等同物理输入；独立720帧验证不替代完整工程成片 |
| 4：保存、放弃与恢复 | Headless保存23核对实际窗口／命令、单写入合并、保存时继续编辑、关闭等待与不回写旧工程；当前长媒体连续五次编辑各产生真实完整备份，末份搬移／外部资源搬离后重新加载与恢复；原生放弃只清理自身标记，搬移的独立备份经实际首页取消／恢复／明确保存；另一个真实进程验证崩溃后的完整闭包恢复 | 同窗口重新发布恢复夹具不冒充进程崩溃；真实崩溃由独立进程记录证明 |
| 5：成片与事务 | 原生普通／透明短片各60个编码输入帧与独立正式预览逐字节相等；两种格式各在15帧注入失败及用户取消，旧成片字节保持、暂存清理、帧／编码器释放，实际界面状态／报告／重试动作相符；Export52覆盖最后提交边界 | 同一57秒双输出单项定向完整成片已结束并通过全部输入／解码帧、完整音轨及释放（下文）；人工视听仍另记 |

原生配置运行未替换`Runtime.Validate`：探针核对其与生产构造的默认委托相等、固定配置名字、真实总帧数及成功结果。最后一次运行玻璃配置207.07秒、逐字强调3.51秒；这些是正式自有试样的执行耗时，不是UI输入响应指标，不用于收口R13。包／目录选择只注入返回值，保存、运行、实际编码、任务命令与后台写入沿用产品路径。

### 探针修正与失败记录

新增`--author`只执行有限的当前作者链并关闭所有自建窗口。每次路由点击先检查`IsEffectivelyEnabled`；关闭模态导出窗后等待任务收尾，才能点击下一次导出或触发备份。备份请求异步排队，探针等待真实保存队列后才检查现场。放弃对照ADR-0173清理自身恢复标记，外部替换标记由独立仓储测试核对；UI恢复夹具在放弃完成之后单独搬移／发布，明确与真实进程崩溃分开。首次工程保存不再直接调用同步`Workspace.SaveAs`，改走实际对话框与后台写入。

`baseline-author`在第二次导出前点击了仍禁用的动作；`current-author`的两分钟观察期限在玻璃720帧结束前超时；`current-author-progress`在导出模态操作尚未收尾时发起备份，被产品忙状态门禁跳过。三次均保留失败日志，不记整条路线通过，不改产品门禁来迎合探针。正式验证现在有逐段进度与600秒观察上限，超时先取消并等待释放；没有降低帧数、质量、诊断或使用成功结果替身。最后的`current-author-final`才是当前完整原生链的通过证据。

### 本机可复查记录

证据根为`artifacts/code-review-repair/final-acceptance/`：`current-author-final/run.log`／`process.json`／`exit.json`／14图记录原生PID1316290退出0；截图按该进程及窗口ID获取，主窗口实际1440×900、缩放1.25。结束后niri只有原有kitty窗口309，未操作或重复打开历史0.43.10用户窗口。实际故障文字、报告／重试动作及恢复提示截图已复查；控件注入仍不宣称物理输入或人工试听。

`headless/results.json`记录七个独立profile的567个断言；`storage-tests/targeted.trx`及`export-boundary-tests/targeted.trx`分别记录29／52项。`crash/writer-ready.json`确认专用写入进程1307369已发布备份，再仅终止该进程（退出-9）；新进程1307400退出0。外部夹具目录已搬离，新进程从真实持久备份恢复57,600,044字节／300秒音频、5,802,265字节／60秒视频和3个固定包；`fresh-recovery.json`及`summary.json`保留进程与媒体身份，不删除旧用户工程或旧基准。

`long-media-save-current/`记录同媒体和3固定包的五次当前真实编辑／备份及搬移末份闭包后重开／重建恢复服务，PID1323038退出0，`summary.json`核对backupCount=5，`closure.json`核对外部资源不可用时的媒体／包。首次`long-media-save`把tmpfs备份直接`Directory.Move`到工作盘，跨文件系统失败；五份保存虽已写出，整条用例不记通过。重跑仅将夹具输出与备份放在同一文件系统，未改产品或删除旧证据。此轮与完整成片并行，只作为存储功能复查，不把其输入采样与F6b／F6c的完整编辑基准比较，也不据此关闭整体UI延迟。

```bash
bash tools/ui-suki-compatibility/run.sh --native --product --author
dotnet test tests/MakoKaraGenerator.Application.Tests/MakoKaraGenerator.Application.Tests.csproj --no-restore -m:1 --filter 'FullyQualifiedName~LibraryRepositoryTests|FullyQualifiedName~ProjectSaveCoordinatorTests'
dotnet test tests/MakoKaraGenerator.Export.Tests/MakoKaraGenerator.Export.Tests.csproj --no-restore -m:1 --filter 'FullyQualifiedName~ExportCommitBoundaryTests'
```

完整成片只启动一次[R8Full的单项定向用例](../../../tests/MakoKaraGenerator.Spike.App.Tests/Rendering/ProjectRenderSessionTests.R8Full.cs)，现已退出0；终态及完整产物记录于`fullfilm-0.49.2/`，不重复启动，不以部分阶段日志代替终态。该领域夹具含测试专用历史作者构造，单独证明当前后端／事务的完整成片；当前生产作者UI由上述原生链证明，两者不混同。整体UI排队延迟、人工听辨／物理输入与最终总体出口继续，整个目标未完成，全量继续按用户要求暂停。


## 完整57秒双输出自动出口（产品0.49.2，2026-10-09）

上一批工具／记录以`f60bcf0`提交推送后，同一任务继续至终态；**单项1通过、0失败／跳过，退出0**，TRX时长34分35秒。本批只收取结果与同步文档，未重启用例、未修改产品或扩大为完整solution测试。产品执行体基线为`aadcd04`，当前作者UI另由328个原生断言证明；历史领域作者夹具不代替当前UI。

| 输出 | 实际格式 | 编码输入／完整解码 | 视频／音轨 | 完整解码音频与源PCM的RMSE |
| --- | --- | --- | --- | --- |
| 普通 | H.264／AAC，320×180，30fps | 1710／1710 | 各57,000,000μs，差0 | 2,736,000样本，0.00015803572304949747 |
| 透明 | ProRes4444／PCM s16le，yuva444p10le | 1710／1710 | 各57,000,000μs，差0 | 2,736,000样本，0.000011425982532289759 |

每个编码输入帧与独立正式预览的像素和完整诊断相等；全流解码验证内容／透明通道，零掉帧／失败帧。AAC重编码和透明背景依赖回退提示按既有合同保留，不把它们清空。工程搬移、副本与自动备份在专用外部音频／背景已删除时维持完整资源／包闭包；9固定包／34实例、真实前奏、重复强调和粒子／拖尾组合的语义断言保持。

每种输出先保留可播放14秒旧片；30秒`PackageAuthorError`发生前已提交900个输入帧，实际包／修订／导出／实例／Evaluate／30,000,000μs及`src/PackEffect.cs:26:9`来源断言通过。实际编码写入369帧后取消（最后12,266,666μs），旧产物字节保持；两次失败／取消均释放帧、编码与渲染会话、清理全部暂存并回收作者加载上下文，新token最终重导出1710帧成功。末尾会话释放和作者加载上下文无存活断言通过，渲染资源沿正式会话路径释放。

证据为`artifacts/code-review-repair/final-acceptance/fullfilm-0.49.2/`中的`fullfilm.trx`／`exit.json`、阶段`evidence/run.log`、双成片／`.evidence.json`、旧片及完整闭包。保留成片再次按SHA-256与报告核对：普通`3fcd733d8b7a9ef816cebe64a9adb59f5b506166780db2c52976bfbff3c0da61`，透明`0ca31f0210529bd9485817620c25eac57ba718e23d0d691d747f4fc8f2dfc920`。原始报告中的测试临时目录内最终目标路径保持，独立保留路径在旁单列，不将它冒充编码暂存路径。

连续路线的这项自动完整成片出口收口。整体UI排队延迟仍有当前测量反证，人工试听／物理输入与最终总体验收未由自动结果推定通过，整个目标继续；全量仍暂停。


## F6d · R13刷新跟进：画面展示复用与精确分段（0.49.3，2026-10-09）

上一批完整成片记录以`2f731ff`提交推送。当前画面区每次刷新都重建对象列表、预设／分段模板和选择集合；原生作者链的短流程没有证明这些重复工作不存在。本批先用实际控件复现，再按ADR-0182修复。不改变渲染、帧计划、输入优先级、保存或固定预设的数据合同。

四个展示模板仅构造一次。对象绑定／名称／详情／启用／缺失状态相同时复用原项目；变化仍替换实际列表。多选按实际项目引用比较，避免重复添加或重置；目标、继承、输入和动作可用性继续刷新。预设候选按目录对象引用比较，同内容完整重读也使用当前目录对象，保持真正的引用门禁。

实际歌词编辑会先清空再填充行集合，旧分段下拉框因直接消费活集合丢失结束端点。本批为其保留上一份展示快照，再按稳定ID映射新行和文字；撤销／重做不丢精确范围。删除端点时回退到当前行；换工程明确重置两个端点。没有保留过期行对象或将旧选区带入新工程。

### 当前验证及失败边界

- App20／Application11项定向通过，零失败／跳过。App包含历史领域草稿回归，不把它们冒充当前控件入口；当前实际控件由下列Headless及原生流程验证。完整filter、命令和TRX在`artifacts/code-review-repair/ui-refresh/`。
- Headless刷新117／目录29／编译绑定53／真实布局29／语义22／任务93／通用313，共656个实际断言通过。刷新流程覆盖深浅主题与760／1440宽度：重复选择、多选八次刷新、修改文字、撤销／重做、端点删除、同内容目录重读、更新修订后的真实确认、启停及实际换工程。普通选择的全库／索引IO增量均0。最后当前工具重新编译后117项复查通过，不重复计数。
- 三项变异都成功编译后触发实际失败：无条件重置对象来源；恢复直接消费活歌词集合；仅按预设ID保留旧目录项目。两份生产源码按原字节恢复并记录SHA；恢复构建和定向复查通过。初始0.49.2执行体也由新控件流程捕获重复展示模板；首版修复的实际端点失败促成快照修复，不删除失败记录。
- 最终有限原生`--product --applications`54个断言／6图通过，PID1455618退出0，专用窗口已关闭。真实多选一次确认／撤销、具名输入无默认映射、取消／迟到输入拒绝、更新保持实例ID／seed、映射撤销／重做、节点后代范围及继承均保持。深浅主题各1440／940／760宽度，在当前1.8倍缩放检查共享MinHeight与半个物理像素取整、控件穿过各滚动视口后可见，以及固定1.8倍缩放、视口变化期间同一输入／逻辑弹层状态保持。
- 首轮原生尺寸断言将布局高度与36作精确比较，在当前1.8倍缩放得到36.111而失败。取`2f731ff`两份原始视图执行体及原版本，先复现同一失败，再按实际MinHeight及物理像素取整修正探针；原执行体48项通过。产品尺寸／主题不改。旧执行体和当前源码恢复均有独立构建记录。
- 图片检查还发现首次将窗口从平铺搬成浮动时，旧原生弹层的屏幕位置不能由逻辑状态断言证明正确。最终截图在布局稳定后重新打开弹层，只记录当前入口和实际输入；它们不作为“保持打开的原生弹层位置已修复”的证据。这个缩放／平台边界继续，物理输入／IME／试听及Windows均不由本流程推定。

初始探针编译错误、首版真实端点失败、CS8602失败编译及随后误用旧二进制的一次运行均保留；后者在`refresh-after-current/invalid-evidence.json`明确标无效，不能作当前源代码证据。之后每次运行以成功编译为前提；最后滚动视口检查的CS0246亦仅记录编译失败，修正命名空间后才运行，无旧产物执行。当前solution、Headless及原生构建零警告／错误。

### 同夹具性能及下一步

仍使用300秒音频／60秒视频63,402,309字节、三个固定包、100个材质模板、101,980字节库。重新核对媒体／字体SHA与先前夹具一致；环境、运行命令和全部样本保存在本批目录。相同Headless实际选择／文本命令序列，隐藏及可见各20次编辑，Input优先级5ms发布；无修改探针优先级或编辑／诊断／绘制。以下均为未插桩输入任务排队分位数，非物理输入／绘制延迟：

| 执行体／样本 | P50 ms | P95 ms | P99 ms | 最大 ms |
| --- | ---: | ---: | ---: | ---: |
| 原0.49.2／0 | 229.7958 | 603.0181 | 665.1264 | 675.3756 |
| 原0.49.2／1 | 216.3705 | 565.1765 | 620.0948 | 630.3690 |
| 原0.49.2／2 | 229.6719 | 634.6573 | 703.2472 | 714.5346 |
| 当前刷新执行体／0 | 293.4491 | 801.5148 | 862.3710 | 882.8212 |
| 当前刷新执行体／1 | 237.0320 | 644.2362 | 707.4654 | 717.6770 |
| 当前刷新执行体／2 | 227.7692 | 609.9569 | 671.7823 | 682.0567 |

当前刷新执行体三次测量仍标0.49.2，发生在最后PATCH版本更新之前；其生产刷新代码与0.49.3相同，后续只增加测试覆盖／版本号。全部六次未插桩样本保留，不筛掉较慢运行；没有整体P99改善证据，也尚不收口整体交互预算。

0.49.3另一次线程采样的输入P99为1195.836ms，与未插桩样本分开。主线程整条序列累计采样墙钟中，RefreshTarget约200ms、RefreshApplicationObjects约170ms、ReloadPresets约50ms、RefreshPresetInputs约14ms、RebuildTokens约390ms、TemplatedControl.ApplyTemplate约748ms；包含启动，嵌套项不能相加，非CPU或单次操作成本。下一步从歌词元素重建和模板／字体布局继续取证，不能将本批展示复用称为全部卡顿解决。

PATCH0.49.3保持公开签名、作者SDK、中立接口、生产程序集、工程／歌词／模板28／16／6、配置1、profile2及固定依赖。当前57秒后端成片证据仍明确来自0.49.2，本批未重跑该长片或全量；没有把旧版本成片记作0.49.3最终全量。整个修复目标继续，已有用户窗口未操作，专用原生进程均有限退出。

可复查入口：

```bash
dotnet build MakoKaraGenerator.slnx --no-restore -m:1
bash tools/ui-suki-compatibility/layout-check.sh --appearance-refresh
bash tools/ui-suki-compatibility/layout-check.sh --catalogue
bash tools/ui-suki-compatibility/run.sh --native --product --applications
```


## F6e · 歌词卡片保持连接与稳定焦点（0.49.4，2026-10-09）

F6d已以`9fbea74`提交推送。原0.49.3执行体在新增实际控件检查中复现换行全部重建；其完整git快照另在当前环境编译，焦点尺寸／排列检查也失败。当前卡片按显示位置保留实际Border／TextBlock和控件树连接，只创建新增位置、移除多余位置；空行／空分支清空，不保留整首歌曲或隐藏池。ID和文字相同的时间更新保持实际卡片和焦点。

事件在使用前核对当前行ID／索引、控件位置、元素ID和文字；焦点、左右键及鼠标重排读取当前目标，不捕获创建时行号。捕获固定发起工程引用和分支，期间换工程（即使行／元素ID与文字相同）、换分支、换行、清空、离开或关闭释放指针，旧手势不写入新行。现有重排时间确认、迟到工程／行／元素门禁和一次撤销保持。模型重载先通知行、再恢复元素，卡片焦点与最终当前ID同步；空行清理待恢复请求，源文本焦点不被旧请求抢走。

卡片原焦点样式临时增加2px边框，引起尺寸／排列变化，原执行体已复现；原生滚动后卡片底边超过真实视口约1.111逻辑像素。当前UiControlMetrics增加共享FocusBorderThickness，非焦点预留透明边框，焦点只换颜色。字体、CompactHeight／Padding、主题刷子和实际重排语义保持。

### 定向与变异

当前App50项通过（LineWorkshop、歌词导入／拆分、明确分支导入、中性预览和撤销一致性），零失败／跳过。Headless卡片129／通用313／语义22／播放133／绑定53，共650个实际断言通过；深浅主题、760／1440宽度覆盖连接保留、源文本／时间、选择／左右键、完整grapheme、捕获鼠标重排及换行、相同ID复制工程的旧手势取消、撤销／重做、缩短／空行／空分支和空行后输入焦点。

九项成功编译后的变异分别检出：新建卡片没有同步实际展示、焦点事件使用旧行、复用文字未更新、旧拖动换行条件、重新脱离／挂接控件、焦点停在旧位置、焦点临时增大边框、空行保留旧焦点请求、相同ID复制工程绕过发起工程引用门禁。前六项在113项正向通过后执行，边框／空行两项在扩充至117项正向通过后执行，最后工程引用门禁在129项正向通过后执行；新增空行清理不改变前六项失败位置。各次编译门禁及恢复字节记录保留，最后工程门禁先在真实复制工程换入时复现写入新目标，再修复并以编译后变异检出；恢复后当前129项和其余回归重新通过。

无效尝试单独保留：首次探针缺少TranslatePoint扩展的using，编译失败后没有执行；保留连接的早期草稿尚未通过源编辑焦点正向检查，其六项变异全部排除。新增空行检查首次拷贝保留较早时间戳，增量产物仍只有113项，不作该新增检查证据；后续强制新编译并核对117项，最终使用当前构建。初版单纯复用后仍清空／重加，采样成本没有下降；据此保留实际树连接，早期基准与记录作为草稿保留，不混作最终结果。

### 有限原生与探针边界

当前有限原生歌词路线69个断言／8图全部通过，PID50249退出0并关闭专用窗口。真实源输入、时间、注音、非法时间草稿、换行／卡片复用／左右键、源编辑／撤销、失败脚本下中性预览、播放／循环、增强LRC确认／取消及迟到结果保护通过。深浅主题、1440×900／940×640／760×640与1.8倍缩放的当前卡片在真实滚动视口内完整可见，截图复核了紧凑窗口的焦点边框和当前文字。

原生初次在940×640要求编辑区高于150时失败；精确原0.49.3完整git快照同样失败，实际高度125／宽700。原生探针统一采用现有Headless紧凑布局的90高度检查，并增加当前卡片在每层ScrollContentPresenter内完整可见和焦点检查，半物理像素仅用于取整。随后这一更具体的检查暴露焦点边框真实改变布局；先保留失败结果，再修复上述产品样式，未用放宽裁切容差掩盖问题。专用配置／窗口只属于本批进程，不操作用户已有窗口；程序化原生焦点／按键不推定物理键盘／IME或试听通过。旧原生弹层搬动位置仍保持原边界。

### 当前环境的固定基准与线程证据

300秒音频／60秒视频仍为63,402,309字节，三个固定包、100个材质模板和101,980字节库；媒体和DejaVu／NotoCJK／NotoEmoji字体SHA与前批复核相同，SDK10.0.112、AMD Ryzen AI 9 H 365／20逻辑CPU。当前内核7.2.9-1-cachyos，前批记录为7.2.8-2-cachyos，因此在当前环境重新建立原0.49.3基准，最终再交替运行原／当前各三次，不与旧开机数据混算。以下原执行体和当时0.49.4保持相同编辑序列、绘制及诊断；没有固定其他用户进程负载，单次差异不作因果证明。Input优先级5ms任务排队指标不是物理输入或绘制延迟。

| 执行体／样本 | P50 ms | P95 ms | P99 ms | 最大 ms |
| --- | ---: | ---: | ---: | ---: |
| 原0.49.3／0 | 483.2476 | 1179.3669 | 1265.3337 | 1299.6985 |
| 当时0.49.4／0 | 502.8997 | 1269.6009 | 1373.8598 | 1393.3367 |
| 原0.49.3／1 | 476.2523 | 1255.5383 | 1340.4838 | 1376.1522 |
| 当时0.49.4／1 | 516.1133 | 1241.4255 | 1333.1348 | 1368.9630 |
| 原0.49.3／2 | 529.4427 | 1184.6337 | 1271.8242 | 1308.6015 |
| 当时0.49.4／2 | 452.3589 | 1251.3750 | 1350.9609 | 1371.2778 |

所有最终交替六个未插桩样本、首次原基准三次以及各版草稿／最终初测的三次结果保留，不筛掉较慢运行。隐藏／可见各20次选择／文本编辑的完整库和索引读取增量均0，目录不在UI读盘，首次全库读一次。整体排队出口和交互预算继续，不把实际控件复用本身写成全部卡顿解决。

原0.49.3累计RebuildTokens采样361.520ms；最初清空／重新加入的复用草稿为387.458ms，实际保持连接的最终执行体为67.993ms，CreateTokenCard约7.430ms。最终TemplatedControl.ApplyTemplate仍约1763.187ms、Reload约824.329ms；包含启动与其他工作，不相加、不当CPU或单次操作成本。该最终线程采样的输入P99为2297.8983ms，与未插桩六次分开，完整原／草稿／最终trace均保留。最终交替三对样本中两次P99高于原执行体，一次较低，没有整体延迟改善证据。实际Reload的视图重新创建、主题／字体布局成本仍待取证，按用户最新指示暂缓。

本机日志、TRX、全部性能样本、git基线与SHA、变异／恢复及原生8图保存在`artifacts/code-review-repair/ui-tokens/`。当前solution、Headless及原生构建零警告／错误；PATCH0.49.4新增GUI共享焦点尺寸项，作者SDK／中立接口、生产程序集、工程／歌词／模板28／16／6、配置1、profile2及固定依赖保持。未跑全量或重复启动已结束的0.49.2长片；旧长片不冒充0.49.4最终全量。整体UI排队、主题／字体布局暂缓；物理输入／试听及最终功能出口继续，整个目标未完成。

**用户最新指示（2026-10-09）：性能相关暂时跳过，不自动恢复优化／基准／线程采样。** 上述测量在最后工程捕获门禁补齐前已结束，保留历史证据；门禁之后不再复测性能。整体预算与Reload／主题／字体布局转为暂缓待办，不阻挡本批功能交付，不记为性能已完成。全量测试仍暂停，最终功能连续路线及人工边界继续。

## 当前功能连续路线与逐项出口核对（产品0.49.4，2026-10-09）

F6e已以`a4e5dd4`提交推送。当前专用原生作者路线328个断言／14图通过，PID54880退出0且所有自建窗口关闭；未操作用户kitty、Edge或QQ窗口。两份脚本配置各720帧正式验证、实际预设／继承／撤销与布局、保存对话框／后台写入、删除外部库和媒体后的闭包搬移重开、双输出短片、失败／取消及实际首页恢复已贯穿当前GUI。重新编译明确采用当前生产App资源与零警告产品依赖，不拿旧探针二进制运行。

普通／透明各60个编码输入帧与独立正式预览逐字节相等；两成片再实际解码各60帧、88,200个44.1kHz单声道音频样本，音视频均2秒。源26秒音轨被明确2秒输出限制，报告中的源时长差是提示，不等于当前产物超长。失败检查在第15帧刻意抛出探针编码异常`UI author verification: encoder interrupted after fifteen frames`，截图与堆栈明确来自InterruptedEncoder，属于旧片保护／释放／重试验收；另有实际用户取消动作。两格式均保持旧片字节、清理暂存并释放全部输入帧／编码器，结束后恢复生产导出器。当前日志无`[KaraokeCanvas]`或“预览渲染失败”，正式画布稳定后无RenderError；不将该记录替代其他未知提示的具体排查。

用户随后要求“先暂时忽略这条，继续完成剩下的内容”。其所见提示原文尚未确认，此条暂缓，不自动恢复排查；上述故障注入结论仅覆盖已核对的截图／堆栈，不把未知提示记为已解决。

逐项核对见[最终出口核对](../research/code-review-remediation-final-audit.md)。R01～R13、B01、A01～A04的合同／实现／验收来源、21个实施提交祖先、五条连续路线与剩余人工出口已汇总。从已验收长片记录`2f731ff`至当前提交，src及Export／Rendering／Runtime.Hosting测试源码无变化；保留0.49.2双长片的SHA与终态TRX复核一致，不将旧二进制记作0.49.4全量或重启该已结束任务。

本机证据为`artifacts/code-review-repair/final-acceptance/author-0.49.4/`中的build／run日志、source／process／exit、14图、functional-audit及shortfilm-decoding记录。普通0.49.4手测启动脚本已准备于`manual-0.49.4/start.sh`，使用已验证普通构建及独立配置，启动前核对产品DLL；未替用户启动窗口。仅新增验收文档与本机记录，产品版本／源码／合同／格式／依赖保持。

自动功能连续路线当前通过；物理键盘／IME、实际听辨和完整人工制作结果仍待用户反馈，原生弹层搬动位置仍有已记录边界，Windows／物理多屏未推定通过。性能与全量按用户要求继续暂停，不自动恢复；整个目标未完成。

## F6f：具名来源菜单位置改变时收起并保留选择（0.49.5，2026-10-09）

0.49.4当前连续复查已以`8570970`提交推送。本批保持打开的原生截图明确复现旧菜单停在原位，而输入窗口内位置从660／760变化到1092／461（日志为逻辑坐标，截图为物理像素）。首次有效记录的专用窗口仍持有焦点、菜单未关闭；随后另一次失焦且菜单在调整前已关闭的记录不作为产品缩放失败。公开位置属性刷新及失效绘制未得到正确截图；窗口内宿主缺少必要层、底层接口／构造器不能外部使用，均未进入产品，也未因此升级依赖。

用户明确选择“接受自动收起并保留选择”。依据ADR-0184／[菜单位置合同](../legacy-ui/preset-input-popup-motion-contract.md)，具名来源使用内部PresetInputComboBox并沿用Suki模板／原生弹层。实际客户区、可报告的位置、缩放或输入相对窗口矩形改变时收起，祖先滚动亦覆盖；稳定布局保持打开，关闭后不自动重开。输入拆除／菜单关闭／模板重建解除监听，再挂接及显式打开跟踪新宿主。来源选择、待应用映射、工程和撤销历史保持。

当前最终原生同一进程186个断言／6图通过，深浅主题各1440／940／760宽度，1.25倍缩放；PID91984退出0并关闭所有自建窗口。实际源选择框、打开前置门禁、静止布局、窗口尺寸、局部Bounds不变的祖先滚动、拆除／再挂接、旧PopupRoot退场、新原生菜单及共享尺寸均核对。6图展示重新打开的新菜单位置，不冒充旧菜单定位。此前两个正向专用序列分别78／6图与138个断言通过，保留其原工具范围，不叠加为最终用例数。

App领域定向9项、Headless通用313／实际画面刷新117／编译绑定53通过；solution、Headless和原生工具零警告／错误。初次隔离App输出在/tmp，7项旧领域夹具未找到仓库资源入口，未进入产品断言；只补该自建目录的仓库／samples链接后9项通过。Headless平台与当前Suki窗口无可用弹层宿主，原生打开／关闭的新断言迁入原生流程，未增加假的事件替身。首次BringIntoView未稳定即打开时，随后布局按新合同收起；最终稳定输入后打开，保留实际打开前置断言。失败编译没有启动旧二进制。

两项成功编译后的变异分别去掉收起、只比较锚点尺寸，专用PID88033／88199均在实际尺寸／祖先滚动断言退出1。源码按原字节恢复并核对SHA，随后当前原生和solution重新构建／正向复查通过；不把编译失败或其他初始化失败计为变异捕获。

证据为`artifacts/code-review-repair/popup-motion/`的build／TRX／headless／native-verified／mutation-results及source记录；旧位置和私人替代试验记录保留在`final-acceptance/popup-*-0.49.4/`。普通0.49.5手测入口为`final-acceptance/manual-0.49.5/start.sh`，校验当前产品DLL并隔离配置，尚未替用户启动。此前0.49.4启动入口在共享构建更新后会按校验明确拒绝，不能绕过。

PATCH0.49.5保持作者SDK／中立接口、生产程序集、格式28／16／6、配置1、profile2及固定依赖。用户裁决下的菜单功能出口通过，原平台旧菜单重定位机制不记为修复。物理拖动／IME、听辨及完整人工制作继续；0.49.4连续成片、0.49.2长片保留各自执行体范围，不改标为当前全量。全量、性能和未知失败提示均继续按用户要求暂缓，不自动恢复，整个目标未完成。

## 暂停期间独立修复：普通入口新建与字体空流（0.49.6，2026-10-09）

原goal按用户要求保持暂停。用户普通0.49.5日志确认SKShaper空`asset`异常发生在确认新建后的工程替换中；旧普通执行体故障注入：首页部分发布、菜单退出-6。当前可读字体门禁／回退、新建发布前诊断及统一失败反馈见[新建合同](../legacy-ui/new-project-startup-contract.md)／ADR-0185，不认定用户环境空流原因已定位。

最终Rendering64／App5全部通过，solution零警告／错误构建。实际普通0.49.6两入口正常新建及故障后重试共32断言／6图，四个专用PID退出0；无字体新进程证明真实空流机制与产品中文错误。三项成功编译后变异捕获实际断言／异常退出，生产源SHA恢复；恢复后定向64／5及模板重试11断言／2图再次通过，PID201175退出0。版本不匹配hook和早期编译失败排除。所有这些结果不包含物理文件选择器、输入、试听或Windows。

由于原NTFS内核写入阻塞，本批在健康home同名分支副本完成并推送，原工作目录及旧脚本未同步；详情及证据路径见合同。普通0.49.6健康独立包338文件已核验并启动，PID203090／niri153，日志只有版本记录。旧媒体／配置保留；自有D状态线程尚未保证释放，未执行root或磁盘修复。原goal、全量、性能及此前未知提示继续暂停，整个目标未完成。

**用户后续人工反馈（2026-10-09）：**“测试完成，目前已经可以正常新建窗口。”本次新建入口恢复人工通过；原goal仍暂停，其余条目和精确字体失败环境不扩展确认。逐项改动及验收步骤见[用户验收清单](code-review-remediation-user-acceptance.md)。本次是文档整理，没有新增构建、测试、性能或原生自动化。

## 独立修复：普通启动导出入口（保持0.49.6，2026-10-09）

用户要求移除原goal、修复后重启且不改版本。当前工具没有删除接口，其系统记录仍暂停；本次单独修复，不继续原路线。已用手测包同一DLL证明未探测FFmpeg使菜单／快捷按钮灰色，而探测仅在被禁用的对话框内启动。打开入口现只保留装配／并发／当前效果错误门禁，实际开始仍使用原CanExport；详见[导出入口合同](../legacy-ui/export-entry-contract.md)。

solution及普通hook零警告／错误构建，App19／Export探测2项及Headless118断言通过。新回归不预先探测，覆盖菜单、快捷按钮、缺失编码器和缓存失败重开；旧谓词成功编译后变异触发实际入口断言，源码SHA恢复。普通App菜单／快捷两条新进程各14断言，PID278412／280101退出0；两份3秒640×480／24fps影片各72帧和完整3秒音轨解码通过，SHA相等。首轮探针关闭等待和缺native诊断失败已分别记录并排除；不作为产品失败。证据位于`artifacts/code-review-repair/export-entry-diagnosis/`和`export-entry-repair/`。

已备份用户Test工程、库及状态，重启前后工程文件SHA保持。准备关闭时旧PID203090已退出、日志未记原因；新普通PID283038／niri231已启动，沿用原profile、原入口转向新包，无startup hook或自动交互。版本保持0.49.6，格式／SDK／依赖保持。没有全量或性能复测，不推定实际用户工程导出／试听、物理输入及总体出口已通过。
