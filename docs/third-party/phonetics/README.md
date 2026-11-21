# 前端注音与分词依赖第三方声明

本目录保留歌词编辑与注音打轴重构（Phase 2 ~ Phase 4）新增前端注音引擎与按需下载词典资产的第三方开源许可声明；这些许可仅适用于对应第三方组件，不改变本仓库自身许可证。

| 组件 / 资产 | 版本 | 许可证（含传递依赖） | 分发方式 | 合规说明 |
| :--- | :--- | :--- | :--- | :--- |
| `wanakana` | `^5.3.1` | **MIT**（0 运行时依赖） | 随前端打包（~0.12 MB） | 用于 `F2` 原地注音浮窗罗马音实时预览转平/片假名 |
| `@patdx/kuromoji`（仅形态素分词引擎） | `^1.0.4` | **Apache-2.0**（内嵌 `doublearray` 上游 **MIT**，0 外部运行依赖） | 随前端打包（< 0.1 MB，**显式剔除**包内 `dict/`） | 通过 Vite 构建插件与桌面端打包脚本严格排除 `@patdx/kuromoji/dict/**`，确保最终 Web 与桌面端产物零 IPADIC 残留 |
| `NAIST-jdic 0.6.3b`（自编裁剪 `.dat.gz`） | `0.6.3b` | **BSD-3-Clause**（无 ICOT 附加条款） | **不打入安装包**；用户首次使用时界面明确确认后按需下载（~14~16 MB）或离线本地导入，持久化缓存于本地 OPFS / IndexedDB | 由 `scripts/build-naist-jdic.mjs` 从官方 `mecab-naist-jdic-0.6.3b` 源 CSV 裁剪编译，附带 `LICENSE.naist-jdic.txt` 与 SHA-256 `manifest.json` |

## 许可证原文索引

- [wanakana MIT 许可证原文](wanakana-MIT.txt)
- [@patdx/kuromoji Apache-2.0 许可证原文](kuromoji-Apache-2.0.txt)
- [NAIST-jdic 0.6.3b BSD-3-Clause 许可证声明](naist-jdic-BSD-3-Clause.txt)

## 非引入范围说明

- 本期明确**不引入**任何中文词典或中文自动注音第三方库；多语种（如英文音标、手动中文拼音）由 `F2` 原地注音浮窗的 `Shift+Enter` 原样提交承载。
- 音频保调变速（`0.25x ~ 2.0x`）直接复用浏览器 / Electron 原生 `HTMLAudioElement.preservesPitch`，未引入额外第三方音频库。
