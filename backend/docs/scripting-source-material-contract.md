# 源材质唯一真值与准备帧归属实施合同

2026-10-04；按已接受合同A§6.1／§7及当前脚本目标授权实施，不重问已确定的透明回退、覆盖和uniform规则。

## 1. 分步出口

M1：Core材质判别模型、整体覆盖、当前JSON、工程／模板闭包与校验、静态编辑保护。
M2：固定源材质解析、真实源归属、准备帧、正式Render及预览／成片同帧；无需伪造脚本实例或每帧编译。
M3：R7工作区中的材质选择／参数／明确替换操作，与跨输出计划和图直接实例接续。

M1不能宣称包源材质已可播放：M2接通前，加载／保存完整合法数据，运行入口明确报告SourceMaterialPreparationPending；不能返回纯色替代或成功的无效果成片。已有脚本写入的包材质继续原真实路径。

## 2. 材质模型及编辑语义

MaterialSpec仅三种：Solid(RgbaColor)、Gradient(完整GradientSpec)、PackageMaterial(packageId,revision,exportId,完整EffectParameterOverrides)。Core不引用SDK／Skia。
FillSpec只持有一个Material，不再保存color与gradient双值；旧源码访问外观不是第二份源数据。颜色读取仅适用于solid／gradient，gradient取首色标；包材质没有伪造的“纯色”。
编辑渐变颜色更新首色标，关闭渐变显式得到首色标的solid，不恢复隐藏旧色。包材质通过完整Material显式替换，旧颜色／渐变写入不能悄悄删除它。填充材质不可null，关闭填充使用透明solid。

FillSpecOverride仅一个可缺省的完整Material；缺失=继承，显式null无效。材质整体替换，旧颜色／渐变选择入口均写入相应完整材质；恢复任一入口恢复该材质继承，不能声称仍是两个独立覆盖属性。无基准的“只关闭渐变”不可持久化，调用者须提交明确solid。
StrokeSpec为几何＋单个Material；几何width／join／position可逐项覆盖，material整体覆盖。整个stroke:null关闭；透明solid不另保留隐藏颜色。透明solid不绘制可见颜色，但正宽度仍保留真实描边几何，StrokeCoverage不乘材质alpha；完全取消几何使用stroke:null或width=0。阴影结构保持。

## 3. 当前格式与资源

M1程序0.18.0；工程26／歌词16／模板5，包profile2。只接受当前格式，不转换旧工程或旧模板；新库templates-v5保留templates-v4与templates.json。
填充直接保存：{"kind":"solid","color":"#RRGGBBAA"}；{"kind":"gradient","gradient":完整规范渐变}；{"kind":"packageMaterial","packageId":...,"revision":...,"exportId":...,"parameters":完整对象}。
每种变体严格校验专属字段，不接受旧color＋gradient或混合变体。描边保存geometry对象及material；覆盖geometry成员可缺省、material整体缺省继承；null材质拒绝。缺失／非法源数据不合成默认材质后保存。

工程effects.packages是全部材质／实例包的唯一登记，源材质不增设第二张实例表。遍历基线、分支／行／元素覆盖及真实文字标记；相同修订共享完整资源。模板所存静态材质及相对配方共同决定完整Packages闭包，分享／套用／另存为／备份继续先资源后唯一入口。源材质引用也阻止注销，历史字节不GC。
固定解析校验export为真实material、实际fill／stroke区域、全部参数、修订及profile；不把包内fallback写回源样式。引用错误保留明确诊断供修复，缺失闭包不伪造资源。模板创建／分享严格拒绝坏引用。

## 4. M2准备帧与错误归属

SourceMaterialOwner与ScriptMaterialOwner区分真实来源：前者指向源对象／部位／固定修订，后者保留真实EffectInstanceId和ArtifactHash。源材质没有脚本实例ID、随机种子或伪造DLL代次。
最终基线StyleView保留真实MaterialHandle；只在字形绘制种子中使用明确白色区域种子。脚本后写Set替换源材质，uniform遵循已确认身份、部位、调用顺序与写权；末次归属进入准备帧。几何查询不执行材质图。
纯材质包无需C#控制器；固定shader／图片／图资源在会话建立前验证，不在帧内编译。无脚本实例的源材质也要求正式准备帧／固定资源。缓存与会话指纹包含材质源及参数，改变参数不重编译无关源码。
颜色／覆盖／alpha、真实下层、透明跳过与包基础外观沿既有R3路径。源失败附真实对象／部位／包修订，阻止导出并保留旧产物；不能假冒脚本异常身份。

## 5. 验证

判别结构／严格JSON／精度／整体覆盖／继承／源来源、资源闭包／注销／库断代／分享／历史；现有solid／gradient像素及alpha保持，包颜色编辑保护；源材质在M2之前明确失败。
M2覆盖基线／分支／行／元素／注音／标记、脚本替换／uniform、无C#纯材质、真实预算／下层／透明回退及MP4／Alpha同帧与中途错误保护。
每个出口同步实际证据，独立提交／推送；R2～R8总目标保持，工作区与性能基准按原路线继续。


## 6. M1实际证据（0.18.0）

模型／覆盖／严格当前JSON、完整工程／模板资源、注销／诊断和静态编辑保护已实施；M2真实源归属／准备帧及M3材质选择／参数尚未接入。程序集／公开作者writer不新增，包profile2保持。源包运行明确未就绪，原脚本写入材质／uniform保持可用。

新增32项，最终2706项全通过、0失败／跳过，全量构建0警告／错误。真实solid／gradient基础字、注音、描边、前奏与透明像素保存往返一致；纯源材质模板独立分享、移除自身原库后套用／工程重开保持完整修订和精确参数。严格错误／闭包、UI只读／保存复制和未准备运行门禁验证通过；正宽度透明材质的描边覆盖回归已修复、原断言保留。

标准全量曾遇到Roslyn内部Emit间歇异常，最终同构建重跑通过，原因未确定，风险未解决；不修改运行时或吞异常、不回退旧DLL。完整证据、日志和后续边界以roadmap§18.70为准，不将M1宣称为源材质实际成片或完整脚本交付。

最终0.18.0已直接启动PID1290924／niri362，日志仅版本记录；旧工程恢复出现版本提示363。无新的材质参数／脚本工作区人工验收，未终止旧进程。


## 7. M2实施细化（0.19.0，实施前确定）

准备帧的材质Owner改为真实Source／Script判别来源。Source保存原始存储路径、部位与固定材质引用，Script保存真实实例与编译代次；源没有实例／DLL身份。错误和透明回退诊断也保存同一来源，导出不得用普通单帧背景兜底吞掉源材质失败。最终归属按既有材质操作顺序，脚本Set／uniform写入后才由该真实脚本接管；alpha／阴影不抢归属。

CompiledProjectEffects编译启用实例包和所有存储源材质包的修订并集，同修订只验证一次；所有资源、导出区域和精确参数在会话前验证，纯资源包没有C#也可成功。源解析集合固定完整快照指纹，将真实参数和句柄投影到BaselineStyles；源继承路径按基线→分支→行→元素以及提示组→标记取实际材质写入层，描边宽度覆盖不改变材质来源。更改源／参数使旧会话失效；帧内不解析包JSON或编译shader。

源材质与脚本材质共用现有元素族／标记区域主Render，注音随基础字；只在显式DrawingStyle中建立白色几何种子，真实StyleView与所有者仍保留包身份。无控制器的源材质工程也创建独立准备会话；未提供准备帧／固定native资源必须失败。共享字簇不能独立裁切的目标沿既有门禁拒绝，不改变排版或静默忽略。现有尚未接通的跨输出依赖、owned overlay、实际映射等仍明确报告Pending，不能因为来自源就绕过。

M2不新增工程字段、包编码或依赖；schema26／16／5和profile2保持。准备帧公开来源协议为不兼容变更，程序升MINOR0.19.0；作者writer规则不改变，真实SDK字节仍进入缓存。R7自动组合根和材质选择／参数保持M3；已有工坊提示仅在真实运行接续后按实际状态调整，不伪造效果预览。


## 8. M2实际证据（0.19.0）

固定源投影、真实Source／Script来源、BaselineStyles、纯帧／时间线／独立端点和正式元素族／标记区域Render已接入。无C#的纯材质包保存重开后不产生控制器产物或效果实例，真实句柄／参数进入基线；分支、行、元素的材质来源与描边几何来源分开，前奏覆盖保留实际pendingFill／elapsedFill路径。脚本按显式部位Set／uniform后写、alpha不抢归属已经实际验证。

新增Rendering11／Export3，共14项；最终八项目2720项全通过、0失败／跳过，构建0警告／0错误。源材质与真实C#写入同一材质的基础字／注音／描边／前奏，在10s／13s／40.5s及1／2倍密度、透明／不透明条件下逐像素相等；倒退重放的像素、每目标／部位完整Owner和Material均保持。源继承、前奏局部覆盖、透明缓存真实来源和预算超限全部回收、错误参数／缺少native资源／旧指纹／取消明确失败通过。源材质record格式化不再读取包材质不存在的静态颜色，原真实颜色访问仍明确拒绝。

实际保存重开、无控制器源工程进入MP4／ProRes4444；解码500ms帧与预览按既有编码容差一致，透明背景依赖产生真实源路径的三个部位提示并使用自身基础外观。200ms材质输出坏覆盖返回SourceMaterialFailureException，保留真实路径／修订／时间，停止编码、保护旧产物并回收临时输出。既有脚本、图、音频／Callback重放、预算及成片测试保持通过。

最终命令：dotnet build MakoKaraGenerator.slnx --no-restore -m:1；dotnet test MakoKaraGenerator.slnx --no-build -m:1。日志/tmp/makokara-source-material-runtime-build-seventh.log和/tmp/makokara-source-material-runtime-tests-final-full.log；定向源材质19项与纯源导出3项另有独立通过记录。本轮未改Roslyn／运行时配置；既往内部Emit间歇异常的根因和风险仍未解决，不能用本次全量通过宣称根因修复。

最终构建直接启动PID1324042／niri371，标题0.19.0演示工程。日志/tmp/makokara-f93b-source-material-runtime-final.log包含版本及ALSA／JACK设备探测警告，未见托管启动异常；不把窗口存活当作音频已验收，没有终止旧进程。自动组合根、材质选择／参数与模板实时预览仍保持M3／R7后续，当前工坊静态入口持续提示未就绪；此批不声明源材质GUI人工验收。schema26／16／5及profile2不变，程序MINOR0.19.0，无新依赖／程序集／作者writer，完整R2～R8保持未完成。
