# 2026-11-01 最小 Web 整合验收

选择复用已有 Web 编辑器，减少整合改动并保留现有预览行为；未比较 Electron 性能。Backend `a4783fa`、Web `c2d06fd` 的 tracked HEAD 快照在初始 App 提交 `947f8d4` 中完整导入，来源记录见根 `source-snapshots.json`。源仓库未修改；Backend 原有 untracked `tools/backend-project-image-export-probe/` 未复制。构建产物、依赖、私人 profile 和嵌套 Git 不属于源快照。

根入口 `scripts/app.mjs` 提供 setup/build/start/dev；App 内发布匹配的 Server＋完整 Worker，不使用兄弟仓库或旧 artifacts。前端启动器改为 App 内部署定位，并补上就绪后初始化失败清理、子进程 spawn 失败、停止计时器清理和真实路径边界。生产静态页面补上受限的本机同源 bootstrap，开发模式复用 Vite bootstrap 插件。HTTP API、预览控制频率和服务端 quota 未更改。

本地实际结果：

| 检查 | 结果与边界 |
| --- | --- |
| 工具链 | Node 26.12.1、pnpm 11.7.0、.NET SDK 10.0.113；Linux x64 |
| 依赖安装 | `pnpm --dir frontend install --frozen-lockfile` 成功；文档使用显式 `pnpm run setup` 避免命令名歧义 |
| `pnpm build` | TypeScript＋Vite 成功；Release/linux-x64 self-contained Server 与完整 Worker 发布成功，两个 dotnet publish 日志无 warning/error；Vite 提示原有大 chunk，不是构建失败 |
| 发布来源 | `.local/backend-dist/20261031T173022570Z`；manifest 记录 App `947f8d4`、`sourceDirty:false`、925 个生产输入、1022 个部署文件；UTC 目录时间对应本地 2026-11-01 |
| `pnpm test:launcher` | 6 项通过：凭证权限异常、凭证 JSON 异常、就绪后 bootstrap 写入失败均回收自有子进程；缺失可执行程序明确拒绝；正常停止清除 bootstrap；部署 symlink 不能逃出 App |
| `pnpm test:unit` | 40 文件、3849 测试通过；不是完整后端或产品验收 |
| `pnpm start` | 生产页面 `127.0.0.1:5173` 与本次 Server `127.0.0.1:44975` 实际就绪 |
| 生产 bootstrap 边界 | 首页 200；直接读取、cross-site、错误 Host 各 403；同源请求 200；响应正文未输出或提交 |
| 真实 Chromium 有限流程 | 自动连接→上传生成的 WAV/LRC→新建两行工程→元素时间修改（修订1）→撤销（修订2）→暂停定位 1 秒→显示实际 PNG→关闭临时工程；无 Runtime 未处理异常 |
| 暂停 PNG | 1280×720，14680 字节，4281 个非黑 RGB 像素，真实歌词显示；SHA-256 `6344e171ebed41929b7dc44793e0af8c9e3560d5b590cffebbe19d1e4ad65074`；只证明正常传输与显示 |
| 生产退出 | 向验证过所属关系的 App PID 381040 发 SIGTERM；host PID 381066/startTicks3796831、Server PID381067/startTicks3796865 均消失，启动器退出0，bootstrap删除 |
| `pnpm dev --port 5174` | Vite 开发页面实际通过浏览器自动连接本次 Server `127.0.0.1:46445`；只做连接检查 |
| 开发退出 | App PID384030收到SIGTERM；host PID384056、Server PID384057均消失，启动器退出0，bootstrap删除 |

复现命令见根 README。有限 browser 脚本是 `pnpm smoke:browser [页面地址]`，成功结果在关闭完成及异常断言之后写出。未提交的本机证据位于 `.local/integration/`：`editor-preview.png`、`browser-result.json`、`lifecycle.json`、`dev-lifecycle.json`；发布日志与 manifest 在上述部署目录。截图不含 bootstrap 或 access token。

目标原始 RGBA／2560×1440 性能矩阵因公开协议不满足前提而停止，有限真实请求记录见 [协议门禁](../performance/preview-protocol-gate.md)。本次不报告吞吐、延迟、零拷贝、完整 W1～W8、长时间稳定性或跨平台验收；PNG 静态截图不能作为这些结论的依据。
