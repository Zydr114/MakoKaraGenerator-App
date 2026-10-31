# 工程媒体与音频分析修订存储合同

2026-10-04；依据已接受合同A §7.3、基础音频合同及当前脚本目标授权；先设计再实施。

## 1. 数据结构与断代

程序0.16.0、工程schema24；歌词15／模板3／效果包profile2保持，不转换旧工程。
内存继续以AudioPath／Background.Path供媒体适配器定位，增加不可变MediaFileRevision描述
（实际源字节SHA-256、长度、原文件名），持久位置唯一为data/media/<hash>.media。
使用固定后缀、解码器按真实内容探测；原文件名用于界面，不改变内容身份。
工程入口必填media对象，含audio／background两项（无媒体显式null）。原path字段须与内容地址一致；纯色背景不能携带媒体修订。
新选择媒体是尚未发布的外部源；加载工程后只有受管位置，不保存外部绝对引用。
源内容缺失或已变更导致保存失败，不把缺资源标为完整闭包。

Project.AudioAnalysis保存已接受的成功修订或失败原因；null表示未分析／无音频。
成功数据保存算法bass-pulse-rbj-v1、48kHz、960样本hop、origin=0、实际样本数、
源／PCM hash、真实半开时长及完整hop特征（低频、pulse、可空phase、confidence）。
算法语义修订沿用现有分析器身份；文件内容地址另以规范UTF-8字节hash覆盖全部特征值。
Core持有中立源值、无SDK依赖；Media投影真实分析结果，Scripting转换为原有AudioFeatures。
原算法、单位、查询和事件不另造一套逻辑。更换音频清除旧描述与分析，撤销恢复二者。

入口必填audioAnalysis对象：noAudio／notAnalyzed／failed／available；failed保留code／message，
failed另保存availability:noAudio／analysisFailed，区分实际无流与解码失败。available显式引用data/analysis/<hash>.json与语义revision，且sourceHash必须匹配音频修订。
失败／未完成不写虚假的样本文件；坏分析引用、hash、结构、profile、时间网格或数值拒绝打开。
加载不启动FFmpeg重建；缺本机缓存仍可读工程的完整已接受分析。

## 2. 发布与失败

音频、背景图片／视频全部流式复制和SHA-256，不读取大媒体为整块byte数组。
先写唯一暂存、Flush(true)、核对实际长度／hash，源再核验未变，再不覆写发布；
已有地址完整核验才复用。分析JSON同样刷盘／校验并不覆写发布。
所有资源先完成，再效果包／歌词、最后根入口；任一失败不改原入口和saved reference。
加载先精确版本门禁，再严格引用／链接／hash／长度校验；不生成可保存空工程。
保存、另存为及每份备份都各自携带完整资源，移除collectAssets可选收拢参数。
旧assets、旧媒体、旧分析、历史引用及未引用对象无GC，不删除用户文件。
持久IO限制默认单媒体16GiB、单分析256MiB，ProjectStoreLimits可显式调整；超过报告错误，
不裁切、不下采样、不降低分析密度。

## 3. 运行与UI边界

完整成功分析转换回真实SDK AudioFeatures，保留原语义revision；固定会话指纹另覆盖分析全部字节。
CompiledProjectEffects接收整个固定Project，自动注入真实分析／不可用状态，不使用播放器状态。
控制器输出requires:["AudioFeatures"]为明确required声明，不新增另一份实例开关；其他未实施需求仍失败。required仅要求初始化前有完整Available修订；歌曲输出超过音轨仍合法，GetAt/GetRange按原协议
返回OutsideRange／Values=null。作者若因此Report(Error)，仍失败并阻止导出。
未分析／失败的required实例继续初始化失败；optional明确收到不可用状态。
源分析接受通过一次工程历史命令；编译不因纯音频特征更改而重新编译源码。

显示名称取originalName；媒体后端定位仍使用受管实际路径。启动恢复的旧工程继续版本拒绝。
本批不新建脚本工作区、自动分析UI或模板资源目录；这些保持后续施工任务。
成片验证必须覆盖保存重开后的真实音频输入、历史／重放、MP4与透明画面同帧以及失败保护旧输出。

## 4. 出口证据

已实施本文数据结构、流式发布、严格加载、完整分析、历史与固定宿主投影；权威状态维护在roadmap §18.68。

### 源值和宿主入口

- MediaFileRevision：Hash／ByteLength／OriginalName／RelativePath。
- AudioAnalysisRevision：SourceHash／PcmHash／SampleCount／DurationUs／Revision／ContentHash／Points。
- ProjectAudioAnalysis：Data或DiagnosticCode／DiagnosticMessage／MissingStream；null与源存在性共同表达未分析／无源。
- AudioFeatureAnalysis.ToSourceData()只接受真实结果，SetAudioAnalysisCommand通过一次历史提交。
- CompiledProjectEffects.CompileAsync(Project,...)固定ProjectAudioProjection及ProjectEffectInputs.Fingerprint；旧ProjectEffects重载仍为无音频组合接缝。

成功分析文件version1，完整固定algorithm／sampleRate／hopSamples／originUs／sourceHash／pcmHash／sampleCount／durationUs／revision／points；每点必填timeUs／bassLevel／pulse／beatPhase（显式null）／beatConfidence。当前只读取唯一规范编码，数值保留double精度。

### 实施验证（0.16.0）

新增42项；八项目2628项全部通过、0失败／跳过，最终构建0警告／0错误。覆盖独立备份／另存为脱离原目录、坏资源／链接／限额拒绝、源身份和精度、失败保护旧入口／saved reference、分析历史；真实短尾解码和保存重开再分析、hop回调／历史／回退／帧率、required音轨外及optional真实状态均通过。实际保存重开后的C#输出与静态等价外观逐点相等，真实MP4／ProRes4444携带音轨并与500ms预览同帧，200ms作者Error保护旧产物；实际受管PNG／MP4按内容解码且透明输出无媒体。

最终构建／测试日志：/tmp/makokara-media-analysis-build-final.log、/tmp/makokara-media-analysis-tests-final-full.log。直接启动PID910561，niri350为0.16.0主窗口，351为旧工程版本提示；启动日志/tmp/makokara-f93b-media-analysis-final.log仅版本记录。没有新增脚本GUI或分析UI，自动组合、模板资源与工作区继续；大媒体保存／恢复沿用同步入口，后续R7需接入异步发布及取消交互。依赖未改，本轮无新的在线依赖审查证据。
