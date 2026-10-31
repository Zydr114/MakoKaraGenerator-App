# R6 完整粒子包与混合绘制接续

> 2026-10-05，承接 ADR-0123～0126。0.31.0已实施；实际完成证据以 roadmap §18.94 为准。

本批把已有公开宿主能力接到可分享、可持久使用的作者包：VectorTrail／SpriteTrail 增加 transition、bridgeUs、bridgeGapLimitUs，默认 Jump 保持；Bridge 与圆点包共用实际后继、独立端点及配额，不在作者源码另建模拟器。VectorTrail 保持 dots／segments／ribbon 三种呈现，SpriteTrail 保持固定图片参数和纯视觉旋转／拖尾。两包升1.1.0、源码／README摘要更新，图片字节不变。

新增完整 EventBurst 包，导出 event-burst，输出 burst。anchor 选择四种真实来源：token 在 TokenStarted 使用 SourceToken；line 在 LinePerformanceStarted 使用 SourceLine；cursor 在逐字起唱、或无逐字时间的行起唱时使用全部 BindingCursors；mark 在真实 ElementVisible/Mark 使用 TextMark。每个事件／每个真实装载各 count 条；并发事件使用全部当前头时会分别发射，不能偷偷合并事件。count 使用清单 number、范围0～Int32.MaxValue，初始化还须整数，零合法且不分配粒子；其他物理／预算沿现有 Burst 合同。默认 token/count=6，寿命600000us、尺寸6px、散布12px、拖尾180000us、颜色(1,.4,.1,.9)，世界速度(0,-10)px/s。

输出绑定和源订阅仍独立。包默认使用 BoundSources；用户扩大订阅后显式 SourceToken／Line／Mark 越权按现有门禁失败，不能根据宽订阅扩大输出或静默抛弃错误。标记没有假TokenId／TrackId，单元素绑定不获得整行／标记写权。作者只登记意图，真实最终几何、预算、随机、身份、寿命和正式绘制由同一宿主负责。

实施组合出口：三种拖尾呈现与桥接、真实 Burst、双文字／音频及行 Glitch 共同消费持久工程；检查事件／出生身份、同一时刻的24／30／60FPS、前奏／槽位、暂停／跳转／普通／透明、层与实例顺序、同种子／改种子、全部预算和错误保护。使用真实目录／.mkfx分享、工程保存重开、正式 CPU／MP4／Alpha、热替换和关闭释放。独立回归必须能通过变异检出失效桥接或错误事件映射；全量构建／回归及Git独立提交／推送后记录证据。

本批不改公开宿主SDK、schema26／16／5、profile2、Shader ABI、程序集或依赖；不开放IncludeInTargetCapture、复杂图映射、owned overlay或父组图，保持明确门禁。这些仍在既有高级输出路线，不以完整作者包或本批自动证据声称完整R6／SDK／R3.1／R7／R8或人工验收完成。

自动验收新增26项：Scripting6检查完整包及默认／显式Bridge描述、Burst初始化整数门禁；Spike.App20通过真实包／参数／事件／准备帧／CPU像素验证上述组合。精灵保持24/s默认，向量保持60/s，45001us飞行窗口分别1／2条，不以相同数量覆盖不同rate。真实MP4和ProRes4444各54帧保留音轨，966666us同帧Alpha差≤3、RGB均差≤9；存活预算38在500000us第二个Burst失败，旧产物和暂存清理验证。源码显式热替换、1×／2×密度及质量切换、旧和当前作者上下文卸载有组合证据。

强制作者忽略Bridge选择的变异使真实发射描述检查失败；去掉整数门禁的变异使分数初始化拒绝回归失败。两次源码及清单均逐字节恢复，日志 `/tmp/makokara-0.31.0-mutation.log`。未取消生产图片完整解码门禁：Scripting层只验证无图片作者包，Sprite通过正式App后端验证／运行，未增加渲染依赖或假验证器。最终全量和应用启动证据见 roadmap。

后续自动收口见[R6验收表](archive/acceptance/scripting-r6-acceptance.md)／ADR-0128、roadmap §18.95：六段视频全部410个输入帧与独立预览比较，完整解码、回退后全段重放、两种格式取消与全部108个恢复帧和作者上下文释放已验证。默认Jump与零空档跨行另有直接比较。该批只增加回归和文档，生产程序／包字节仍为0.31.0；收口后续计划第4批明确范围，原路线高级能力及最终工作区／人工验收保持未完成。
