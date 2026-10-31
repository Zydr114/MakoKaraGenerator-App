# 工程后台保存与恢复标记合同

F6a／R13在0.45.3实施，依据[修复计划 §9](archive/plans/code-review-remediation-plan.md)、ADR-0173及[实际验收](archive/acceptance/code-review-remediation-acceptance.md)。模板目录缓存已在0.45.4按[独立目录合同](template-library-catalogue-contract.md)接通。

## 输入和所有者

`ProjectWorkspace.CaptureSave`由工作区所有者线程捕获不可变Project、目录、工作区Generation、dirty和时间。新建、成功打开、恢复递增Generation；普通编辑、撤销和保存不改变工作区身份。后台存储只接收`ProjectSaveSnapshot`，不持有或读取可变工作区。

Application层`ProjectSaveCoordinator`由每个MainWindow持有；`IProjectSaveStorage`提供同步完整闭包写入及独立恢复标记操作，生产实现继续使用ProjectStore／AutoSaveStore／AutoSaveSession。服务不引用Avalonia，不新建工程历史或第二套领域状态。原同步ProjectWorkspace.SaveAs和ProjectAutoSave.Save仍供后台用例使用，内部同样以实际捕获的快照完成；生产UI不再调用同步自动保存。

## 写入与完成

单个后台任务串行写入。最多保留一个正在执行的写入、一份待办的最新自动备份和一份受命令互斥保护的手工请求；自动备份替换待办，不无限排队。手工保存取消尚未开始的旧自动待办，排在执行中的事务之后；其间的新自动快照仍可合并，成功另存为后按实际落盘目录备份。相同已备份Project引用不重写，失败保留重试资格；手工成功重置该判据，撤销回此前备份实例也能建立更新的恢复现场。

手工写出的Project经UI线程`AcceptSave`更新saved引用、目录和诊断。保存期间的新编辑仍dirty，状态及提示明确“此前快照已保存；新编辑仍未保存”。旧Generation或协调器Epoch不接受完成，不更新新工程标题、目录、状态或saved引用。最近列表写入也使用捕获的名称／路径，在后台执行；其失败作为已保存后的警告。

自动备份先完整写出独立目录，再单独发布恢复标记；只有两者成功才记为已备份。标记失败报告并允许同一快照重试。手工根入口已经发布后，清理旧标记失败保留`RecoveryError`警告，不能把工程提交谎报成失败。存储或通知观察者失败均不使手工完成Task悬空；观察者失败记录诊断，不改变已提交结果。

## 离开与关闭

放弃／换工程先同步使Epoch失效，拒绝新请求并取消待办，再异步等待执行中的完整资源事务结束；当前Core同步事务不做半途破坏性中断。标记操作在状态锁外执行，等待不阻塞UI dispatcher。已开始发布标记的竞争同样等待到安全点，再按完整AutoSaveSession比较清理本协调器的标记，不删除被另一现场替换的标记。

正常手工保存保留编辑入口，新增／打开等文件动作仍受互斥门禁保护。保存期间关闭先停止继续编辑并异步等待已接受的手工快照，再走保存／放弃／取消确认。正常关闭在保存结束后释放预览和音频；`OnClosed`只停止定时器、注销通知和释放视图模型，不再补写。离开取消或成功进入下一工程后Resume重新接受当前工作区捕获的输入。

每份备份仍有独立媒体、分析、歌词及效果包闭包；完整hash／长度校验、刷盘、资源先发布／入口最后替换、保留份数与版本门禁保持。没有跨备份共享媒体、schema变动、作者SDK或新生产程序集／依赖。

## 验证与基准入口

[应用层回归](../tests/MakoKaraGenerator.Application.Tests/ProjectSaveCoordinatorTests.cs)验证实际写出的输入、保存期间新编辑、合并、串行、重试、旧结果、标记竞争及提交后的警告。[有限Headless路由](../tools/ui-suki-compatibility/PersistenceFlow.cs)通过真实文本控件、菜单、关闭和首页动作验证dispatcher响应及资源等待，不使用桌面窗口。

`bash tools/ui-suki-compatibility/layout-check.sh --persistence`执行路由。基准使用同一工具的`--persistence-benchmark`，须设置`MAKO_PERSISTENCE_FIXTURE`为专用媒体目录；生成300秒48kHz双声道PCM16音频及60秒640×360／30fps H.264视频，工程含GlassInk／WordEmphasis／CursorTrail三个固定包，隔离库含100个普通模板。每次五个不同快照，各等待真正落盘，记录输入优先级dispatcher任务从后台投递至执行的延迟；不把它当作物理键盘、IME或绘制延迟。

基准先在0.45.2取得63,402,309媒体字节／71,843库字节的实测，再同条件测0.45.3。由该基准设定当前有限Headless检查P99≤50ms，使用可选`MAKO_PERSISTENCE_P99_BUDGET_MS=50`；硬性边界仍是生产保存IO不在dispatcher、单写入且待办合并。50ms不是所有机器或原生交互保证，F6b已另行验证普通编辑零目录IO；带窗口目录基准不沿用本保存基准的50ms检查。

`dotnet-trace`10.0.750501的EventPipe sampled-thread-time记录管理线程采样栈，Speedscope保留线程及完整调用链；不据此宣称CPU占用、完整native阻塞栈或功耗结果。工具在本批隔离目录安装，不需要root、不进入产品依赖；命令口径见[官方文档](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace)。实际环境、分位数、栈和边界见修复验收。

[独立进程恢复入口](../tools/ui-suki-compatibility/PersistenceCrashRecovery.cs)的`--persistence-crash-writer`写出真实大媒体备份及恢复标记后通知专用父进程；父进程仅SIGKILL自身子进程，再在外部素材不可用时用新进程`--persistence-recovery`验证完整闭包。子进程等待最多60秒，父进程有期限并清理自身进程；此证据覆盖进程崩溃恢复，不代替断电、原生或用户人工验收。

全量命令仍为`dotnet test MakoKaraGenerator.slnx --no-build -m:1`，仅记录给用户；按用户要求暂不自动执行。
