# 组透明度与嵌套隔离实施合同

2026-10-04。按已接受的 R0 §6.3、ADR-0073／0090 接续；不改变工程 JSON、源树或编排规则。

## 本批边界

- `Style.MultiplyOpacity(GroupTarget, factor)` 仅工程／显式节点绑定可写真实组。因素有限且非负；同目标按调用和实例顺序相乘，最后校验静态基线 × 总因素在 [0,1]，不夹值。组不获得文字填充、描边或阴影。
- 准备帧保存最终有效组因素及真实实例归属，恒等输出省略；隐藏但真实的组仍校验。复制准备帧及重放保持输出。
- 有效组在其第一项原绘制位置成为连续合成单位；孩子按原绘制序号排序，槽位树序不作为 Z 序。父子有效组逐层完成合成并各乘一次 alpha。
- 先完成孩子的实际生产图结果，再统一应用组透明度。通过外部下层与已处理画面的线性混合，使普通透明内容和替换下层图都按相同 alpha 淡出；下层不重复叠加。组 alpha 不改变原歌词／注音／前奏的共同 alpha。
- 交错的后代因隔离变为连续单位时输出具名诊断，缓存和导出报告保留。无有效组 alpha 继续原入口和顺序。
- 隔离工作表面、下层快照、产物和最终混合共用帧／会话资源账。预算、取消及 native 错误明确失败并保留真实实例；不降分辨率。

## 接续

0.12.2 已交付组透明度及嵌套主 Render 隔离。组图 writer、向父图交付可按配方读取的孩子来源、owned overlay、自身粒子下层排除、区域材质与完整工作区继续原路线；不将图语义简化为对最终背景画面的 shader。未支持的图捕获继续明确失败。

## 验证要求

真实离线作者代码与包声明／写权；重叠孩子、父子 alpha、独立翻译和前奏；真实行图与组 alpha、透明和不透明下层；恒等及隐藏组；交错顺序诊断／缓存；共享预算回收／取消；预览与 MP4／Alpha 同帧及失败保护。

## 原始捕获与资源限制

独立 CaptureSceneTarget 的 TargetColor 若范围自身或显式后代含有效组 alpha，会报告 GroupOpacityCapturePending；不能忽略新输出并返回原始字形。原始 Coverage 忽略外观 alpha、保持几何；祖先 alpha 不混进子对象的局部输入。后续父图捕获必须读取隔离中的真实部件产物。

组工作表面目前使用实际全画布密度，不按墨迹缩图；很深的树可能触及明确预算并失败。显式子文字 SaveLayer 纳入同一账；完整 native 滤镜 scratch、分支 overlay 和 owned overlay 资源出口仍待后续，不宣称完整 R3 资源出口。

## 0.12.2 实施与出口证据

新增 Rendering24／Scripting5／Export3；公开作者实际离线编译和包默认／覆盖参数、六种绑定写权、声明不能越界、跨实例最终alpha／归因与重放通过。主画布验证原始入场／注音／前奏／独立翻译、重叠槽位alpha为127～128而非逐孩子约192、嵌套alpha各一次、隐藏／恒等与副本、非均匀密度、真实行图和替换下层淡出、外部下层不含自己／不重复、原始捕获门禁及Coverage独立、预算／取消回收、作者资源结束与宿主程序独立。交错运行绘制序的诊断和缓存验证通过；该夹具显式交错真实运行对象，不改变软件分支源序规则。

三个真实包短片用例经正式 Render 验证：MP4／Alpha 第15帧与预览，RGB平均误差<9、Alpha误差≤3；300000μs写失效组停止编码、保护原输出、清理临时产物。定向Render43项／Export3项通过，记录 `/tmp/makokara-group-opacity-render-final.log`、`/tmp/makokara-group-opacity-export-final.log`；SDK定向20项通过，记录 `/tmp/makokara-group-opacity-sdk-first.log`。

最终构建0警告／0错误，八项目2386项全部通过（83／1000／74／87／709／170／179／84）。日志 `/tmp/makokara-group-opacity-build-final.log`、`/tmp/makokara-group-opacity-tests-full.log`。沿用现有缓存授权：全量 `dotnet build MakoKaraGenerator.slnx --no-restore -m:1`、`dotnet test MakoKaraGenerator.slnx --no-build -m:1`；无依赖改动或本轮在线审查结论。

最终0.12.2已直接启动，PID540185／niri320，日志 `/tmp/makokara-f93b-group-opacity-final.log` 记录同一版本，未见托管启动异常；niri321是旧自动备份触发严格版本门禁，没有转换。未新增脚本工作区或以启动代替人工脚本验收，原有窗口保留。本批仍不关闭完整R2～R8。
