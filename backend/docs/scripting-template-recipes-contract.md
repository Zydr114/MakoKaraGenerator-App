# 模板效果配方与资源闭包实施合同

2026-10-04；依据已接受合同A§7.4及当前脚本目标授权，先设计再实施。

## 1. 本批源数据

程序0.17.0；工程schema25／歌词15／模板4／包profile2。不转换旧库或工程。
新库位于应用配置目录templates-v4/index.json，完整包在packages/<hash>/；旧templates.json不读、不覆盖。
未建立新库时保留内置模板并从空个人库开始；旧库存在的说明是信息，不把它当新库损坏。
新index版本或结构损坏阻止写回，不能用空库覆盖原文件。

StyleTemplate增加完整不可变Packages和有序EffectRecipes；包字节不含机器路径、DLL或运行对象。
配方必填recipeId、name、target（line／tokenFamily）、packageId、revision、exportId、parameters、overlayLayer。
行模板可含两种target；元素模板只含tokenFamily。无源目标ID、InstanceId、订阅、会话、出生或粒子。
数组顺序是配方顺序；overlayLayer为package／behindText／aboveText，仅影响Overlay输出，不改变文字／图的顺序。
非package层要求导出真实声明Overlay；包参数、导出、绑定类型及非组输出由唯一EffectPackageDefinition校验。
每份模板的Packages恰好为所需修订闭包；文件清单已经覆盖其图片、shader、源码及其他资源。
纯效果模板可无静态写入属性；完全空模板拒绝。直接材质仍待MaterialSpec源结构接续，不伪造静态字段。

index必填schemaVersion、packages及templates；packages引用packages/<hash>并校验实际包身份。
模板entry保留静态Style及Properties，effectRecipes必填（静态模板为[]）。完整包先发布，index最后原子替换。
已有内容地址不覆写，失败保留旧index，删除模板不删除历史包对象。加载严格读取资源，不返回丢配方的静态替代模板。
目录复制／分享独立拥有index与全部包；Application再提供.mktpl ZIP，目录与ZIP同闭包，严格文件集合及预算。
库读取／保存及ZIP展开统一按全部内容计费（含index，默认256MiB／1024文件／单文件64MiB，可显式调整）；index另受16MiB上限。正式读取在每文件分配前计费，UTF-8非法字节拒绝。
前奏提示仅提供可应用的静态模板，不将行／元素脚本配方隐式改绑到标记。

## 2. 套用与来源

ProjectEffectInstance新增可空templateOrigin及overlayLayer。来源保存templateId、recipeId、ownerKind、ownerId。
owner为本次套用的行／元素，真实输出目标由唯一binding保存，不另存目标列表。
line配方对所选行创建单目标Lines实例；tokenFamily配方按该行内各元素创建单目标Tokens实例。
元素模板仅对所选元素创建实例；全工程范围沿现有全部分支行／元素路径。
每个实例去重键为(binding类型, 单个目标ID, templateId, recipeId)，来源与单目标绑定结构一致。
源删除后保留失效实例供修复，原启用错误门禁保持。

首次套用注册完整包、创建独立ID和随机种子；同目标同模板同配方更新原实例并保留ID、seed和enabled。
再次套用相同内容不产生新工程或新历史；配方顺序改变时重排本次模板块，其他模板及手工实例的相对顺序保持。
已有块放回其原始位置；首次块加在实例末尾。已移除配方或不再属于本次行的元素配置只移除本次模板来源实例。
其他模板／直接实例不删除；显式清除此模板效果与恢复静态样式继承分开，清除可作为一次历史。
复制源实例清除templateOrigin，使副本成为直接实例，不占原套用槽；编辑绑定需显式脱离来源。

静态写入与效果套用合为同一预览结果和一次工程历史；ChangedCount按套用目标计算，效果变化也计数。
所有目标及包先验证，失败不返回部分结果。真正套用重新检查当前源与配方，预览不持有运行状态。
UI工坊保存／复制静态部分必须保留已有配方及闭包，不能悄悄丢失。当前静态画布明确标注配方未预览；完整脚本预览仍按R7接入。

## 3. 运行与能力边界

CompiledProjectEffects把overlayLayer投影为固定宿主设置；正式视觉批次、连续出生和事件Burst统一应用，粒子查询的ParticleSample.Layer也保留本帧实际层；使用现有发射器批次层，不把它误称为出生时固定层。
只有声明的Overlay接受层覆盖，原声明／预算／事件／时间／几何均不改变；幕后不是修改作者包或源码。
模板内已有控制器使用原真实Roslyn／固定几何／样式／图／粒子路径；非法或未接通导出拒绝执行，不输出无效果成片。
直接图实例、MaterialSpec源格式、跨输出计划、完整工作区和模板配方UI继续原R2～R8任务，本批不宣称脚本交付完成。

## 4. 验证出口

覆盖完整库／ZIP分享、原库保留、独立闭包、严格资源／参数／target／layer门禁、发布失败保护旧index；
行与元素／跨分支全范围、再次套用身份／seed／enabled稳定、重排／删除旧配方、直接实例与其他模板保护、清除及历史；
工坊改名／改色／复制／删除保持配方；保存重开离线编译，真实文字／注音像素、Overlay及连续／Burst层、MP4与Alpha同帧和失败保护。
全量构建／测试、当前窗口启动、ADR和文档同步、一步提交并推送远程。证据补记roadmap§18.69。


## 5. 编译接续中的稳定性收口

首次全量出现17项Roslyn元数据Emit内部NullReference；同一混合包组独立80项全部通过。为限制同时热编译时的出口并发，ScriptCompiler采用进程内可取消的串行门禁，CSharpCompilation concurrentBuild=false，选项进入产物指纹。没有把异常改成成功或重试旧产物；坏源码继续返回具名诊断。新增六次并发提交相同复杂SDK源码的DLL／PDB一致性验证，原取消／新进程复用／卸载测试保留。最终证据以roadmap§18.69为准，未据此宣称已定位第三方缺陷根因或完成性能评测。
