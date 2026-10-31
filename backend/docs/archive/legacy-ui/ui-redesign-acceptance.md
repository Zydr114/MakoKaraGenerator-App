# UI重设计与Suki迁移验收（当前0.41.1）

## 人工反馈修复进度（2026-10-07）

用户指出高级／诊断／帮助／取色弹层透明、视图菜单重复效果入口、两个基础任务页签及按钮层级不一致。本轮按该反馈修复，桌面自动化仍禁用。

第一步已修复固定Suki7.0.1的Flyout呈现器：原模板外壳写死Transparent且未绑定Background，现以原生SukiFlyoutPresenterStyle为基础，只修正外壳背景／边框／圆角／内容滚动；颜色从SukiPopupBackground取值，菜单Flyout、右键菜单、提示及色板使用同一弹层表面。`artifacts/ui-headless-layout.s4y0y0`在深浅主题实际打开普通Flyout、取色器、ComboBox、菜单、ContextMenu与MenuFlyout，检查实现模板内的实底与布局；同时保留原短窗回归。此证据来自Headless，未操作用户桌面，不声明合成器像素或物理取色验收。

第二步已统一全部工作区页签为原生ToggleButton：同一选中淡色底／底线、前景、字号、留白和36px高度，NavigateTask排他设置IsChecked；高级页切回／复用／关闭仍沿原草稿门禁。移除视图中的效果入口及接线，选定高级入口后收起菜单。标准文本按钮统一36px／14px字／12,6留白，紧凑区32px／13px字／10,5留白；歌词辅助按钮也移除局部12px字号及旧留白，系统图标／色块／卡片继续使用专用尺寸。顶部菜单行默认拉伸按钮的尺寸问题通过垂直居中修复。主／次／辅助动作使用原生Flat／Outlined／Basic，导航不使用动作强调层级。

| 0.41.1验证 | 实际结果与证据 |
|---|---|
| 最终Headless | `artifacts/ui-headless-layout.cLqcgH`：114条PASS，构建0警告／错误；760／940／1440宽度，反复切换、打开／复用／关闭高级页、动作实际尺寸、六个歌词辅助按钮密度及原短窗滚动均通过 |
| 弹层离屏像素 | 内存平台实际打开模板，Skia绘制10份深浅主题Flyout根PNG并校验背景像素alpha=255；普通Flyout、取色、高级、帮助和诊断均通过。ComboBox／菜单／右键／MenuFlyout同时检查实际模板内不透明表面 |
| 回归 | `/tmp/makokara-0.41.1-ui-feedback-tests.log`：3197项通过，0失败／跳过；排除唯一57秒R8完整长片（0.41.0已全量验证）。回归后只清理歌词辅助按钮局部字号／留白，最终114项Headless覆盖该变化，领域／渲染路径不改 |
| 最终构建 | `/tmp/makokara-0.41.1-ui-feedback-build-delivery.log`：全solution零警告／错误，默认应用输出0.41.1 |

SukiWindow在Headless缺少原生Popup宿主，实际主窗口Flyout内容挂到普通内存Window的overlay入口检查呈现器；此证据不覆盖主窗口弹层的物理位置或焦点。原生桌面自动化仍禁用，修复后的真实桌面外观、物理取色及用户操作等待人工复查。程序PATCH升0.41.1，schema26／16／5、profile2及精确程序版本门禁保持。

2026-10-07；分支`codex/ui-suki-redesign`。用户已接受[P0原型](../../design/ui-redesign-prototype.html)，P1～P5代码已实施，P6自动验证已完成；最终人工验收待完成。入口见[使用指南](ui-workspace-guide.md)。schema26／16／5、效果包profile2不变，0.40.2工程按严格版本门禁拒绝，文件不转换。

## 自动证据

| 验证 | 实际结果与范围 |
|---|---|
| 独立固定版本原生关卡 | Suki7.0.1／Avalonia12.1.3、1200×820／760×640、1.25缩放、原生输入／ColorView／弹层／对话／通知；见[兼容记录](ui-suki-compatibility.md) |
| P1独立源码快照 | `artifacts/ui-p1-isolated-build`零警告／错误；3184项回归全通过、0失败／跳过（排除单项完整R8长片，随最终集成验证） |
| 产品原生作者链 | `artifacts/ui-suki-compatibility.7IKc0t/build.log`零警告／错误，`run.log`213条PASS（含截图检查），27张截图；实际1440×900／940×640／760×640、1.25缩放 |
| 作者输出 | 同一新UI工程普通MP4与透明ProRes4444各60个编码前输入帧严格比较独立正式Preview；最近保存重开及输出取消入口通过 |
| 实际交互 | 数值合法／非法连续输入一次Undo／Redo，一次Flyout连续选色一次Undo，默认整首／显式目标，粘贴离开取消，模板脏稿切换保留／复用／关闭取消／放弃，全部原生对话初始化／关闭 |
| 领域回归 | 最终非长片3197项全部通过、0失败／跳过；`/tmp/makokara-ui-delivery-tests.log`。集成全量3198项全部通过、0失败／跳过，退出码0；`/tmp/makokara-ui-final2-tests.log`与`artifacts/ui-final2-r8-evidence`保留完整长片／恢复证据。最新界面收尾后再次执行3197项非长片及38项无窗口布局检查，长片对应领域路径未改变 |
| 最终构建 | `/tmp/makokara-ui-final-build-after-review.log`零警告／错误；默认应用输出为0.41.0，未自动启动额外窗口 |
| 最终短窗修正 | `artifacts/ui-headless-layout.VZ4BuU`零警告／错误，38条PASS；真实控件树在728×277／908×277／1408×530工作区检查内部列表高度、1000字反馈、常驻保存、滚动可达性及预览／属性区域 |

产品截图只说明拍摄时的代码。以下代表入口：

- [首页](../../design/ui-redesign/author-home.png)
- [歌词打轴](../../design/ui-redesign/author-lyrics-timed.png)
- [画面与样式](../../design/ui-redesign/author-appearance.png)
- [最小窗口歌词任务／深色](../../design/ui-redesign/product-main-minimum-dark.png)
- [高级编排分配](../../design/ui-redesign/product-scene-assignment-minimum.png)与[时间预览](../../design/ui-redesign/product-scene-preview-minimum.png)

模板／效果最小窗截图暴露了内部裁切，已作为修正前诊断保留，不列为最终视觉通过。原生外层测量通过不代表内部控件可用。最终短窗滚动修正使用Headless检查；用户指出自动窗口影响使用后停止原生自动化，**未重新检查最终修正的合成器像素**。

## 失败与边界

0.40.2基线构建零警告／错误，3186通过、1项R8旧片报告IsVerified失败，原因未确认；不把基线记为全绿。较早集成全量有一项新增粘贴测试错误地比较ImmutableArray容器；改为逐项验证已有对象引用及数量，保留Unicode／无虚假时间／撤销断言，重新执行最终全量。失败或中止探针不算最终通过。

真实中文字符串赋值只能验证显示，不能证明IME组合／候选／提交。Wayland当前1.25缩放不能代表物理多屏DPI或Windows；自动作者链不能代表用户视听及操作验收。完整SDK、组图／父捕获、跨输出及R3.1等门禁保持，既往Emit／TypeLoad间歇风险未定位。

## 用户人工路线

1. 新建工程并导入音频，导入或粘贴中文歌词；检查中文IME、焦点与长行。
2. 在歌词任务打轴、拆分／合并、注音／翻译及多来源关联；检查循环、播放和撤销。
3. 进入画面任务，默认整首调字号与颜色；显式选行／字，检查局部覆盖、非法输入和恢复继承。试用单／双／三行与选段布局，复查提示的影响范围。
4. 在宽／窄窗口打开三个高级页；重点复查最小760×640下模板与效果滚动、属性／预览、完整诊断和常驻保存。切换保留草稿，关闭／回首页分别试保存、放弃、取消。
5. 制作模板并分享／导入闭包，定位精确实例，保存失败源码、编译新修订并单实例接受；保留其他实例身份。
6. 导出普通与透明成片；取消／失败后检查旧产物，保存重开和备份恢复。Windows与多屏DPI结果单独记录。

原生探针新增显式`--native`开关，默认拒绝桌面自动化；常规验证使用无窗口入口。本轮未恢复桌面自动化，也不自行启动额外窗口。默认应用输出已重建至0.41.0，用户可自行启动验收：`dotnet run --project samples/TechnicalSpike.App --no-build`。提交历史及同名远程分支记录施工进度。

## Git交付

已分步提交并推送同名远程分支`origin/codex/ui-suki-redesign`：

| 提交 | 可复查步骤 |
|---|---|
| `82bb73e` | P0施工合同、已接受原型及独立兼容证据 |
| `7743365` | P1原生主题／窗口、固定依赖与许可证 |
| `08a71e5` | P2～P6工作区、即时编辑、生命周期、回归与使用文档 |

最终收尾保留原生探针执行权限，默认仍拒绝桌面自动化；未传`--native`时退出64。当前默认应用构建为0.41.0；本轮没有自动重启应用或窗口测试，人工项目仍按上表边界保留。
