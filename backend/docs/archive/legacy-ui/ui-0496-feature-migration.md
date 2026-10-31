# 0.49.6 UI 功能迁移基线

日期：2026-10-10。实际施工起点 `000d6da`（设计 `01933a6`；产品 `78b7a55 / 0.49.6`）。本文件冻结原入口，供[重构计划](ui-0496-refactoring-plan.md) F-01～F-05 逐切片核对。所有入口当前保留；“新位置”是施工去向，不是已实现声明。

## 1. 逐入口清单

以下从起点的 AXAML 动作控件清点，包含无具名按钮、菜单、上下文菜单及模板内动作。可用条件列记录原绑定/静态条件；代码接线由对应视图的全部 partial 文件共同负责，不能把没有 XAML Click 属性误认为无功能。后续迁移必须同时阅读对应代码门禁与 TaskCommandContext，登记实际新控件和定向/Headless/原生证据，再退出旧入口。列表中 `沿用代码门禁` 是需要沿调用链复核的项目，不表示无门禁。

### AppearanceLyricsView

原入口：[AppearanceLyricsView.axaml](../../../samples/TechnicalSpike.App/Views/AppearanceLyricsView.axaml)；新位置：S2/S3 对象、预设与来源映射；布局动作转 S4。状态：0.49.11 已接通稳定行目标、多选和选择预设入口；其余作者/工坊路径仍待后续切片。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| ChoosePresetButton | {DynamicResource AppearanceChoosePreset} | IsEnabled=False；选择歌词行后打开预设面板 |
| MultiSelectCheck | {DynamicResource AppearanceMultiSelect} | 0.49.11：歌词行多选/单选模式 |
| SelectAllLinesButton / ClearLinesButton | {DynamicResource AppearanceSelectAll} / {DynamicResource AppearanceClearSelection} | 0.49.11：稳定行目标集合 |

### AppearanceWorkspaceView

原入口：[AppearanceWorkspaceView.axaml](../../../samples/TechnicalSpike.App/Views/AppearanceWorkspaceView.axaml)；新位置：S2/S3 对象、预设与来源映射；布局动作转 S4。状态：0.49.11 已接通对象多选、具名来源映射和正式候选试看；0.49.12接通复制当前应用到工坊、保存返回原对象与正式临时试看；0.49.13完成工坊基础外观首屏与资源库状态整理；0.49.14完成效果组件配置/验证/源码三页与修订工作流。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| MultiSelectObjectsCheck | {DynamicResource AppearanceMultiSelect} | 0.49.11：对象多选/单选模式 |
| SelectAllObjectsButton / ClearObjectsButton | {DynamicResource AppearanceSelectAll} / {DynamicResource AppearanceClearSelection} | 0.49.11：稳定对象目标集合 |
| IncludeDescendantsCheck | {DynamicResource AppearanceIncludeDescendants} | IsVisible=False；节点范围门禁保持 |
| RemoveApplicationButton | {DynamicResource AppearanceRemove} | 沿用代码门禁 |
| ToggleApplicationButton | {DynamicResource AppearanceDisable} | 沿用代码门禁 |
| UpdateApplicationButton | {DynamicResource AppearanceUpdate} | 沿用代码门禁 |
| CopyApplicationButton | {DynamicResource AppearanceCopyPreset} | 沿用代码门禁 |
| RefreshCatalogueButton | {DynamicResource AppearanceCatalogueRefresh} | 沿用代码门禁 |
| ApplyPresetButton | {DynamicResource WorkspaceApply} | 沿用代码门禁 |
| PreviewPresetButton | {DynamicResource AppearancePreviewButton} | 0.49.11：映射有效且目标非空时，将临时 Project 交给正式预览会话 |
| SegmentLayoutCheck | {DynamicResource AppearanceSegment} | 沿用代码门禁 |
| ApplyLayoutButton | {DynamicResource WorkspaceApply} | 沿用代码门禁 |
| OpenCompositionButton | {DynamicResource WorkspaceComposition} | 沿用代码门禁 |
| MakePresetButton | {DynamicResource WorkspaceTemplates} | 沿用代码门禁 |

### BranchSettingsWindow

原入口：[BranchSettingsWindow.axaml](../../../samples/TechnicalSpike.App/Views/BranchSettingsWindow.axaml)；新位置：S1 歌词与打轴 / 来源、文本、时间、播放。状态：入口保留，迁移待对应切片验收。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| NewTranslation | {DynamicResource BranchNewTranslation} | 沿用代码门禁 |
| RemoveBranch | {DynamicResource BranchRemove} | 沿用代码门禁 |
| CloseButton | {DynamicResource DialogCancel} | 沿用代码门禁 |
| SaveButton | {DynamicResource BranchSave} | 沿用代码门禁 |

### ColorPickerPanel

原入口：[ColorPickerPanel.axaml](../../../samples/TechnicalSpike.App/Views/ColorPickerPanel.axaml)；新位置：全局 / 工程生命周期、设置、主题与通用对话框（按切片细分）。状态：入口保留，迁移待对应切片验收。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| PresetWhite | {DynamicResource ColorPickerPresetWhite} | 沿用代码门禁 |
| PresetDark | {DynamicResource ColorPickerPresetDark} | 沿用代码门禁 |
| PresetRed | {DynamicResource ColorPickerPresetRed} | 沿用代码门禁 |
| PresetOrange | {DynamicResource ColorPickerPresetOrange} | 沿用代码门禁 |
| PresetYellow | {DynamicResource ColorPickerPresetYellow} | 沿用代码门禁 |
| PresetGreen | {DynamicResource ColorPickerPresetGreen} | 沿用代码门禁 |
| PresetCyan | {DynamicResource ColorPickerPresetCyan} | 沿用代码门禁 |
| PresetBlue | {DynamicResource ColorPickerPresetBlue} | 沿用代码门禁 |
| PresetPurple | {DynamicResource ColorPickerPresetPurple} | 沿用代码门禁 |
| PresetPink | {DynamicResource ColorPickerPresetPink} | 沿用代码门禁 |
| AdvancedButton | {DynamicResource ColorPickerAdvancedOpen} | 沿用代码门禁 |

### ColorSwatchPicker

原入口：[ColorSwatchPicker.axaml](../../../samples/TechnicalSpike.App/Views/ColorSwatchPicker.axaml)；新位置：全局 / 工程生命周期、设置、主题与通用对话框（按切片细分）。状态：入口保留，迁移待对应切片验收。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| SwatchButton | 图形/模板内容 | 沿用代码门禁 |

### ExportDialog

原入口：[ExportDialog.axaml](../../../samples/TechnicalSpike.App/Views/ExportDialog.axaml)；新位置：S9 导出。状态：入口保留，迁移待对应切片验收。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| BrowseButton | 图形/模板内容 | 沿用代码门禁 |
| CloseButton | 图形/模板内容 | 沿用代码门禁 |
| CancelButton | 图形/模板内容 | 沿用代码门禁 |
| StartButton | 图形/模板内容 | 沿用代码门禁 |

### ImportLyricsDialog

原入口：[ImportLyricsDialog.axaml](../../../samples/TechnicalSpike.App/Views/ImportLyricsDialog.axaml)；新位置：S1 歌词与打轴 / 来源、文本、时间、播放。状态：入口保留，迁移待对应切片验收。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| EstimateCheck | 图形/模板内容 | 沿用代码门禁 |
| ConfirmButton | 图形/模板内容 | 沿用代码门禁 |
| CancelButton | 图形/模板内容 | 沿用代码门禁 |

### LyricLineEditorWindow

原入口：[LyricLineEditorWindow.axaml](../../../samples/TechnicalSpike.App/Views/LyricLineEditorWindow.axaml)；新位置：S1 歌词与打轴 / 来源、文本、时间、播放。状态：入口保留，迁移待对应切片验收。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| PreviousLineButton | {DynamicResource LinePrevious} | 沿用代码门禁 |
| NextLineButton | {DynamicResource LineNext} | 沿用代码门禁 |
| CloseButton | {DynamicResource DialogClose} | IsVisible=False |
| AppendLyricsButton | {DynamicResource LyricsPasteAppend} | 沿用代码门禁 |
| ApplyLineTimeButton | {DynamicResource LineTimeApply} | 沿用代码门禁 |
| ShiftLineButton | {DynamicResource LineShiftButton} | 沿用代码门禁 |
| AuditionLineButton | {DynamicResource LineAudition} | 沿用代码门禁 |
| SplitTokenButton | {DynamicResource LineSplitToken} | 沿用代码门禁 |
| MergeTokenButton | {DynamicResource LineMergeToken} | 沿用代码门禁 |
| ApplyTokenTimeButton | {DynamicResource LineTimeApply} | 沿用代码门禁 |
| ClearTokenTimeButton | {DynamicResource TokenTimeClear} | 沿用代码门禁 |
| UndoButton | {DynamicResource ToolbarUndo} | 沿用代码门禁 |

### MainWindow

原入口：[MainWindow.axaml](../../../samples/TechnicalSpike.App/Views/MainWindow.axaml)；新位置：全局 / 工程生命周期、设置、主题与通用对话框（按切片细分）。状态：入口保留，迁移待对应切片验收。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| 未具名 1 | {DynamicResource MenuFile} | 沿用代码门禁 |
| 未具名 2 | {DynamicResource MenuProject} | 沿用代码门禁 |
| NewProjectMenuItem | {DynamicResource ToolbarNewProject} | InputGesture=Ctrl+N |
| OpenProjectMenuItem | {DynamicResource ToolbarOpenProject} | InputGesture=Ctrl+O |
| SaveProjectMenuItem | {Binding Label} | IsEnabled={Binding CanExecute}; DataContext={x:Null}; InputGesture={Binding Gesture} |
| SaveProjectAsMenuItem | {Binding Label} | IsEnabled={Binding CanExecute}; DataContext={x:Null}; InputGesture={Binding Gesture} |
| 未具名 7 | {DynamicResource MenuImport} | 沿用代码门禁 |
| ImportAudioMenuItem | {DynamicResource ToolbarImportAudio} | 沿用代码门禁 |
| ImportLyricsMenuItem | {DynamicResource ToolbarImportLyrics} | 沿用代码门禁 |
| ImportBackgroundMenuItem | {DynamicResource InspectorBackgroundImport} | 沿用代码门禁 |
| ImportBackgroundVideoMenuItem | {DynamicResource InspectorBackgroundImportVideo} | 沿用代码门禁 |
| ExportMenuItem | {DynamicResource MenuExport} | 沿用代码门禁 |
| ExportLyricsMenuItem | {Binding Label} | IsEnabled={Binding CanExecute}; DataContext={x:Null}; InputGesture={Binding Gesture} |
| ExportVideoMenuItem | {Binding Label} | IsEnabled={Binding CanExecute}; DataContext={x:Null}; InputGesture={Binding Gesture} |
| 未具名 15 | {DynamicResource MenuEdit} | 沿用代码门禁 |
| UndoMenuItem | {DynamicResource ToolbarUndo} | InputGesture=Ctrl+Z |
| RedoMenuItem | {DynamicResource ToolbarRedo} | InputGesture=Ctrl+Shift+Z |
| 未具名 18 | {DynamicResource MenuView} | 沿用代码门禁 |
| LyricsSourceMenuItem | {DynamicResource PanelLyricsSources} | 沿用代码门禁 |
| AppearanceLyricsMenuItem | {DynamicResource AppearanceLyricsTitle} | 沿用代码门禁 |
| ApplicationObjectsMenuItem | {DynamicResource AppearanceApplicationObjects} | 沿用代码门禁 |
| PresetLibraryMenuItem | {DynamicResource PanelPresetLibrary} | 沿用代码门禁 |
| LayoutPanelMenuItem | {DynamicResource AppearanceLayout} | 沿用代码门禁 |
| OutputPanelMenuItem | {DynamicResource InspectorComposition} | 沿用代码门禁 |
| 未具名 25 | {DynamicResource MenuTheme} | 沿用代码门禁 |
| ThemeSystemMenuItem | {DynamicResource ThemeSystem} | 沿用代码门禁 |
| ThemeLightMenuItem | {DynamicResource ThemeLight} | 沿用代码门禁 |
| ThemeDarkMenuItem | {DynamicResource ThemeDark} | 沿用代码门禁 |
| ThemePaletteMenuItem | {DynamicResource ThemePaletteMenu} | 沿用代码门禁 |
| 未具名 30 | {DynamicResource MenuHelp} | 沿用代码门禁 |
| AboutMenuItem | {DynamicResource MenuAbout} | 沿用代码门禁 |
| HomeButton | {DynamicResource WorkspaceHome} | 沿用代码门禁 |
| QuickSaveButton | {Binding Label} | IsEnabled={Binding CanExecute}; DataContext={x:Null} |
| QuickExportButton | {Binding Label} | IsEnabled={Binding CanExecute}; DataContext={x:Null} |
| LyricsTaskButton | {DynamicResource WorkspaceLyrics} | 沿用代码门禁 |
| TemplateTaskButton | {DynamicResource WorkspaceTemplates} | 沿用代码门禁 |
| AppearanceTaskButton | {DynamicResource WorkspaceAppearance} | 沿用代码门禁 |
| CloseLyricsSourceButton | × | 沿用代码门禁 |
| OpenBranchSettings | {DynamicResource BranchSettings} | 沿用代码门禁 |
| QuickImportAudioButton | {DynamicResource ToolbarImportAudio} | 沿用代码门禁 |
| LyricsSourceButton | {DynamicResource PanelLyricsSources} | 沿用代码门禁 |
| QuickImportLyricsButton | {DynamicResource ToolbarImportLyrics} | 沿用代码门禁 |
| PasteLyricsButton | {DynamicResource LyricsPaste} | 沿用代码门禁 |
| ToggleLyricPreviewButton | {DynamicResource LyricNeutralPreviewToggle} | 沿用代码门禁 |
| AddLineButton | {DynamicResource ToolbarAddLine} | 沿用代码门禁 |
| MoreLineActionsButton | {DynamicResource ToolbarMore} | 沿用代码门禁 |
| MoreRemoveLineButton | {DynamicResource ToolbarRemoveLine} | 沿用代码门禁 |
| MoreDuplicateLineButton | {DynamicResource ToolbarDuplicateLine} | 沿用代码门禁 |
| MoreSplitButton | {DynamicResource ToolbarSplit} | 沿用代码门禁 |
| MoreMergeButton | {DynamicResource ToolbarMerge} | 沿用代码门禁 |
| MoreTokenSplitButton | {DynamicResource ToolbarTokenSplit} | 沿用代码门禁 |
| RemoveLineButton | 图形/模板内容 | 沿用代码门禁 |
| DuplicateLineButton | 图形/模板内容 | 沿用代码门禁 |
| SplitButton | 图形/模板内容 | 沿用代码门禁 |
| MergeButton | 图形/模板内容 | 沿用代码门禁 |
| OpenTokenSplitButton | 图形/模板内容 | 沿用代码门禁 |
| 未具名 57 | {DynamicResource LyricRowToggleTip} | 沿用代码门禁 |
| AppearanceLyricsButton | {DynamicResource AppearanceLyricsTitle} | 沿用代码门禁 |
| ToggleInspectorButton | {DynamicResource AppearanceApplicationObjects} | 沿用代码门禁 |
| PresetLibraryButton | {DynamicResource PanelPresetLibrary} | 沿用代码门禁 |
| LayoutPanelButton | {DynamicResource AppearanceLayout} | 沿用代码门禁 |
| OutputPanelButton | {DynamicResource InspectorComposition} | 沿用代码门禁 |
| CloseAppearancePanelButton | × | 沿用代码门禁 |
| OpenAppearanceWorkspaceButton | {DynamicResource WorkspaceAppearance} | 沿用代码门禁 |
| ClearBackgroundButton | {DynamicResource InspectorBackgroundClear} | 沿用代码门禁 |
| UseAudioDurationButton | {DynamicResource InspectorDurationFromAudio} | 沿用代码门禁 |
| UseLyricsDurationButton | {DynamicResource InspectorDurationFromLyrics} | 沿用代码门禁 |
| 未具名 68 | {DynamicResource StatusShortHint} | 沿用代码门禁 |
| DiagnosticsButton | {DynamicResource WorkspaceDiagnostics} | 沿用代码门禁 |

### NewProjectDialog

原入口：[NewProjectDialog.axaml](../../../samples/TechnicalSpike.App/Views/NewProjectDialog.axaml)；新位置：全局 / 工程生命周期、设置、主题与通用对话框（按切片细分）。状态：入口保留，迁移待对应切片验收。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| ChooseAudioButton | {DynamicResource ToolbarImportAudio} | 沿用代码门禁 |
| ChooseLyricsButton | {DynamicResource ToolbarImportLyrics} | 沿用代码门禁 |
| CancelButton | 图形/模板内容 | 沿用代码门禁 |
| OkButton | 图形/模板内容 | 沿用代码门禁 |

### PlaybackBarView

原入口：[PlaybackBarView.axaml](../../../samples/TechnicalSpike.App/Views/PlaybackBarView.axaml)；新位置：S1 歌词与打轴 / 来源、文本、时间、播放。状态：入口保留，迁移待对应切片验收。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| PlayButton | {DynamicResource ToolbarPlay} | 沿用代码门禁 |
| RetryAudioButton | {DynamicResource AudioRetry} | IsVisible={Binding CanRetryAudio}; Click=OnRetryAudioClicked |
| LoopInButton | {DynamicResource PlaybackLoopIn} | 沿用代码门禁 |
| LoopOutButton | {DynamicResource PlaybackLoopOut} | 沿用代码门禁 |
| ClearLoopButton | {DynamicResource PlaybackLoopClear} | 沿用代码门禁 |
| HelpButton | {DynamicResource TimelineHelp} | 沿用代码门禁 |

### SaveProjectDialog

原入口：[SaveProjectDialog.axaml](../../../samples/TechnicalSpike.App/Views/SaveProjectDialog.axaml)；新位置：全局 / 工程生命周期、设置、主题与通用对话框（按切片细分）。状态：入口保留，迁移待对应切片验收。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| BrowseButton | 图形/模板内容 | 沿用代码门禁 |
| CancelButton | 图形/模板内容 | 沿用代码门禁 |
| OkButton | 图形/模板内容 | 沿用代码门禁 |

### ScriptConfigurationWorkspaceWindow

原入口：[ScriptConfigurationWorkspaceWindow.axaml](../../../samples/TechnicalSpike.App/Views/ScriptConfigurationWorkspaceWindow.axaml)；新位置：S7 效果组件 / 配置、验证、源码及库。状态：0.49.14 已接通配置、验证、源码三页、空包入口、按固定修订缓存编译结果及明确采用同包新修订；保存、编译、采用和正式验证仍保持独立门禁。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| UndoButton | {DynamicResource ToolbarUndo} | IsEnabled={Binding CanUndo} |
| RedoButton | {DynamicResource ToolbarRedo} | IsEnabled={Binding CanRedo} |
| NewButton | {DynamicResource ScriptWorkspaceNew} | IsEnabled={Binding CanInteract} |
| RefreshLibraryButton | {DynamicResource LibraryRefresh} | IsEnabled={Binding CanRefreshLibrary} |
| SaveCopyButton | {Binding Label} | IsEnabled={Binding CanExecute}; IsVisible={Binding IsVisible}; DataContext={x:Null} |
| SaveAndTryButton | 保存并试用 | 0.49.12：原草稿返回身份＋共用保存门禁，已保存同草稿不重复写库 |
| ReturnToTargetButton | 返回原对象 | 0.49.12：原作者离开确认，取消/放弃/目标过期保护 |
| DeleteButton | {DynamicResource ScriptWorkspaceDelete} | IsEnabled={Binding CanDelete} |
| ImportPackageButton | {DynamicResource ScriptWorkspaceImportPackage} | IsEnabled={Binding CanImportPackage} |
| ImportArchiveButton | {DynamicResource ScriptWorkspaceImportArchive} | IsEnabled={Binding CanImportPackage} |
| 未具名 9 | {DynamicResource ScriptParameterResetAll} | Command={Binding ParameterPanel.ResetAllCommand} |
| SaveSourceRevisionButton | {Binding Label} | IsEnabled={Binding CanExecute}; DataContext={x:Null} |
| DiscardSourceButton | {DynamicResource ScriptWorkspaceSourceDiscard} | IsEnabled={Binding SourceEditor.HasUnsavedChanges} |
| CompileSourceButton | {DynamicResource ScriptWorkspaceSourceCompile} | 0.49.14：按当前草稿/固定修订切换文案；IsEnabled={Binding SourceEditor.CanCompile} |
| CancelSourceCompileButton | {DynamicResource ScriptWorkspaceSourceCancel} | IsEnabled={Binding SourceEditor.IsBusy} |
| ShareSourcePackageButton | {DynamicResource ScriptWorkspaceSourceShare} | IsEnabled={Binding SourceEditor.HasPackage} |
| CancelRuntimeButton | {DynamicResource ScriptRuntimeCancel} | IsVisible={Binding Runtime.IsBusy} |
| CloseButton | {DynamicResource DialogClose} | 沿用代码门禁 |
| SaveButton | {Binding Label} | IsEnabled={Binding CanExecute}; DataContext={x:Null} |

### ScriptParameterFieldView

原入口：[ScriptParameterFieldView.axaml](../../../samples/TechnicalSpike.App/Views/ScriptParameterFieldView.axaml)；新位置：S7 效果组件 / 配置、验证、源码及库。状态：0.49.14 随配置页接入三页工作区；原参数类型、精度、重置和非法草稿门禁保持。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| 未具名 1 | {DynamicResource ScriptParameterReset} | Command={Binding ResetCommand} |
| BooleanBox | {DynamicResource ScriptParameterEnabled} | IsVisible={Binding IsBoolean} |
| 未具名 3 | {DynamicResource ScriptParameterRemovePoint} | IsVisible={Binding CanRemove}; Command={Binding RemoveCommand} |
| 未具名 4 | {DynamicResource ScriptParameterAddPoint} | IsVisible={Binding CanAddPoint}; Command={Binding AddPointCommand} |

### ScriptRuntimeValidationView

原入口：[ScriptRuntimeValidationView.axaml](../../../samples/TechnicalSpike.App/Views/ScriptRuntimeValidationView.axaml)；新位置：S7 效果组件 / 配置、验证、源码及库。状态：0.49.14 独立为验证页并显式绑定原 Runtime；保存配置、源码采用和目标选择后才能运行，旧报告过期规则保持。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| RuntimeRunButton | {DynamicResource ScriptRuntimeRun} | IsEnabled={Binding CanRun} |
| RuntimeCancelButton | {DynamicResource ScriptRuntimeCancel} | IsEnabled={Binding IsBusy} |
| RuntimePreviousIssues | {DynamicResource EffectWorkspaceDiagnosticPrevious} | IsEnabled={Binding CanPreviousIssuePage} |
| RuntimeNextIssues | {DynamicResource EffectWorkspaceDiagnosticNext} | IsEnabled={Binding CanNextIssuePage} |

### StartPageView

原入口：[StartPageView.axaml](../../../samples/TechnicalSpike.App/Views/StartPageView.axaml)；新位置：全局 / 工程生命周期、设置、主题与通用对话框（按切片细分）。状态：入口保留，迁移待对应切片验收。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| NewButton | {DynamicResource ToolbarNewProject} | 沿用代码门禁 |
| OpenButton | {DynamicResource ToolbarOpenProject} | 沿用代码门禁 |
| TemplatesButton | {DynamicResource HomeTemplates} | 沿用代码门禁 |
| RecoveryButton | {DynamicResource HomeRecovery} | 沿用代码门禁 |
| 未具名 5 | {DynamicResource ToolbarOpenProject} | 沿用代码门禁 |

### StrokeSpecEditorView

原入口：[StrokeSpecEditorView.axaml](../../../samples/TechnicalSpike.App/Views/StrokeSpecEditorView.axaml)；新位置：S6 工坊 / 外观、材质、效果及资源库。状态：0.49.13基础外观首屏与库状态已接通，保存/返回底栏和换行动作保持；效果作者继续整理。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| EnabledBox | {DynamicResource DesignStrokeEffect} | 沿用代码门禁 |

### StyleGradientEditorView

原入口：[StyleGradientEditorView.axaml](../../../samples/TechnicalSpike.App/Views/StyleGradientEditorView.axaml)；新位置：S6 工坊 / 外观、材质、效果及资源库。状态：0.49.13基础外观首屏与库状态已接通，保存/返回底栏和换行动作保持；效果作者继续整理。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| AddStop | {DynamicResource WorkspaceGradientAddStop} | 沿用代码门禁 |
| ClearGradient | {DynamicResource WorkspaceGradientClear} | 沿用代码门禁 |

### StylePropertyRowView

原入口：[StylePropertyRowView.axaml](../../../samples/TechnicalSpike.App/Views/StylePropertyRowView.axaml)；新位置：S6 工坊 / 外观、材质、效果及资源库。状态：0.49.13基础外观首屏与库状态已接通，保存/返回底栏和换行动作保持；效果作者继续整理。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| WriteCheck | {DynamicResource WorkspaceWriteProperty} | IsEnabled={Binding IsSupportedForTarget} |
| BooleanBox | {DynamicResource DesignEnabled} | IsEnabled={Binding CanEditValue}; IsVisible={Binding IsBoolean} |
| ClearColor | {DynamicResource DesignDisableEffect} | IsEnabled={Binding CanEditValue}; IsVisible={Binding IsOptionalColor} |

### StyleTargetPickerDialog

原入口：[StyleTargetPickerDialog.axaml](../../../samples/TechnicalSpike.App/Views/StyleTargetPickerDialog.axaml)；新位置：S6 工坊 / 外观、材质、效果及资源库。状态：0.49.13基础外观首屏与库状态已接通，保存/返回底栏和换行动作保持；效果作者继续整理。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| SelectAll | {DynamicResource WorkspacePickerAll} | 沿用代码门禁 |
| ClearAll | {DynamicResource WorkspacePickerClear} | 沿用代码门禁 |
| CancelButton | {DynamicResource DialogCancel} | 沿用代码门禁 |
| OkButton | {DynamicResource DialogConfirm} | 沿用代码门禁 |

### TemplateCreationDialog

原入口：[TemplateCreationDialog.axaml](../../../samples/TechnicalSpike.App/Views/TemplateCreationDialog.axaml)；新位置：S6 工坊 / 外观、材质、效果及资源库。状态：0.49.13基础外观首屏与库状态已接通，保存/返回底栏和换行动作保持；效果作者继续整理。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| CancelButton | {DynamicResource DialogCancel} | 沿用代码门禁 |
| CreateButton | {DynamicResource TemplateNew} | 沿用代码门禁 |

### TemplateWorkshopWindow

原入口：[TemplateWorkshopWindow.axaml](../../../samples/TechnicalSpike.App/Views/TemplateWorkshopWindow.axaml)；新位置：S6 工坊 / 外观、材质、效果及资源库。状态：0.49.13基础外观首屏与库状态已接通，保存/返回底栏和换行动作保持；效果作者继续整理。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| LibraryButton | {DynamicResource PanelPresetLibrary} | 沿用代码门禁 |
| PropertiesButton | {DynamicResource PanelStyleEditor} | 沿用代码门禁 |
| ScriptWorkspaceButton | {DynamicResource ScriptWorkspaceOpen} | 沿用代码门禁 |
| CloseButton | {DynamicResource DialogClose} | 沿用代码门禁 |
| NewButton | {DynamicResource TemplateNew} | IsEnabled={Binding CanEditLibrary} |
| ImportTemplatesButton | {DynamicResource TemplateTransferImport} | IsEnabled={Binding CanChooseTemplateImport} |
| RefreshLibraryButton | {DynamicResource LibraryRefresh} | IsEnabled={Binding CanRefreshLibrary} |
| SaveCopyButton | {Binding Label} | IsEnabled={Binding CanExecute}; IsVisible={Binding IsVisible}; DataContext={x:Null} |
| MultiLineSampleCheck | {DynamicResource TemplateSampleMultiple} | 沿用代码门禁 |
| SamplePlayButton | {DynamicResource TemplateSamplePlay} | 沿用代码门禁 |
| SampleRestartButton | {DynamicResource TemplateSampleRestart} | 沿用代码门禁 |
| SampleLoopCheck | {DynamicResource TemplateSampleLoop} | 沿用代码门禁 |
| ImportPackageButton | {DynamicResource TemplateMaterialImport} | IsEnabled={Binding CanImportPackage} |
| ImportArchiveButton | {DynamicResource TemplateMaterialImportArchive} | IsEnabled={Binding CanImportPackage} |
| ApplyScriptConfigurationButton | {DynamicResource ScriptWorkspaceApplyToTemplate} | IsEnabled={Binding CanEditDraft} |
| AddEffectRecipeButton | {DynamicResource TemplateEffectAdd} | IsEnabled={Binding EffectRecipeEditor.CanAdd} |
| UpdateEffectRecipeButton | {DynamicResource TemplateEffectUpdate} | IsEnabled={Binding EffectRecipeEditor.CanUpdate} |
| CancelEffectRecipeEditButton | {DynamicResource TemplateEffectCancelEdit} | IsEnabled={Binding EffectRecipeEditor.CanCancelEdit} |
| 未具名 19 | {DynamicResource AppearanceEnabled} | 沿用代码门禁 |
| 未具名 20 | {DynamicResource TemplateEffectEdit} | 沿用代码门禁 |
| 未具名 21 | {DynamicResource TemplateEffectMoveUp} | 沿用代码门禁 |
| 未具名 22 | {DynamicResource TemplateEffectMoveDown} | 沿用代码门禁 |
| 未具名 23 | {DynamicResource TemplateEffectRemove} | 沿用代码门禁 |
| UndoDraftButton | {DynamicResource ToolbarUndo} | IsEnabled={Binding CanUndoDraft} |
| RedoDraftButton | {DynamicResource ToolbarRedo} | IsEnabled={Binding CanRedoDraft} |
| ShareTemplateButton | {DynamicResource TemplateTransferShare} | IsEnabled={Binding CanChooseTemplateShare} |
| SaveButton | {Binding Label} | IsEnabled={Binding CanExecute}; DataContext={x:Null} |
| DeleteButton | {DynamicResource TemplateDelete} | IsEnabled={Binding CanDelete} |

### TextInputDialog

原入口：[TextInputDialog.axaml](../../../samples/TechnicalSpike.App/Views/TextInputDialog.axaml)；新位置：全局 / 工程生命周期、设置、主题与通用对话框（按切片细分）。状态：入口保留，迁移待对应切片验收。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| CancelButton | 图形/模板内容 | 沿用代码门禁 |
| OkButton | 图形/模板内容 | 沿用代码门禁 |

### ThemePaletteDialog

原入口：[ThemePaletteDialog.axaml](../../../samples/TechnicalSpike.App/Views/ThemePaletteDialog.axaml)；新位置：全局 / 工程生命周期、设置、主题与通用对话框（按切片细分）。状态：入口保留，迁移待对应切片验收。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| ResetButton | {DynamicResource ThemePaletteReset} | 沿用代码门禁 |
| CancelButton | {DynamicResource DialogCancel} | 沿用代码门禁 |
| ApplyButton | {DynamicResource ThemePaletteApply} | 沿用代码门禁 |

### TokenSplitDialog

原入口：[TokenSplitDialog.axaml](../../../samples/TechnicalSpike.App/Views/TokenSplitDialog.axaml)；新位置：S1 歌词与打轴 / 来源、文本、时间、播放。状态：入口保留，迁移待对应切片验收。

| 原控件/入口 | 原文案/内容 | 原条件与接线线索 |
|---|---|---|
| MergeButton | 图形/模板内容 | 沿用代码门禁 |
| ClearButton | 图形/模板内容 | 沿用代码门禁 |
| EstimateCheck | 图形/模板内容 | 沿用代码门禁 |
| ApplyLineButton | 图形/模板内容 | 沿用代码门禁 |
| ApplyAllButton | 图形/模板内容 | 沿用代码门禁 |
| CloseButton | 图形/模板内容 | 沿用代码门禁 |

## 2. 动态动作与快捷键

这些入口没有独立 AXAML 名称，不能仅靠上表验收。原调用位置固定为起点文件行号。新位置随所属视图迁移；底层命令、菜单项适用条件、确认、输入准备与一次撤销保持。

| 原代码入口 | 新位置 |
|---|---|
| `samples/TechnicalSpike.App/Views/MainWindow.Feedback.cs:37` · `var button = new Button { Content = buttons[index], MinWidth = UiControlMetrics.ActionMinWidth, Classes = { "Outlined" } };` | 全局 / 工程生命周期、设置、主题与通用对话框（按切片细分） |
| `samples/TechnicalSpike.App/Views/SceneEditorControl.Dialogs.cs:140` · `var checks = children.Select(node => new CheckBox { Content = NodeLabel(node.Id), Tag = node.Id, IsChecked = layout.SlotIds.Contains(node.Id) }).ToArr` | S5 自定义布局 / 结构、歌词分配、时间检查 |
| `samples/TechnicalSpike.App/Views/SceneEditorControl.Generation.cs:28` · `var opening=new CheckBox { Content=Loc.Get("LeadInIncludeOpening"),IsChecked=current.IncludeOpening };` | S5 自定义布局 / 结构、歌词分配、时间检查 |
| `samples/TechnicalSpike.App/Views/SceneEditorControl.Generation.cs:29` · `var gaps=new CheckBox { Content=Loc.Get("LeadInIncludeGaps"),IsChecked=current.IncludeLongGaps };` | S5 自定义布局 / 结构、歌词分配、时间检查 |
| `samples/TechnicalSpike.App/Views/SceneEditorControl.Generation.cs:35` · `var enabled=new CheckBox { Content=branch.Name,IsEnabled=supported,IsChecked=supported && program.BranchId==SourceBranch };` | S5 自定义布局 / 结构、歌词分配、时间检查 |
| `samples/TechnicalSpike.App/Views/SceneEditorControl.Generation.cs:102` · `var check=new CheckBox { IsEnabled=actionable,IsChecked=candidate.Status is LeadInCandidateStatus.New or LeadInCandidateStatus.Update };` | S5 自定义布局 / 结构、歌词分配、时间检查 |
| `samples/TechnicalSpike.App/Views/SceneEditorControl.LeadIns.cs:53` · `void Add(string key,Action action,bool danger=false) { var item = new MenuItem { Header=Loc.Get(key) }; if (danger) item.Classes.Add("Danger"); item.C` | S5 自定义布局 / 结构、歌词分配、时间检查 |
| `samples/TechnicalSpike.App/Views/SceneEditorControl.LeadIns.cs:99` · `var enabled=new CheckBox { Content=Loc.Get("LeadInEnabled"),IsChecked=cue.Enabled }; int generation=_formGeneration;` | S5 自定义布局 / 结构、歌词分配、时间检查 |
| `samples/TechnicalSpike.App/Views/SceneEditorControl.Navigation.cs:74` · `var edit = new MenuItem { Header = Loc.Get("SceneEditMembers") }; edit.Click += (_,_) => OpenClipDialog(clip); menu.Items.Add(edit);` | S5 自定义布局 / 结构、歌词分配、时间检查 |
| `samples/TechnicalSpike.App/Views/SceneEditorControl.Navigation.cs:75` · `var container = new MenuItem { Header = Loc.Get("SceneContainer") }; container.Click += (_,_) => _editor.SelectSceneNode(clip.ContainerId); menu.Items` | S5 自定义布局 / 结构、歌词分配、时间检查 |
| `samples/TechnicalSpike.App/Views/SceneEditorControl.Navigation.cs:76` · `var delete = new MenuItem { Header = Loc.Get("SceneDeleteClip") }; delete.Classes.Add("Danger"); delete.Click += (_,_) => DeleteClip(clip); menu.Items` | S5 自定义布局 / 结构、歌词分配、时间检查 |
| `samples/TechnicalSpike.App/Views/SceneEditorControl.Tree.cs:71` · `{ var item = new MenuItem { Header = Loc.Get(key), IsEnabled = enabled }; if (danger) item.Classes.Add("Danger"); if (hint is not null) ToolTip.SetTip` | S5 自定义布局 / 结构、歌词分配、时间检查 |
| `samples/TechnicalSpike.App/Views/SceneEditorControl.cs:170` · `var button = new Button { Content = Loc.Get(key), Margin = new(0,2), Padding = UiControlMetrics.CompactPadding,` | S5 自定义布局 / 结构、歌词分配、时间检查 |
| `samples/TechnicalSpike.App/Views/SceneEditorControl.cs:282` · `{ var menu = new ContextMenu(); foreach (var choice in choices) { var item = new MenuItem { Header = choice.Label }; item.Click += (_,_) => selected(c` | S5 自定义布局 / 结构、歌词分配、时间检查 |

键盘入口另核对 `MainWindow.Keyboard.cs`、`LyricLineEditorWindow.Keyboard.cs`、`SceneEditorControl` 的键盘/拖动接线，以及 `TaskCommandContext`、工坊/组件 `Commands`；键盘打轴、播放/循环、撤销/重做、保存/另存必须与按钮和菜单共同验证。

起点统计：**210 个 AXAML 动作入口、14 处动态动作构造**。此计数是清点证据，不是全部功能验收通过数量。

## 3. 功能合同与迁移出口

| 能力/原用例 | 新入口及保留条件 | 验证出口 |
|---|---|
| 工程新建/打开/最近/恢复/保存/另存/关闭 | 全局及首页；必需音频与歌词、可读字体、失败候选、脏状态、关闭检查 | ProjectCreationTests / NewProjectPreparation 用例 / 保存与恢复用例；原生两入口与保存重开 |
| 导入/追加/分支与翻译关联/歌词导出 | S1 来源管理及文件菜单；格式、明确目标、损失确认、迟到结果保护 | 歌词导入/源编辑定向；--lyrics / --feedback |
| 行文字/注音/拆合/清除/平移/时间轴/键盘打轴 | S1 原全部能力；微秒精度、非法草稿、组提交与一次撤销 | 源编辑/时间编辑定向；--token-refresh / --lyrics；物理 IME 待人工 |
| 播放/定位/试听/循环/吸附/波形/预览质量 | 共享播放与 S1/S2 预览；选择与播放头独立，中性和正式身份分别标明 | --playback；实际试听待人工 |
| 工程/来源/行/单元/片段/节点、启停/移除/更新/映射 | S2/S3；稳定 ID、原兼容门禁、固定副本、身份/种子及多选历史 | PresetApplicationSelectionTests / PresetApplicationAppearanceTests；--applications / --appearance-refresh |
| 候选临时视觉试看与作者返回 | S3 与 S6 返回上下文；正式临时快照、取消/迟到/释放、原目标验证 | 0.49.12：保存并试用/返回原对象已接通，精确保存候选交给正式临时会话；删除目标、同ID换工程、冲突和作者切换均拒绝旧返回；工坊整页与效果作者继续 |
| 四种布局/全部或连续段/安全拆分 | S4 独立范围；保留未选、一次应用/撤销、无变化不提交 | BasicLayoutTests；--layout |
| 树/容器/槽位/片段绑定/前奏/生成/变换/重挂/拖动 | S5 三页；保留草稿门禁及脚本关闭标记 | SceneEditorTests / SceneTreeInteractionTests；--composition / --panels |
| 全部静态外观/注音外观/渐变/描边/阴影/透明度/材质 | S6 基础外观及相关高级组；保持原属性支持门禁、作者历史及固定试样 | 样式/工坊定向；--templates / --authoring / --bindings |
| 内置/个人库、新建/复制/导入/导出/删除/刷新 | S6/S7 库菜单；内置只读、作者保存与工程应用分离、闭包保持 | --library / --package-import / --draft / --task-commands |
| 包/归档、包内效果/材质、参数/精确值/重置/图片/曲线/RGBA/JSON、目标/范围/输入/种子/叠加 | S7 配置及高级组；不降低门禁或精度 | ScriptParameterPanelTests / ScriptConfigurationWorkspaceTests；--parameters / --bindings |
| 源码文件/保存/放弃/编译/取消/采用/分享/正式验证/报告 | S7 源码与验证；修订状态独立、改参数结果失效、保存加入明确父草稿 | 源码/正式验证定向；--scripts / --diagnostics；失败取消与完整报告 |
| 音频替换、图片/视频选择/替换/移除/适配/播放策略、画布/帧率/结束取值 | S8 分组及文件菜单；原媒体保护和一次可撤销取值 | 媒体/工程设置定向；--background-video / --audio |
| 普通/透明输出、质量/路径/依赖/进度/取消/失败/重试/旧片保护 | S9；允许探测入口与编码门禁分开，设置往返保留草稿 | ExportStateTests / 导出入口合同 / ExportStateTests；实际输出仅在受影响切片执行 |
| 深/浅/系统主题、强调色、帮助/关于、弹层、焦点 | 全局及主题菜单；theme.json 独立，Suki 模板/实底/位移收起保持 | UiSukiThemeTests / UiThemeSettingsTests / ControlWiringTests；--popup-motion；平台/IME 待人工 |

## 4. 第 1 切片实际落地

0.49.7 集中 4/8/12/16/24 间距；画面与工坊 AXAML 及画面动态行间距消费同源值。共用反馈先接工程保存与画面预设操作；保存成功只在底栏显示，约 4.5 秒收起；缺项/错误/进行中持续，旧回调不能清除新消息；取消预设确认保持原反馈；未解决错误按任务/稳定目标隔离，返回原任务仍保留，换工程重置。其余页面反馈、完整重排、真实试看和制作返回仍按后续切片推进。

验证与当前限制见[实施记录](ui-0496-refactoring-progress.md)。

<a id="shell-commands"></a>
## 5. 第 2 步外壳切片的命令去向（0.49.8）

以下只展开本批实际消费的命令，§1/§2 的冻结基线保持原样；其他页面继续按对应切片审查，不把本表当成全功能验收。

| 原入口 | 本批入口与执行目标 | 实际门禁与输入准备 | 验证 |
|---|---|---|---|
| LyricsTaskButton / AppearanceTaskButton / TemplateTaskButton | 歌词与打轴、画面与编排相邻；预设工坊在资源组 | 工程任务须有活动工程；工坊可独立进入；原 NavigateTask、编排门禁和历史保持 | --shell：实际三任务切换、长名、深浅主题和宽窄状态 |
| SaveStateLabel | 全局工程名或无工程的“个人预设库”；ProjectSaveStatus 独立显示工程保存状态 | _hasActiveProject 决定身份；工程名省略时保存状态仍显示，完整名称有提示 | --shell：工程/作者身份、无工程和长名 |
| QuickSaveButton | 全局“保存工程” → _projectCommands.Save | TaskCommandsAvailable、有工程、非首页、未导出；共用 CommitTaskInputs、RunProjectOperationAsync、原快照保存服务 | --task-commands：作者非法时实际工程写盘；--shell：新工程选择保存位置及取消 |
| SaveProjectMenuItem / Ctrl+S | 文件菜单的活动任务保存；工坊“保存预设”，工程任务“保存工程” | CurrentTaskCommands，共用各作者输入准备/校验及忙碌门禁；保存不放在“工程”子菜单内 | --task-commands：菜单/按钮/焦点文本框快捷键、非法草稿、关闭确认和独立脚本作者 |
| SaveProjectAsMenuItem / Ctrl+Shift+S | 仍只在工程任务另存工程；工坊隐藏且不执行 | 活动上下文的 SaveAs；原位置/覆盖确认保持 | --task-commands：真实工程另存、工坊拒绝快捷键落入普通保存 |
| QuickExportButton / ExportVideoMenuItem | 工坊内仍可打开工程导出；_projectCommands.ExportVideo | 有工程、命令可用、有 VideoExportEngine、未导出且无 EffectPreviewFailure；入口不依赖 CanExport，开始编码仍走原门禁 | --task-commands：新探测、缺少依赖、两条工坊入口、取消及释放后重入；ExportStateTests |
| ExportLyricsMenuItem | 全局工程歌词导出；_projectCommands.ExportLyrics | 有工程、命令可用、未导出；原文件选择/损失确认/过期工程检查保留 | --task-commands：工坊真实请求文件选择及取消；本批未重新导出 LRC 文件 |
| WorkshopHint / 原试样内作者脏标记 | 工坊标题区 WorkshopAuthorName / WorkshopAuthorStatus，作者保存仍在工坊操作区 | 使用原作者 Name/DirtyStatus；只读/个人/新草稿状态保留在试样区，不复制作者模型或历史 | --shell：收起属性面板后长名称与作者状态仍可见 |

本批不重排歌词表单或更改时间提交语义；预设返回应用、工程设置独立入口、效果组件工作页仍按后续切片实施。

<a id="lyrics-commands"></a>
## 6. 歌词切片实际入口（0.49.9）

§1保留原冻结清单，以下登记已迁移的实际入口。分组按钮只切换编辑组，不提交数据；作者/导出/高级能力继续原合同。

| 原入口 | 当前入口与门禁 | 验证入口 |
|---|---|---|
| LyricsSourcePanel / BranchSelector / OpenBranchSettings | 行列表顶部单行来源区；单来源名称、多来源选择器及“管理”，仍调用SelectBranch和原设置对话框 | --lyrics-drafts、接线回归；来源领域定向 |
| QuickImportAudioButton / CloseLyricsSourceButton | 来源更多菜单；音频替换文件菜单保持，独立工程设置未由本批完成 | 控件接线与原命令；不重复实际媒体替换验收 |
| SourceLineText / RubyTextBox | 中央滚动正文、注音默认展开；原即时提交/合并撤销保持 | --lyrics-drafts、--token-refresh、--lyrics |
| TokenHost / SplitTokenButton / MergeTokenButton | 正文文字序列与拆合组；保留稳定卡片、键盘和真实指针重排，先检查时间草稿 | --lyrics-drafts、--token-refresh、拆合领域定向 |
| LineStartBox / LineEndBox / ApplyLineTimeButton | 独立时间区的“本行时间”；旧按钮退出，Enter/离开整组提交，Esc恢复；跟随主来源的行仍禁止独立时间改写 | LyricsTimeDraftTests、--lyrics-drafts；旧作者连续链改为实际Enter |
| TokenStartBox / TokenEndBox / ApplyTokenTimeButton | 同一时间区的“所选文字时间”；旧按钮退出，两端共用原包含/邻字/时长门禁 | 时间/精度领域定向、--lyrics-drafts |
| ClearTokenTimeButton | 所选文字时间内清除；先检查草稿，清除后同步空值，迟到回调不能写回 | --lyrics-drafts |
| ShiftLineStartBox / ShiftLineButton | 正文折叠平移组；原预演、完整时间变化清单、过期工程检查及确认保持 | 原平移领域定向；--lyrics-drafts验证非法时间阻止确认、布局不推走时间区，未扩大实际原生平移确认验收 |
| PasteLyricsText / AppendLyricsButton / PasteLyricsExpander | 正文折叠粘贴组，顶部原粘贴入口展开/聚焦；原离开检查保持 | 导入/追加领域定向、接线；--lyrics-drafts长正文 |
| PreviousLineButton / NextLineButton / AuditionLineButton / UndoButton | 时间区旁常驻；选择行不寻址，试听仅音频就绪且时间有效；新增LocateLineButton明确定位 | --lyrics-drafts、--lyrics；原播放命令 |
| 旧时间错误刷新 | 原错误控件就地显示；稳定目标缓存，Esc恢复；RestoreTimeDraftButton明确撤回全部未提交时间 | --lyrics-drafts：非法/删除/过期/预编辑、保存门禁及撤回；两项编译后变异 |
| 保存/导出准备及时间轴/微调/拆合 | 仍走原命令和项目历史，新增统一时间草稿准备；失败返回具体时间组，未发布非法值 | --task-commands、--lyrics-drafts、时间/接线领域定向 |

同一工作区实例持有正文和时间区，时间区只移动控件宿主，不复制草稿/选择或历史。实际当前构建、原生截图及未验收边界见[实施记录§6](ui-0496-refactoring-progress.md#6-第2步歌词编辑与时间组提交0499)。


<a id="settings-add-lyrics"></a>
## 7. 工程设置与统一添加歌词入口（0.49.10）

本节接续§6；旧表保留历史入口，不改写成当前完成声明。设置只是工程任务的独立工作页，继续使用同一EditorViewModel、ProjectWorkspace和命令历史。

| 原入口 | 当前入口与门禁 | 验证入口 |
|---|---|---|
| CompositionPanel / OutputPanelButton / OutputPanelMenuItem | 顶栏ProjectSettingsButton打开ProjectSettingsView；原画面设置动作/菜单也指向同一页。画布/背景/音频/输出四组，返回原任务与同一工坊实例 | --project-settings、--task-commands、ControlWiringTests |
| CanvasWidthBox / CanvasHeightBox / FrameRateCombo | 控件名迁入设置页；常用尺寸与自定义像素，宽高整组提交。自定义帧率保留，非法/过期草稿不发布，Esc恢复 | CompositionEditingTests、--project-settings及草稿重置变异 |
| BackgroundColorBox / BackgroundColorSwatch / ChooseBackgroundImageButton / ChooseBackgroundVideoButton / ClearBackgroundButton | 同组选择/替换/清除，媒体名及异步图片/视频首帧缩略图；适配按媒体可见，播放策略只供视频。失败保留原媒体，菜单失败返回设置页就地显示，换工程拒绝旧选择器/准备结果 | BackgroundEditingTests / BackgroundVideoEditingTests、--project-settings、--background-video |
| QuickImportAudioButton / 文件菜单音频导入 | 音频替换移入设置页，文件菜单保持原串行导入路径；文件名、时长/实际音频状态和SettingsRetryAudioButton复用原会话 | --project-settings：实际按钮+隔离准备器，失败保留/恢复/一次历史；恢复不改工程 |
| OutputDurationBox / UseAudioDurationButton / UseLyricsDurationButton | 设置页输出范围组；未改输入保持整数微秒，取音频/歌词末尾明确计算一次，后续编辑不持续绑定 | CompositionEditingTests、--project-settings |
| QuickImportLyricsButton / PasteLyricsButton / AddLineButton | 歌词工具栏AddLyricsButton统一弹层，空来源EmptyAddLyricsButton展开同一弹层；仍调用原文件导入/粘贴/新建行。行操作移入同一工具栏，来源管理菜单保留原对话框/关闭 | --project-settings原生弹层、--lyrics-drafts长内容、接线检查 |
| PasteLyricsText / AppendLyricsButton | 明确已有来源末尾或新建来源名称；原文本和Unicode拆分规则、未定时状态保持。新来源+歌词整批一次撤销，旧会话/Generation/已删来源拒绝发布并保留输入 | LyricsAppendTargetTests七项、--project-settings、工程身份变异 |
| 非法设置下保存/导出/新建/打开/首页/关闭 | 原工程命令准备检查设置草稿；失败返回独立设置页。工坊自己的保存/历史不被工程设置取代 | --project-settings、--task-commands |
| 紧凑歌词实时预览 | 在原中性预览上调整响应式宿主；窄窗旁置240宽预览，来源列表200宽，正文与列表可滚动，时间区仍独立固定 | --lyrics-drafts深浅三视口/长错误/200行及原生逐层裁切检查 |

实际构建/截图/进程与尚未完成的边界见[实施记录§7](ui-0496-refactoring-progress.md#7-工程设置与添加歌词入口04910)。导出草稿往返、真实预设候选试看与作者返回未由本批完成；中性歌词预览不变成正式画面试看。
