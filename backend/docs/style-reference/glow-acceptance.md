# 静态辉光实施与有限验收

日期：2026-11-01。工作树 `071c`，分支 `docs/backend-style-sdk-reference`。范围仅 App 仓库后端、对应测试和文档；前端主工作树没有参与本次修改。

## 设计与源码

[外辉光规范](glow.md)和 [ADR-0243](../archive/decisions/decisions.md#adr-0243)记录算法、参数与兼容边界。完整链路包含 Core / Application / Contracts / Hosting / 持久化 / Worker 源快照 / SDK 只读投影 / Skia 原绘制和包材质、图像合成。未增加动态 Glow writer 或前端控件。

版本为产品 `0.110.0`、HTTP 合同 `1.42.0`、协议 `1`，工程/歌词/模板 schema `29/18/7`。模板目录 `templates-v7`，旧版本继续由当前格式门禁拒绝，没有新增自动迁移。固定包格式2、SDK声明1.0.0保持。

## 测试与合同

| 验证 | 结果 | 范围 |
|---|---|---|
| Core 完整测试 | 1341/1341 | 新值校验、继承、来源、copy/restore、token/mark、持久化、格式门禁、模板与包捕获配置 |
| Application 定向 | 115/115 | 四层编辑、whole/leaf/null/restore、原子拒绝、历史/no-op |
| Hosting 定向 | 74/74 | canonical/local、模板、标记外观与真实命令读取 |
| Server 定向 | 121/121 | strict JSON、颜色与完整字段、null、父子冲突与模板wire |
| Worker 源快照定向 | 115/115 | 完整静态源、固定预设、Glow往返与字段严格性 |
| SDK writer 定向 | 25/25 | 现有样式、场景与材质writer合同 |
| Skia CPU / Vulkan 定向 | 各176/176 | 新Glow与原StyleDesign、SceneCapture、ColorPart、PreparedStyle、MixedEffectPackage回归 |
| CLR metadata / 合同 | 通过 | 112 paths / 125 methods / 597 schemas / 3431 examples，2547反例 |

TRX 和定向日志保存在本工作树的 `backend/artifacts/glow-tests/`，不作为源码提交。最终生产源码提交 `54f2c647599272be9deb06560789557d67ebbaf9`；完整后端解决方案在最后渲染修改后再次构建，通过零警告/零错误。实际 SDK 根据 `global.json` 的 rollForward 规则解析到本机 `10.0.114`；配置最低值仍为 `10.0.112`，没有升级依赖。

新增像素断言覆盖透明填充仍有外光、字形内部扣除、sigma/spread、零值、公共透明度一次、演唱边界、多段Ruby、逐字覆盖和独立变换、相邻不同颜色字形、标记、超过64像素的外扩、覆盖通道、部件重组、包材质与图capture、预算释放及原生范围拒绝。CPU/Vulkan证据分别登记，不声称两后端逐位相同。

错误实现变异分别移除内部扣除、重复乘样式透明度、丢弃只读Glow、将Glow裁到演唱区域；各指定测试先失败，随后恢复生产代码。普通构建失败不计作变异成功。第五个变异恢复旧跨token绘制顺序，新部件重组对照测试失败；恢复后通过。

## 匹配发布与真实 HTTP

待最终生产源码提交、同源 self-contained Server/Worker 发布和 fresh `backend-glow-http-probe` 完成后登记。探针会使用独立profile和随机loopback，保留有限PNG、请求/响应、修订、历史、保存重开、颜色/外扩差异以及进程排水和凭证扫描证据。

## 边界

这是后端有限验证，不代表前端界面、浏览器、跨平台、任意字体/共享字簇、长片或性能验收。共享字簇无法独立改变某个token的Glow时明确失败。旧历史测试夹具被保留，因新schema/必填源成员不再宣称旧快照字节能够直接在新引擎解码；当前格式往返另有断言。
