# SVG 依赖第三方声明

本目录保留本 App 引入的 SVG 依赖原许可证；这些许可属于对应第三方组件，不给 App 自身添加许可证。

| 组件 | 固定版本 | 包声明许可证 | 包作者 |
| --- | --- | --- | --- |
| Svg.Skia | 4.9.1 | MIT | Wiesław Šoltés |
| Svg.Model | 4.9.1 | MIT | Wiesław Šoltés |
| Svg.SceneGraph | 4.9.1 | MIT | Wiesław Šoltés |
| Svg.Animation | 4.9.1 | MIT | Wiesław Šoltés |
| ShimSkiaSharp | 4.9.1 | MIT | Wiesław Šoltés |
| Svg.Custom | 4.9.1 | MS-PL | Wiesław Šoltés |
| ExCSS | 4.3.1 | MIT | Tyler Brinks |

Svg 系列来自固定 tag `v4.9.1`，包 repository commit 为
`8b23ee1e01b1f2ba90eb74b71ab8c7f27d49f037`。Svg.Custom 的 MS-PL 不能由根目录 MIT 替代。
ExCSS 来源为包声明 commit `c97e84d6126bb2e42658cf5af627d52e697c8779`。

- [Svg 系列 MIT 原文](Svg.Skia-MIT.txt)
- [Svg.Custom MS-PL 原文](Svg.Custom-MS-PL.txt)
- [ExCSS MIT 原文](ExCSS-MIT.txt)

还原使用 App 已锁定的 SkiaSharp `3.119.4` 与 HarfBuzzSharp `8.3.1.5`；未升级这两个系列。
Svg 包为受管组件，最终资产包括既有 SkiaSharp 及 HarfBuzzSharp 的 Linux、macOS、Win32 native 包。
Linux x64 有 `libSkiaSharp.so` 和 `libHarfBuzzSharp.so`；Windows x64 有对应 DLL。
资产存在只证明依赖闭包；本阶段实际执行验证以 Linux Vulkan 日志为准，不声明 Windows/macOS 运行通过。
