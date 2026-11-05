# 作者源码声明与完整配置采用烟测

`scripts/author-metadata-smoke.mjs`（`pnpm smoke:author-metadata`）使用真实 Chrome、真实 HTTP
fixture 和普通产品控件，验证源码声明改变后的配置迁移。它复用作者包烟测的只读 ZIP 校验、
CodeMirror 正常复制/输入、完整包保存、异步编译与真实显示 PNG 证据，不调用私有 store。

## 准备与运行

需要 Node 26、前端依赖与 Chromium。由运行者先启动本仓库当前源码匹配的 Server/Worker 和
loopback 前端，并使用独立后端 profile；GPU 后端须可用。脚本只启动/关闭自己的 Chrome profile，
不会管理外部服务。`--headed` 显示窗口，`CHROME_PATH` 可指定 Chromium。

输入必须为真实 HTTP probe 产出的 package3/SDK2 SVG vector `.mkfx`：原包无参数，单 controller，
各一个 C#、SVG、svgResource，含有效 vector viewport 和六位纯色 fill。脚本只读归档，
检查 ZIP CRC/SHA、文件清单与有限预算；随后通过产品编辑器生成 B/C 修订，不重写输入归档。
`--binary-path` 可额外要求输入包含指定只读文件。`--inspect-fixture` 仅检查输入，不打开浏览器。

从 App 仓库根运行；前端地址必须是无路径、查询与凭证的 loopback origin，输出目录必须不存在，
且父目录已存在：

```bash
node frontend/scripts/author-metadata-smoke.mjs \
  --url http://localhost:5187 \
  --package .local/script-sdk-m09-20261105/http-svg-04/svg-vector-fixture.mkfx \
  --output .local/script-sdk-m09-20261105/ui-author-metadata-next
```

历史输入仅是有限 fixture 示例，重跑时须保留自己的 HTTP 来源与匹配发布依据。
2026-11-05 已通过的第三轮使用产品 **0.112.14**、合同 **1.46.0**，后端发布源码
`1e83e0d475abe10be462e2774621f0c17764457a`（`sourceDirty=false`）。对应本地启动器
`.local/script-sdk-m09-20261105/run-author-metadata-ui.mjs` 管理匹配发布、独立 profile 和 Vite；
该脚本不属于产品烟测入口，后续运行应核对其发布路径并使用全新 run 目录。

## 覆盖与故障注入

主流程通过 UI 导入并创建 A，编译 A 以支持后续精确 undo；修改 manifest/export/Int64/duration
声明与 SVG 颜色，保存编译 B/C。候选只预览本地配置字段，普通在用 A 和实际 A 图片保持。
修正配置后单个采用 POST 提交完整八字段，核对回执/GET/声明与执行时 CAS；普通新 schema 控件
实际 PUT 后撤销，源码采用 undo/redo 恢复完整配置及旧/新控件。核对未 blur 原文、取消重开、
真实 400 字段错误、迟到旧声明、未保存源码及真实 Ctrl+Z/Ctrl+Shift+Z 历史。

`author-metadata-fetch-gates.mjs` 仅在本次独立标签页包装 fetch；控制记录单独输出：

- 延迟一个旧候选的真实声明响应，读取 clone 供网络采集后，原样交付同一个 Response。
- 真实采用 POST 成功后，对紧随的一个逻辑草稿 GET 注入交付故障。当前客户端有三次网络重试，
  因此初次加重试共四次；每次仍完成 native GET 并记录真实 `200`，然后抛 `TypeError`。
  **这不是实际断网或真实服务 GET 失败**。故障耗尽后显式“重读采用结果并比较”走真实读取，
  证明页面保留最后确认配置、冻结普通写入、比较成功后解冻并清掉无法确认的旧采用历史。

不能把声明 OPTIONS 预检当作业务 GET。烟测记录两者，核对精确的两个查询及无 body；
所有作者写入来自 UI，控制器不伪造写入或响应正文，也不调用私有重读来制造产品不存在的入口。

## 证据与边界

`summary.json` 给出逐项检查和显示帧数；`network.json` 保留脱敏真实网络，原始 PNG、阶段截图
与 `displayed-frames.json` 核对下载/blob SHA、RGBA、可见性、来源和代次。另有
`author-identities.json`、`canonical-manifests.json`、`metadata-requests.json`、
`candidate-rejection.json`、`ordinary-schema-controls.json`、`delivery-gates.json`、
`credential-scan.json` 和 `evidence-manifest.json`。失败会保留阶段及本轮对象 ID 供运行者清理；
成功时仅经 UI 删除本轮草稿/组件并恢复设备设置。

2026-11-05 第三轮 `ui-author-metadata-03` 为 **140 检查/7 显示帧通过**；第一轮的 CDP 修饰位、
第二轮的 OPTIONS 断言修正及失败证据保留在 [W8 补验收记录](web-client-w8-design.md) §7.1。
PNG 采集与交付控制会改变时序，不能用作性能证据。Chrome 启动器禁用浏览器 GPU；`gpu/vulkan`
来自后端真实响应，不代表浏览器硬件加速。浏览器只有 opaque 试样，候选没有独立渲染器；
透明 HTTP 帧、导出、共享纹理、跨平台、长期运行和完整产品验收均须分别记录。
