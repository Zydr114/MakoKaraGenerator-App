# GPU 成片预览背景修复记录（2026-11-01）

修复提交：`6107175`（`fix(preview): 修复GPU成片预览误用透明背景`）。

## 问题与修复

桌面 GPU 成片预览创建流时传入 `alphaMode: 'straight'`。后端将这一显式透明输出模式映射为 `TransparentBackground = true`，渲染器因此跳过工程底色和背景媒体；用户可成功导入和修改背景，GPU 预览仍只显示透明区域后的容器底色。CPU 静态预览使用 `opaque`，所以能显示背景。这是前端成片预览的模式选择缺陷，未发现背景设置因 GPU 设备选择而被禁用的逻辑。

`GpuPreviewController` 的创建请求现改为 `alphaMode: 'opaque'`，与成片预览用途一致。输入修订替换、尺寸调整以及重新创建均延续这一选择。显式 `straight` 仍是合法的透明歌词层输出语义；本修复不修改公开 API、后端透明输出行为、纹理 alpha 格式或 500ms 音频时钟修正节奏。

## 已完成检查

- 回归先在原实现复现失败：期望 `opaque`，实际为 `straight`。
- `pnpm exec vitest run test/unit/gpu-preview-controller.test.ts`：6 项通过，覆盖创建、修订替换、尺寸调整、失败重试与隐藏后重新显示的请求模式。这是客户端参数回归，不是像素渲染验收。
- `pnpm typecheck` 与 `git diff --check`：通过。
- 主代理完成前端构建，`vite build --emptyOutDir false` 成功。

## 真实画面验收

独立验收使用修复提交 `6107175` 的前端构建，以及匹配部署 `.local/backend-dist/20261101T045924324Z`（部署清单 SHA-256：`fdaa354a0809150a57df47cc48e578b5844e909f3ba831b2804aff3524777ac2`）。真实生产 controller 的创建请求为 `1280×720 / opaque`；随后以 `320×180` 定点暂停帧比较 CPU PNG 与 Electron GPU canvas 像素。验收使用独立命名 profile 和专用输入，没有操作用户工程。

两轮各 7 个定点场景全部通过，分别使用 `BackgroundColor` 与 `BackgroundMedia` 效果图输入：

- 背景图片的四个彩色角完整显示，CPU/GPU 最大通道差为 2。
- 背景视频在 250ms 显示红色、1250ms 显示蓝色，两帧 CPU/GPU 最大通道差均为 1；真实界面的背景设置可以使用。
- `BackgroundColor` 依赖的图像效果在 `opaque` 下可见，与同工坊移除效果绑定的基准相比有 6760 个通道差大于 8（平均绝对差 1.718294）；该效果帧 CPU/GPU 最大通道差为 1，诊断没有透明背景依赖旁路。
- `BackgroundMedia` 依赖的补验使用固定 PNG 工程背景媒体取样，效果相对于同工坊无绑定基准有 5989 个通道差大于 8（平均绝对差 1.280530）；效果帧 CPU/GPU 最大通道差为 1、平均绝对差 0.000182，诊断没有旁路。这证明本次有限效果确实使用了背景取样；未测视频驱动的背景取样效果，不扩展为任意效果或材质覆盖。
- 显式 `straight` 对照仍省略视频及工程背景，四角 Alpha 为 0；背景依赖效果按原语义产生 `TransparentBackgroundDependency` 警告并旁路。透明对照不据此声称所有边缘像素与 CPU 完全一致。

版本化摘要见 [双轮验收证据](evidence/gpu-preview-background-fix-2026-11-01.json)。原始报告分别为 `.local/gpu-preview-background/real-6107175-attempt2/report.json` 与 `.local/gpu-preview-background/real-6107175-media/report.json`。每轮启动器正常退出（code 0），各 11 个已观测的本次拥有 PID/startTicks 身份均已不存在；这是已采样身份的清理证据，不宣称枚举完整任意子进程树。首次未执行场景的导航就绪竞态失败报告另行保留。

这些像素读取仅用于有限正确性验收。本记录不构成性能验收，不据此宣称帧率或延迟达标。

## 重新载入边界

普通桌面入口提供构建后的静态前端文件；修改源码或重新构建不会更新已加载的页面。桌面主进程禁止完整导航及 F5、Ctrl/Cmd+R 刷新。用户应先保存工程，再关闭并重新启动桌面应用载入新构建。

修复版已通过正常桌面入口启动，沿用默认 profile，页面地址 `http://127.0.0.1:5173`；HTTP 页面及脚本内容与本次验收的新构建哈希一致。此次供手测的启动没有启用 CDP，没有打开测试工程或修改用户设置。

2026-11-01 用户实际使用修复版后确认：GPU 预览现在能够正常看到背景。这项确认仅记录背景恢复，不扩展为用户确认所有效果、视频驱动取样或帧率达标。
