# 控件接线检查修复与非渲染验证（0.49.6，2026-10-10）

## 问题与修正

`main` 集成前的非渲染验证在产品基线 `1dba195feec9e43ebc075660d09043eee7e43a57` 上发现控件接线守卫误报。`PresetInputComboBox` 保留 `ComboBox` 的 StyleKey，并在模板应用时调用 `e.NameScope.Find<Popup>("PART_Popup")`。固定 SukiUI 7.0.1 的[源码模板](https://github.com/kikipoulet/SukiUI/blob/495a238268b8778ecd71b20f6727e82a17df38da/SukiUI/Theme/ComboBoxStyles.xaml#L144)确实声明了这个 Popup；它属于依赖库模板自己的作用域，而不属于本工程视图 XAML 的名称集合。

原测试用正则匹配所有 `Find<T>` 文本，没有区分调用接收者。现在使用已有 Roslyn 依赖解析实际调用，视图的 `Find`／`Require` 继续核对工程 XAML；直接 `NameScope.Find` 属于模板部件合同，不要求视图重复声明。没有按 `PART_` 前缀整体放行，也没有改产品控件或复制 Suki 模板。注释和字符串中的示例不作为实际调用。动态名称仍保持原检查边界，模板部件实际可用性由对应模板／运行验收证明，本测试不宣称覆盖所有模板的部件拼写。

新增14个参数化回归用例：普通／this／其他视图查找、Require、缺失的 PART_Popup 仍被拒绝；已声明控件通过；模板接收者、留白和注释隔开时正确识别；注释／字符串不制造实际调用。原工程扫描与新用例共15项全部通过。只修改测试与验证记录；产品、版本号、依赖和持久格式保持0.49.6原状态。

## 验证结果

仓库内固定目录 `artifacts/current-build` 构建零警告／零错误。初次缺少 Roslyn 扩展命名空间的编译不计通过；补齐引用后复用同一目录增量构建并通过。完整非渲染范围在相同构建上执行 **2311项，零失败／零跳过**，进程退出0，耗时42.708秒；TRX逐项核对排除项未执行，受测文件SHA保持。

| 项目 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: |
| Application | 164 | 0 | 0 |
| Core | 1207 | 0 | 0 |
| Export（非渲染白名单） | 16 | 0 | 0 |
| Media | 126 | 0 | 0 |
| Runtime.Hosting（非实际渲染） | 12 | 0 | 0 |
| Scripting | 264 | 0 | 0 |
| Spike.App（非实际渲染） | 438 | 0 | 0 |
| Timeline | 84 | 0 | 0 |
| 合计 | 2311 | 0 | 0 |

此前仓库外 `/tmp` 构建产生13项仓库定位失败；移回仓库内后只剩本次接线守卫误报。本次没有将失败用例加入过滤器：原失败项已实际执行并通过。两轮失败记录与修正后的终态分别保留，不覆盖或改标旧结果。

## 测试范围和复查入口

用户要求不跑实际渲染。此次排除整个 `MakoKaraGenerator.Rendering.Tests`、App的 `Rendering` 命名空间；Export只运行 `EncoderAvailabilityTests`、`FfmpegCommandLineTests`、`UserInterfacePurityTests`。其他项目保留全部非实际渲染用例，包括模拟帧状态、脚本编译／执行、资源释放、媒体读取、库并发和保存。以下散落的实际像素渲染／正式成片用例另行排除：

- `MakoKaraGenerator.Spike.App.Tests.ViewModels.BasicCompositionTests.基础编排只投影基础样式和布局并保留完整工程`
- `MakoKaraGenerator.Spike.App.Tests.ViewModels.LineWorkshopTests.描边模板制作保存套用读取和撤销形成闭环`
- `MakoKaraGenerator.Spike.App.Tests.ViewModels.SceneEditorTests.新工程完整制作双槽居中三槽并保存重开随机跳转和撤销`
- `MakoKaraGenerator.Spike.App.Tests.ViewModels.TemplateAuthorFlowTests.多行试样有真实注音重复词间奏和可渲染标记`
- `MakoKaraGenerator.Spike.App.Tests.ViewModels.TemplateAuthorFlowTests.工坊自有媒体使用真实分析且能独立正式渲染`
- `MakoKaraGenerator.Spike.App.Tests.ViewModels.TemplateWorkshopPreviewTests.行模板修改样式后直接重建中央预览`
- `MakoKaraGenerator.Spike.App.Tests.ViewModels.ExportStateTests.进度回调回到调用方线程而不是后台线程`
- `MakoKaraGenerator.Spike.App.Tests.ViewModels.ExportStateTests.验证仍可取消提交关闭取消且迟到进度不覆盖最终状态`
- `MakoKaraGenerator.Spike.App.Tests.ViewModels.ScriptRuntimeValidationTests.正式完整试样通过后明确采用失败源码仍失败而不回退旧DLL`
- `MakoKaraGenerator.Spike.App.Tests.ViewModels.ScriptRuntimeValidationTests.真实作者在三百毫秒报告Error不能被首帧通过掩盖`
- `MakoKaraGenerator.Spike.App.Tests.ViewModels.ScriptRuntimeValidationTests.真实背景材质双输出通过且保留透明回退提示和部位能力`
- `MakoKaraGenerator.Spike.App.Tests.ViewModels.ScriptRuntimeValidationTests.非法已覆盖材质保留实际错误而非回退成通过`
- `MakoKaraGenerator.Runtime.Hosting.Tests.ProjectHostingTests.无GUI调用真实固定包编译原生装配且独立会话重放保持全部帧和诊断`
- `MakoKaraGenerator.Runtime.Hosting.Tests.ProjectHostingTests.静态路径不创建编译缓存且只接受创建时的快照`
- `MakoKaraGenerator.Runtime.Hosting.Tests.RuntimeValidationTests.纯执行器渲染真实控制器和原生图的两种输出`

可在仓库根目录复查同一范围，复用固定构建路径：

```bash
task_build_root="$PWD/artifacts/current-build"
task_filter='FullyQualifiedName!~MakoKaraGenerator.Rendering.Tests.&FullyQualifiedName!~MakoKaraGenerator.Spike.App.Tests.Rendering.&FullyQualifiedName!~MakoKaraGenerator.Spike.App.Tests.ViewModels.BasicCompositionTests.基础编排只投影基础样式和布局并保留完整工程&FullyQualifiedName!~MakoKaraGenerator.Spike.App.Tests.ViewModels.LineWorkshopTests.描边模板制作保存套用读取和撤销形成闭环&FullyQualifiedName!~MakoKaraGenerator.Spike.App.Tests.ViewModels.SceneEditorTests.新工程完整制作双槽居中三槽并保存重开随机跳转和撤销&FullyQualifiedName!~MakoKaraGenerator.Spike.App.Tests.ViewModels.TemplateAuthorFlowTests.多行试样有真实注音重复词间奏和可渲染标记&FullyQualifiedName!~MakoKaraGenerator.Spike.App.Tests.ViewModels.TemplateAuthorFlowTests.工坊自有媒体使用真实分析且能独立正式渲染&FullyQualifiedName!~MakoKaraGenerator.Spike.App.Tests.ViewModels.TemplateWorkshopPreviewTests.行模板修改样式后直接重建中央预览&FullyQualifiedName!~MakoKaraGenerator.Spike.App.Tests.ViewModels.ExportStateTests.进度回调回到调用方线程而不是后台线程&FullyQualifiedName!~MakoKaraGenerator.Spike.App.Tests.ViewModels.ExportStateTests.验证仍可取消提交关闭取消且迟到进度不覆盖最终状态&FullyQualifiedName!~MakoKaraGenerator.Spike.App.Tests.ViewModels.ScriptRuntimeValidationTests.正式完整试样通过后明确采用失败源码仍失败而不回退旧DLL&FullyQualifiedName!~MakoKaraGenerator.Spike.App.Tests.ViewModels.ScriptRuntimeValidationTests.真实作者在三百毫秒报告Error不能被首帧通过掩盖&FullyQualifiedName!~MakoKaraGenerator.Spike.App.Tests.ViewModels.ScriptRuntimeValidationTests.真实背景材质双输出通过且保留透明回退提示和部位能力&FullyQualifiedName!~MakoKaraGenerator.Spike.App.Tests.ViewModels.ScriptRuntimeValidationTests.非法已覆盖材质保留实际错误而非回退成通过&FullyQualifiedName!~MakoKaraGenerator.Runtime.Hosting.Tests.ProjectHostingTests.无GUI调用真实固定包编译原生装配且独立会话重放保持全部帧和诊断&FullyQualifiedName!~MakoKaraGenerator.Runtime.Hosting.Tests.ProjectHostingTests.静态路径不创建编译缓存且只接受创建时的快照&FullyQualifiedName!~MakoKaraGenerator.Runtime.Hosting.Tests.RuntimeValidationTests.纯执行器渲染真实控制器和原生图的两种输出&(FullyQualifiedName!~MakoKaraGenerator.Export.Tests.|FullyQualifiedName~MakoKaraGenerator.Export.Tests.EncoderAvailabilityTests.|FullyQualifiedName~MakoKaraGenerator.Export.Tests.FfmpegCommandLineTests.|FullyQualifiedName~MakoKaraGenerator.Export.Tests.UserInterfacePurityTests.)'
dotnet build MakoKaraGenerator.slnx --artifacts-path "$task_build_root" -m:1
dotnet test MakoKaraGenerator.slnx --artifacts-path "$task_build_root" --no-build --no-restore -m:1 --filter "$task_filter" --logger trx --results-directory artifacts/non-render-verification/results
```

本次日志、范围清单、定向和完整范围TRX、受测源码及程序集SHA、进程终态位于 `artifacts/non-render-verification/control-wiring/`。构建输出在验证／集成完成后清理，只保留小体积证据。完整含渲染solution、独立Headless／原生探针、性能、物理输入／IME、听辨及Windows／多屏均未在本批重新运行；用户反馈的当前构建带脚本渲染成功保留其人工反馈范围。

## 集成约束

用户已授权非渲染测试通过后合并 `main`。本批修复与验证记录用一个提交交付；在核对远程祖先关系后使用 `--ff-only` 集成，不改写既有历史。实际集成结果以本地／远程Git引用为准。

仓库CI在 `push main` 时自动运行含渲染的完整solution。因此本次提交使用 `[skip ci]`，遵循此次非渲染范围，CI配置保持；该标记只对本次提交的push／pull_request触发有效，依据[GitHub说明](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/skip-workflow-runs)。原goal仍暂停，未完成的人工／平台和性能边界不因集成而记为完成。
