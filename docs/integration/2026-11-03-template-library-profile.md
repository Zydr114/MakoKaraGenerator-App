# 2026-11-03 桌面个人模板库升级修复

用户运行 `pnpm desktop` 后进入样式库，页面显示“读取模板库失败”。本轮针对这个阻塞修复，保留原有界面控件和旧格式拒绝规则。

## 原因与修复

辉光升级将模板格式提升到 schema 7，Core 已提供 `templates-v7` 目录；Host 却仍固定读取 `state/libraries/templates/index.json`。实际桌面 profile 的该入口是 schema 6，含一个模板及包文件，因此返回 HTTP 422。前端用 `Promise.all` 同时读取内置和个人库，个人库失败使内置结果也未显示。

此前的辉光验收在独立新 profile 上通过，未覆盖升级后复用个人库的场景。此次以原个人库完整副本在旧部署上复现 422；原库及副本各 30 个文件的字节未变化。旧部署证据在 `.local/desktop-library-smoke-old/mw07dm7i/result.json`。

- 新个人库使用 `state/libraries/templates-v7/index.json`。历史固定入口只有明确声明正整数旧 schema 时才原位保留并隔离，不读取模板、不转换、不删除包。
- 已经在固定目录保存的 schema 7 库继续使用原入口及包闭包。未知、未来版本、损坏、链接、超预算或两个当前入口的歧义继续拒绝，不用空库掩盖错误。
- 首次成功选址后在仓储生命周期内固定；选址失败允许修复文件后重试。内置库与发现接口独立于个人库磁盘操作。
- 前端分别保存两个库的读取结果。个人库失败仍可查看和试样内置模板，显示具体错误与重试；新建、复制、保存、导入等个人库写入被禁用，不再使用失败前的旧修订。未保存草稿保留，重试成功后恢复操作。外观页模板选择器使用同样的错误提示。

后端源码提交 `2e6808d`；前端与合同提交 `e423655`。产品 **0.110.1**、合同 **1.42.1**、HTTP 协议 **1**，工程／歌词／模板 schema 仍为 **29/18/7**。工程精确产品版本门禁沿用原规则，不增加旧工程兼容或迁移。

## 分层验证

| 检查 | 结果 |
| --- | --- |
| 缺陷红灯 | 旧实现的新增路径回归 18 项中 7 项失败，失败日志与 TRX 保留 |
| 修复绿灯 | 25 项路径回归与 2 项懒仓储／事件回归通过，已包含在下方分层结果 |
| 后端完整 solution | 0 警告、0 错误 |
| 后端相关回归 | Core 19、Application 45、Hosting 69、Server 32，共 165 项通过，无跳过 |
| 前端 | typecheck 通过，44 个文件／3921 项单元测试通过 |
| App 启动器 | 7 项通过 |
| CLR metadata、freeze、合同校验 | 112 paths／125 methods／597 schemas；3431 例，含 2547 反例 |
| 最终匹配发布的真实 HTTP | templates、full-templates、presets、project-lifecycle、glow 共 5 个文件／56 项通过，耗时 144.39 秒 |

完整构建使用本机既有 SDK 处理参数 `-p:AllowMissingPrunePackageData=true`，未升级依赖。Server 测试文件采用 partial 类，首次按文件名过滤没有执行测试；修正为实际方法名后 32 项通过。前端全量首次暴露一个组件保存测试未设置个人库可用状态，已补齐 fixture 的真实前提后全量通过。合同校验器原本枚举到 1.42.0，本次加入 1.42.1 并保留全部既有形状和负例检查。

源码测试与离线合同日志在 `.local/library-*`，TRX 在 `.local/library-regression*`，metadata 和合同报告在 `backend/artifacts/library-profile-20261103/`。最终 HTTP 日志为 `.local/library-http-integration-final.log`，独立运行针对下文固定提交的匹配发布，全部通过。以上路径均属于本机忽略目录，不提交私人 profile 或启动凭证。

## 首次发布失败与防护

首次新发布 `.local/backend-dist/20261103T045337666Z` 的生产输入 hash 一致，但发布 Server 后、发布 Worker 前 Git HEAD 从 `2e6808d` 变为 `e423655`。SourceLink 将不同提交写入 PDB，两端 `MakoKaraGenerator.Scripting.Abstractions.dll` 因此有 72 字节差异；包、图片与统一来源能力的完整程序集指纹握手正确拒绝。仅核对生产源码文件不足以证明匹配发布。

该发布的真实 HTTP 回归是 51 通过／5 失败（含 Glow 测试硬编码产品版本的单独失败）；桌面成功打开库并保存辉光，但试样未能显示，不能记为桌面通过。失败目录 `.local/acceptance/library-smoke-mw07ueo1-06872b02/` 保留，11 个已观察自有进程全部退出，旧库 30 个文件字节保持。Glow 保存断言已改为读取冻结合同的产品常量。发布工具在 `85f3ca6` 补上 HEAD 稳定检查和两端脚本抽象程序集字节一致性检查，4 项独立回归通过；对该实际混合部署的 `validate_dist` 也明确拒绝。专用桌面脚本与产品版本断言在 `3a1e83f` 提交，后续固定该 HEAD 重新发布到新目录。

## 桌面复验入口

```bash
pnpm smoke:desktop-library --legacy-library .local/desktop-profile/state/libraries/templates
```

专用脚本将旧目录完整复制到唯一命名的隔离 profile，通过真实 Electron 窗口复制内置模板、编辑并保存辉光、关闭并重启同一个 profile、读回完整定义。随后只改变隔离新库的 schema，验证内置可查看、个人库错误提示与禁写，再还原字节并点击重试。两次结束均按自身 PID 与进程身份检查排水；原始旧库和副本逐文件 hash 必须保持一致。

默认不传 `--legacy-library` 时，脚本使用只针对版本头隔离的非空 fixture；实际旧资源闭包验收使用上述完整副本。截图仅在确认当前库只含本次新测试模板后保存，避免把私人模板内容纳入公开证据。

## 最终匹配发布与实际桌面结果

固定 `3a1e83f2853e615ce7e41a9f64773b4d63500564` 发布至 `.local/backend-dist/20261103T050434907Z`，产品 0.110.1，`sourceDirty:false`；生产输入逐项与当前源码一致。两端脚本抽象程序集 SHA-256 均为 `ab435dd51ba68ca3e745d93e7f54779b474fea75f9c37e1e5c1877c352264e5d`。启动检查 `web:null`、`deployment:null`，默认 `pnpm desktop` 指向该部署。证据 `.local/library-final-deployment-verification.json`。

实际旧库完整副本的 Electron 回归退出 0，26 条断言全部通过；[结果 JSON](evidence/template-library-20261103/desktop-result.json)。保存和重启后均等待试样图像 ready，再检查辉光值及属性组一致。主代理已查看[重启后的辉光试样](evidence/template-library-20261103/restarted-glow.png)和[损坏个人库错误提示](evidence/template-library-20261103/invalid-personal-library.png)，沿用既有控件、布局与主题。错误截图记录的是内置详情可读和禁写状态，试样当时仍在生成；正常保存及重启截图另有 ready 门禁。

旧原库与隔离副本的 30 个文件在全过程逐文件 hash 不变。正常主链模板 HTTP 失败为 0，页面未捕获异常为 0；故意损坏新库产生 1 次预期 HTTP 失败，之后还原字节并重试成功。两次启动器均退出 0，各观察 11 个自身进程，残留 0。完整私有证据在 `.local/acceptance/library-smoke-mw086vka-ab4b559b/evidence/`。

对本次构建、测试、防护日志、最终桌面 Server 日志及结果 JSON 共 30 个文件执行凭证模式检查，匹配为 0；检查清单在 `.local/library-log-scan.json`。该结果限于清单所列文本输出，不包括私人库数据或浏览器 profile 存储。

这次只验证本机模板库升级、辉光试样、保存重启及失败恢复，不扩大为跨平台或性能验收。用户原有桌面进程未被停止；退出旧窗口后重新运行 `pnpm desktop` 才会加载新部署，无需清理 profile。
