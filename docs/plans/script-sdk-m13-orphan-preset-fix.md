# M13 补充：删除目标后失效预设阻断运行

日期：2026-11-14。状态：修复及本次定向验收完成。范围为 App 当前分支，不执行 M14。

## 根因

Aorist 工程在删除“新容器”后，仍保留启用的“和声”预设，目标身份已经不存在。
2026-11-14 本地 04:05:51 自动保存尚正常，04:06:22 已出现悬空引用；其间片段改绑到常规容器后删除旧容器。
修复前 0.112.89 发布 DLL 可复现：删除被接受，预设保留，随后完整校验报 `InvalidEffectInstance`，
固定输入编码折叠为 `SnapshotInvalid`，界面显示“测量失败：固定运行输入无法准备”。
只在调查副本移除该预设即可通过固定输入编码；布局参考另有下文记录的独立缺陷。
原始证据位于 `.local/investigation-measurement-20261114/`。

保留失效分配供撤销、恢复或明确移除是已有设计；问题是运行校验将其整体拒绝，与界面“不再起作用”的说明不一致。

## 实施方案

1. **统一缺失目标语义。** 顶层应用目标不存在的预设完整保留，包含启用值、快照、实例身份、种子及包闭包。
   运行时跳过这条分配，给出明确警告，其他有效目标继续测量、预览和导出。
   不自动改绑到默认容器；撤销恢复原目标身份后恢复原效果。
2. **保持输入完整性。** 执行实例、源材质与媒体依赖采用一致的有效目标规则；持久化及资源完整性仍保留原校验。
   固定快照源、投影核对与指纹保持一致，不通过删除校验器解决问题。
   有效应用目标上的无效订阅／成员绑定、直接效果实例与损坏资源不在本次放宽范围内。
3. **说明与恢复入口。** 复用外观页“工程默认”的失效分配列表和明确移除操作，说明记录保留、运行跳过及恢复原目标后的行为。
4. **验收。** 复用既有测试，补删除后警告与运行成功、快照往返、恢复原目标、其他错误仍拒绝的最少回归。
   对当前 Aorist 隔离副本验证真实测量、预览、短区间导出；核对原始预设记录未被静默改写。
   更新行为合同、匹配发布及证据，每步中文提交并推送。

## 边界

不修改用户正在打开的工程，不自动移除失效分配，不自动迁移旧工程版本。
正常工程像素与 GPU 表面预算不应变化；不新增大规模夹具或重复整曲性能测量。
最终结果在本记录补齐，构建、测试、合同与实际产品验收分别报告。

## 实现与回归

- `b9a07ba`：修复计划；`c91075d`：失效分配说明及原目标恢复回归；
  `5390bd3`：运行隔离、完整源保留及产品 0.112.90；`b17b21f`：合同 1.52.1；
  `2eb0299`：布局参考修复；`4373d46`：最终合同源码基线。
- 统一复用 `PresetApplicationTargets.Exists` 判断顶层目标；实例投影、材质准备与编译、
  媒体依赖和设备要求跳过失效应用。持久目录、包占用、完整源指纹及快照核对保持严格。
- 只有失效应用、没有其他实际运行输入时，既有运行指纹入口允许静态渲染／测量；
  不可变快照的判断使用弱引用缓存，避免逐帧重复扫描。已有媒体输入仍先走原完整校验。
- 完整 solution build：0 warning、0 error。Core 62、Rendering/codec 239、Hosting 53、
  Scripting 56 项定向回归全部通过。新增后端 4 cases、前端 1 case，复用既有夹具。
  两个删除节点回归先在旧实现上因领域 Error 失败，再在修复上通过；覆盖纯样式和脚本加材质。
- 前端 typecheck、4487 项单元测试通过；合同更新后的 3943 项合同回归通过。
- fresh CLR metadata、freeze write/check、validate 通过：115 路径、128 方法、667 schemas、
  3930 合成例（2839 反例）。HTTP 形状和所有例子 case 不变，最终 20 个生产文件进入合同指纹；
  6 份前端合同／行为副本与来源逐字节一致。

构建及回归证据：`.local/fix-orphan-preset-20261114/`、
`.local/orphan-preset-fix-20261114/rendering-orphan-and-codec.trx`；
最终合同证据：`backend/artifacts/orphan-preset-20261114/contract-final/summary.json`，
源码基线为 `2eb0299013f464ad9e0199113146b323cf145c21`；初轮 `contract/` 保留。
正式帧验证报告只聚合运行帧问题；本次领域 warning 与外观失效分配说明各自保留原有入口。

## 验收暴露的布局参考缺陷

首轮匹配发布 `b17b21f` 已解除固定输入阻断，但真实 HTTP 布局测量返回
`SceneMeasurementWorkerFailed`。直接调用该发布 DLL 得到 `ArgumentException: 歌词行已失效`：
`LayoutReferenceScene` 生成 `__layout_reference__` 代表行，`LyricsForBinding` 却使用原工程解析器查找它。
仅移除孤儿的对照副本也同样失败；时间测量和选中真实行的测量成功，证实这是独立的布局试样来源缺陷。

`2eb0299` 在两个生产文件内修复：几何与样式共用按不可变源缓存的布局试样；只有行 ID、Binding ID、
容器／槽位及分支都匹配生成规则时才使用试样解析器，普通失效行仍拒绝。原工程、源指纹与实例身份不改。
新增一个回归先在旧实现上复现同栈异常，再通过；累计新增后端 5 cases、前端 1 case，无新大型夹具。
最终 solution build 仍为 0 warning／0 error；ProjectHosting 11、SceneMeasurementRendering 4、
宿主测量 28、预设渲染 10 项通过。真实 Aorist 的新 Debug DLL Vulkan 探针中，布局／时间测量分别返回
10／6 个元素，运行诊断为 0；这是源码探针证据，最终交付仍以匹配发布的 HTTP／桌面结果为准。

首轮 HTTP 与桌面失败证据保留于 `.local/fix-orphan-preset-20261114/http/`、`desktop/`。
源码探针日志为 `runtime-layout-{orphan,without-orphan}.log`、`runtime-source-{layout,time}-orphan.log`，
回归红灯为 `layout-preset-red.trx`。桌面前轮还保留了 CPU 默认选择及本地定位器超时的尝试；
最终验收明确选择当前 SDK v2 所需 GPU，并使用独立目录，不把这些尝试计为通过。

## 最终匹配发布

`pnpm build:desktop` 完成，发布为 `.local/backend-dist/20261113T211023584Z`，
产品 0.112.90／合同 1.52.1，后端与原生源提交均为 `4373d46b4b87921976ade17408e9b937bd15c9dd`，
两者 `sourceDirty=false`。逐项复核 1229 个后端源文件、34 个原生源文件、3 个原生产物及
1025 个部署文件 SHA256，全数匹配；证据为 `deployment-verification-final.json`。
前端生产构建通过，保留既有 bundle 大小提示。

最终构建的第一次尝试因本地固定版本 Skia 缓存缺失而失败，日志保留为 `desktop-build-final.log`。
恢复时只复制固定、已校验归档中缺失的 42 个文件；全量 11765 个源文件内容一致，16 个 DEPS 缓存干净，
未修改源码或 pins，随后 bootstrap 全部通过，再以 `desktop-build-final2.log` 记录成功构建。
缓存恢复证据为 `native-cache-repair-skia-audit.json`、`native-cache-repair-deps-audit.json`、
`native-cache-bootstrap-repaired.log`。这些构建环境问题与应用回归分开记录。

## 最终真实工程验收

使用调查时冻结、仍含失效“和声”分配的 Aorist 副本，复制到独立 profile；仅在验收副本中将
`applicationVersion` 从 0.112.89 改为 0.112.90。用户 live 工程未被本次修复或探针写入。

- **HTTP**：`http-final/summary.json` 记录 285 请求、1572 断言全部通过。
  GPU 布局／时间测量、42.866666 秒和 50 秒的浏览器 PNG 预览均成功，PNG 哈希与解码尺寸正确；
  正式帧验证成功。保存前后完整 `presetApplications` 对象逐字段一致，关闭重开后失效分配仍
  `enabled=true`、`targetValid=false`，布局测量再次成功且 PNG 哈希与首次相同。
- **导出**：1080p30、50 秒、1500 帧 H.264＋AAC 导出终态为 `succeeded`，制品长度及 SHA256 匹配。
  ffprobe 确认 1500 视频帧，视频和音频均为 50.000000 秒；ffmpeg 完整解码全部音视频无错误。
  本轮导出任务耗时 117.365 秒，仅记录观测值；桌面验收同期运行，不作性能基准。
- **桌面**：`desktop/verified-all/summary.json` 的 `passed=true`；真实时间／布局请求均 HTTP 200，
  无 fallback diagnostic，编排画布显示布局参考文字，外观页正确显示保留／跳过／恢复说明。
  通过公开 bridge 精确驱动 GPU 共享纹理，在 1920×1080 下请求 42,866,666／50,000,000 微秒并完成绘制，
  `cpuPixelReadbacks=0`、`outstanding=0`、`previewDisabled=false`，无失败事件。
  此探针使用自定义 timelineResetId，未驱动 AudioController；其截图中的 UI 主时钟／准备状态
  不属于精确 seek 的验证结论，普通 UI 同步由下一项补验覆盖。
- **正常 UI 操作**：`desktop/ui-clock-stable-verified/summary.json` 与 `ui-seek-50s-stable.png`：
  通过正常时间轴指针操作定位 50 秒，1280×720 预览、界面主时钟、标题、原生播放时钟和
  当前真实 timelineResetId 的绘制帧均为 50,000,000 微秒；等待 1104 毫秒后仍一致，
  准备提示／遮罩为空，无失败事件、无 CPU 像素回读，工程文件字节未变。主代理已目视核对。
  首次补验因本地探针模板字符串的正则转义错误中断，保留在 `desktop/ui-clock-stable/`，
  未操作时间轴，属于探针错误；修正后在新目录通过，未为此修改生产源码。
- **退出与凭证**：HTTP 观测的 41 个进程身份、桌面的 40 个进程身份全部排水，启动器退出码均为 0。
  HTTP 扫描 646 个文件，桌面扫描 8 个证据文件（2 个内存凭证值），无泄露；未停止用户应用。
  正常 UI 补验的 24 个进程身份全部排水、退出码 0，4 个证据文件的 2 个凭证值精确扫描无泄露；
  首次探针失败的 21 个身份也全部排水。

本轮为该工程的定向回归，未重跑整曲、长时间交互、跨平台或性能验收；前次整曲结果仍属于 0.112.89 的历史证据。
严格工程版本门禁保留，未添加旧工程自动迁移。已准备的隔离桌面副本可通过以下命令打开：

```sh
pnpm desktop --session-name orphan-preset-verified-20261114 --port 5197
```

选择 `orphan-preset-fixed`。本次到此补齐 M13 阻断修复，不执行 M14。
