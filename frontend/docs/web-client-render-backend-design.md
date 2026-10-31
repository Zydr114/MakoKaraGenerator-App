# Web 客户端：渲染设备（CPU／GPU）

日期：2026-10-27。状态：**已完成**（2026-10-27 用户验收；实施记录见 §6）。
依据：
- 用户 2026-10-27 的决定：W5 之后先同步合同 1.39.0，再在 W6 之前插一个小批次，做渲染设备设置（整体设计稿 `docs/web-ui-design.md` §10）。
- 后端合同 **1.39.0**（产品 0.106.0，后端 `9ab24b0`），行为见 `docs/backend/backend-api.md` §25，调用顺序见 `docs/backend/frontend-workflows.md` §5.3。部署在 `.dev/backend-dist/0.106.0`。2026-10-27 在本机实测：`defaultBackend` 是 `cpu`，GPU 可用，设备是 AMD Radeon 880M Graphics（RADV STRIX1，Vulkan 1.4.354）。

## 1. 后端提供了什么

- `GET /api/v1/render-backends`：启动默认（`MAKO_RENDER_BACKEND`，未配置为 CPU），以及 CPU、GPU 各一项的检查结果。可用时有设备摘要，不可用时有原因（`field: renderBackend`，failureCode 是 `RenderBackendUnavailable`／`Unsupported`／`Mismatch`／`ProtocolInvalid`）。这是隔离 Worker 当时真实检查的结果，不保证之后一定能渲染。
- 预览创建（工程、候选试看、模板试样）、正式验证、导出、同帧测量的请求都可以带 `renderBackend: 'cpu' | 'gpu'`。省略或 null 用启动默认；`PUT .../input` 省略时保留预览原来的选择。
- `PUT /api/v1/preview-sessions/{id}/render-backend`：只给已有预览换设备。相同设备是 no-op；不同设备成功后换新代次，旧帧作废；失败时旧状态不变。
- 预览会话、帧、事件一定回显实际设备；任务在登记时固定选择，写进 job input、验证／导出报告、测量结果。历史记录里没有这个字段表示“未记录”，不能当成 CPU。
- GPU 失败不会自动改用 CPU。要用 CPU，前端得明确发起新的选择或新任务。设备选择不写进工程、修订或撤销历史。

## 2. 界面

### 2.1 顶栏“渲染设备”

顶栏右侧、连接状态左边加一个按钮，显示当前偏好解析出的设备，例如“CPU”或“GPU”。没有工程时也显示：样式库的缩略图也要用它。

> §10 写的是“设置里加偏好”。导航栏的“设置”打开的是工程设置面板，只在有工程时出现，而设备偏好记在本机、不属于工程，所以改放在顶栏。

点开是一个菜单：

- **跟随后端默认（CPU）**：括号里是 `defaultBackend`。选这项时请求里不带 `renderBackend`。
- **CPU**：副标题是设备摘要，本机是“Skia raster”。
- **GPU**：副标题是设备摘要，例如“AMD Radeon 880M Graphics · Vulkan 1.4.354”。不可用时这一项禁用，副标题写原因，例如“不可用：GPU device unavailable（RenderBackendUnavailable）”。
- 分隔线下面是 **重新检测**：重新读一次 `render-backends`。

偏好记在 `localStorage` 的 `mako.renderBackend`，值是 `cpu` 或 `gpu`；没有这个键就是跟随默认。它不是凭证，可以这样存。偏好对所有工程生效，不写进工程。

偏好是 GPU、检测结果却是不可用时（比如换了机器），偏好**不自动改**。按钮显示“GPU（不可用）”，下面各处的渲染会按 §2.3 失败并写明原因，用户在菜单里改成 CPU 才会恢复。

### 2.2 各处怎么用偏好

| 地方 | 会话 | 怎么用 |
|---|---|---|
| 成片预览（歌词、编排、外观页右侧） | 长期开着 | 创建时带偏好。偏好变了，用 `PUT .../render-backend` 换设备，见 §2.3 |
| 导出页首帧、末帧缩略图 | 一次性 | 下次渲染时带新偏好；偏好变了就重新渲染这两张 |
| 外观页候选试看、样式库缩略图 | 一次性 | 下次渲染时带新偏好；已经缓存的缩略图不重画 |
| 编排页同帧测量（叠加层几何） | 一次性 | 带偏好 |
| 正式验证、导出任务 | 任务 | 开始时带偏好。任务开始后再改偏好，不影响这个任务 |

“跟随后端默认”时都不带这个字段。只有给已开的预览换设备时，才把它解析成 `defaultBackend`（换设备接口要求填具体值）。

### 2.3 成片预览换设备

`PreviewController` 记下“想用的设备”和当前会话实际的设备：

1. 还没有会话：创建时带偏好；回执里的 `renderBackend` 就是会话的实际设备。
2. 有会话，而偏好解析出的设备和会话的设备不一样：在下一次取帧之前先 `PUT .../render-backend`。成功后采用新代次，同代次的播放时刻从头算起，之后照常取帧。晚到的旧帧按代次丢弃（现有逻辑）。
3. 换设备失败：
   - `previewGenerationConflict`：丢掉会话，下次按新偏好重建（与现在处理代次冲突的方式一致）。
   - 设备错误（`field: renderBackend`）或其他错误：旧会话不动，画面保留最后一帧。预览停在错误状态，写明“没有换到 GPU：渲染设备不可用：<原因>。画面是 CPU 渲染的最后一帧。”之后**不再取帧**，也不自动重试；改偏好或点“重试”才会再试。这样预览不会悄悄继续用原来的设备出新帧。
4. 创建时设备就不可用（比如偏好是 GPU，GPU 不可用）：显示错误并提示“可以在顶栏改用别的渲染设备”，没有画面；同样停住，不反复重建，改偏好或点“重试”才再建。

预览标题加上实际设备：“成片 · GPU · 修订 12 · 0:01.200”。设备取自正在显示的那一帧的元数据，而不是偏好。

### 2.4 任务和报告

- 正式验证、导出的任务卡片写明“渲染设备：GPU”，取自任务的 `input.renderBackend`；没有这个字段就写“渲染设备：未记录”。导出报告摘要里也有一项“设备”，取自报告的 `renderBackend`。
- 提交时因设备失败（`field: renderBackend`），提交错误写明原因，并提示“可以在顶栏改用别的渲染设备后重新开始”。
- 偏好变了，正在跑的任务不受影响，卡片上仍是任务自己的设备。

### 2.5 错误文案

设备错误统一写成“渲染设备不可用：<message>（<failureCode>）”。判断条件是 `BackendError.field === 'renderBackend'`。

## 3. 结构

- **协议**（`src/protocol/preview.ts` 等）：
  - 新增 `RenderBackend = 'cpu' | 'gpu'`，以及 `RenderBackendsResponse`、`RenderBackendAvailability`、`RenderBackendDeviceInfo`、`PreviewRenderBackendInput`。
  - 上面列出的请求类型加可选的 `renderBackend?: RenderBackend | null`。
  - 回显：`PreviewSessionResponse`、`PreviewFrameMetadata`、`PreviewEvent` 里一定有 `renderBackend`；`RenderJobInput`、`ExportJobInputSnapshot`、`ValidationReport`、`ExportReport`、`SceneMeasurement` 里是可选的。
  - 单元测试核对枚举和这些字段与 OpenAPI 一致。
- **客户端**（`src/client/mako-client.ts`）：
  - `renderBackends()`。
  - `preview.setRenderBackend(previewId, { expectedPreviewGeneration, renderBackend })`，按回执重试（`preview-render-backend`）。
  - `renderStill` 的输入加 `renderBackend`；其余创建、任务、测量的输入本来就会原样带过去。
- **编辑器**：
  - `src/editor/model/render-device.ts`（纯 TS）：偏好解析成 `choice()`，其中 `request` 是请求里要带的值（跟随默认时是 undefined），`target` 是想要的实际设备（给换设备用）；还有设备文案。各 store 只依赖其中的 `RenderBackendRequest` 接口，缺省是 `FOLLOW_DEFAULT`，单元测试不用管偏好。
  - `src/editor/preview/render-device.ts`：`RenderDeviceStore`，管偏好与检测结果，读写 `localStorage`；还有设备错误的文案（要用 `BackendError`，所以不放在 model 里）。整个应用一个实例，和 `client` 放在一起（`connection.ts`）。连接重置后重新检测。
  - 顶栏菜单组件。
  - `PreviewController` 按 §2.3 加 `setRenderBackend`、`retry`，`PreviewPanel` 订阅偏好并显示设备。
  - 各一次性渲染、测量、任务的调用点用 `renderBackendField(device)` 展开请求字段（跟随默认时展开为空）；任务卡片显示设备。

## 4. 实施步骤

| 步 | 内容 | 检查 |
|---|---|---|
| 1 | 协议类型、客户端 `renderBackends`／`setRenderBackend`／`renderStill` 透传；合同一致性和客户端单元测试 | typecheck、unit |
| 2 | `RenderDeviceStore` 与顶栏菜单（检测、选择、记住、GPU 不可用时禁用并写明原因） | unit；浏览器截图 |
| 3 | 成片预览按偏好创建、换设备、失败停住与重试，标题显示实际设备 | unit（假客户端）；浏览器 |
| 4 | 一次性渲染、测量、验证、导出带偏好；任务卡片显示设备；设备错误文案 | typecheck、unit |
| 5 | 集成测试：`render-backends`，GPU 创建预览，CPU→GPU→CPU 换设备与 no-op，验证任务回显设备。浏览器 smoke：切换偏好后预览标题变成 GPU，再切回 CPU，验证任务卡片写明设备 | integration；smoke |

每步一提交并推送。

## 5. 验收

1. 顶栏能看到当前设备。菜单列出跟随默认、CPU、GPU，以及各自的设备摘要；选择在刷新页面后还在。
2. 切到 GPU，已开的成片预览不用重开工程就换成 GPU 帧，标题写“GPU”；切回 CPU 同样。
3. 偏好是 GPU 时，导出页缩略图、外观候选试看、样式库缩略图、编排测量、正式验证、导出都用 GPU；任务卡片写“渲染设备：GPU”。
4. GPU 不可用时：菜单里 GPU 禁用并写明原因；如果偏好已经是 GPU，预览和任务失败并写明原因，不会自动用 CPU 出结果。
5. 现有的 smoke（browser、editor、appearance、orchestration、project）在跟随默认下照常通过。

本机的 GPU 一直可用。第 4 条里“不可用”的界面，用隐藏 Vulkan 驱动的方式启动后端来实测（见 §7）。

## 6. 实施记录

2026-10-27 实施，每步一提交并推送：

| 步 | 提交 | 内容 |
|---|---|---|
| 0 | `10a6da5` | 同步合同 1.39.0（产品 0.106.0，后端 `9ab24b0`），回归全绿：单元 3640、集成 148，五个 smoke 照常通过 |
| — | `301f995` | 本设计 |
| 1 | `50bc565` | 协议类型（`RenderBackend`、检测结果、换设备输入，各请求的可选字段与回显）；客户端 `preview.renderBackends()`、`preview.setRenderBackend()`（按回执重试）、`renderStill` 透传 |
| 2 | `bad321e` | `model/render-device.ts`（偏好解析与文案，纯 TS）、`RenderDeviceStore`（`localStorage` 的 `mako.renderBackend`，单飞检测，失败保留上次结果，连接重置后作废）、顶栏菜单 |
| 3 | `7aa97d1` | `PreviewController`：创建时带偏好，偏好变了先 `PUT .../render-backend` 再取帧；设备失败停住、保留最后一帧、“重试”；标题显示正在显示那一帧的设备 |
| 4 | `d1625d8` | 导出页缩略图（缓存键含设备，偏好变了重画）、正式验证、导出、外观候选试看、样式库缩略图与试样、编排同帧测量都按偏好带字段；任务卡片“渲染设备：…”，导出报告摘要“设备”；提交时的设备错误文案 |
| 5 | `979af58` | 集成测试 `render-backend.test.ts`（5 项，本机真实 GPU 全部通过） |
| 5 | `7c87e21` 及下一提交 | 浏览器 smoke `pnpm smoke:render`：GPU 可用时 15 项；GPU 不可用时走另一支 8 项（用 `VK_ICD_FILENAMES=/nonexistent` 启动后端模拟） |

单元测试从 3640 增加到 3654：合同一致性 1、客户端 2、偏好与文案 5、预览换设备 4、同帧测量带设备 1 等。

## 7. 出口对照

| 验收（§5） | 结果 | 证据 |
|---|---|---|
| 1. 顶栏显示当前设备，菜单三项与设备摘要，刷新后偏好还在 | 通过 | `smoke:render` 第 1、7 段；单元 `render-device.test.ts` |
| 2. 切到 GPU，已开的预览不重开就换成 GPU 帧；切回同样 | 通过 | `smoke:render` 第 3、6 段（外观页 CPU→GPU→跟随默认回到 CPU）；集成 CPU→GPU→no-op→冲突→GPU 帧→CPU；单元 `preview-controller.test.ts` |
| 3. 偏好是 GPU 时各处都用 GPU，任务卡片写明 | 通过 | `smoke:render` 第 3～5 段：候选试看、编排预览与测量、样式库缩略图、导出页缩略图、正式验证与导出；卡片“渲染设备：GPU”、报告摘要“设备 GPU”；集成里验证任务的输入与报告回显 |
| 4. GPU 不可用时禁用并写明原因；偏好已是 GPU 时明确失败，不改用 CPU | 通过 | 后端以 `VK_ICD_FILENAMES=/nonexistent/icd.json VK_DRIVER_FILES=/nonexistent/icd.json pnpm dev:backend` 启动，检测结果是 GPU 不可用（`RenderBackendUnavailable`）。`smoke:render` 的另一支：菜单里 GPU 禁用并写原因；把偏好存成 GPU 后刷新，按钮写“GPU（不可用）”；成片预览创建失败、写明原因并给“重试”，没有画面；正式验证提交失败并提示改设备；改用 CPU 后预览恢复。单元：换设备失败停住并保留最后一帧、创建失败不反复重建 |
| 5. 现有 smoke 在跟随默认下照常通过 | 通过 | browser 18/18、editor 56/56、appearance 21/21、orchestration 95/95、project 26/26 |

## 8. 已知限制

- **换“换设备途中”的切换失败只有单元测试**：真实后端上 GPU 要么一直可用、要么启动时就不可用，“已有 CPU 会话换 GPU 时失败”这一路（停住、保留 CPU 的最后一帧）只能用假客户端测。
- **已缓存的缩略图不重画**：样式库与选择器的模板缩略图、样式库试样按模板与模板修订缓存，改偏好后不会重画，下次缓存失效时才用新设备（§2.2 的约定）。导出页首帧、末帧缩略图的缓存键里有设备，改偏好会重画。
- **同帧测量不因改偏好而重测**：编排页叠加层的几何与设备无关，改偏好后沿用当前测量结果，下次修订或时刻变化时才按新设备测量。
- **候选试看与缩略图不显示设备**：这些一次性渲染的界面里没有标题位置写设备；只有成片预览标题、任务卡片和导出报告写明实际设备。
- **检测结果不自动刷新**：连接就绪时检测一次，之后只有“重新检测”或连接重置才会再读；Worker 中途失去 GPU 时，以实际渲染失败的原因为准。
