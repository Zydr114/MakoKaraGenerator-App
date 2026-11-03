# ASS / TTML 歌词导入兼容修复

本轮修复 App 的 ASS 导入，产品 **0.110.2**、合同 **1.42.2**、HTTP 协议 **1**。后端实现提交 `7c11eaa`，前端、合同及 HTTP 回归提交 `0be255d`。工程／歌词／模板 schema 沿用 29/18/7；不改两个兄弟源仓库、Downloads 原文件和用户 profile。

## 原因与修复

新建工程旧入口只为 LRC、TTML 文件名放行浏览器未知类型上传，而 ASS 文件常以 `application/octet-stream` 上传，因此文件规划和新建准入不一致。现在前端为 ELRC/SRT/VTT/ASS/SSA 补上 `text/plain`，XML 补上 TTML MIME；后端新建允许登记的歌词扩展名使用 octet，未知二进制扩展和视频 MIME 仍拒绝。既有文件导入／导入规划的 octet 内容嗅探不收紧，避免破坏未知扩展名的旧流程。

MakoLrcStudio ASS 同时包含 `Dialogue ... Effect=fx` 绘制层和 `Comment ... Effect=karaoke` 源打轴。旧解析只读 Dialogue，导致绘制层、ruby 显示层被当成多行歌词，原始注音关联丢失。现在文件具有 Studio 头标识和 karaoke 源注释时，优先读取源事件，恢复：

- `正文|读音` 与 `#|后续读音`，生成一组 ruby 和独立注音片段。
- 空 `\k` 的停顿时间，以及裸 `#` 对正文和最后注音片段的延音；下一元素从推进后的时间继续。
- 普通 ASS 仍读取 Dialogue，保留字面的竖线；`\k/\kf/\ko` 使用原厘秒时间。

ASS 为部分读取：布局、绘制特效、分部元数据、翻译／罗马音字幕通过报告声明未映射；不新增 ASS 导出。不尝试从任意绘制坐标反推 ruby。Studio 源注释以 `|` 分隔注音但未对字面竖线提供无歧义转义，这一导出侧边界暂缓。

## TTML 暂缓的原因

Downloads 的 `Aorist.ttml` 和 `Sympatheia.ttml` 都是以下结构：

```xml
<tt xmlns="http://www.w3.org/ns/ttml" ...>
  <head xmlns="">...</head>
  <body xmlns=""><div><p ...>...</p></div></body>
</tt>
```

`xmlns=""` 清空默认命名空间，body/div/p/span 不再属于 TTML。App 严格查找 TTML body，得到零行及“TTML 缺少 body”；实际 HTTP 导入规划为 `EmptyImport`。MakoLrcStudio `src/domain/ttml.ts` 委托 `@applemusic-like-lyrics/ttml` 的 `TTMLGenerator.generate` 输出。本轮只读检查导出源码及真实文件，不改 Studio、不把无命名空间的标签猜成 TTML；按用户“若是 MakoLrcStudio 格式问题则暂时搁置”的要求保留这个拒绝。

诊断时仅在内存中去掉两个 `xmlns=""`，Aorist 可恢复 48 行、147 组 ruby、327 个片段；Sympatheia 可恢复 40 行（该 TTML 正文本身没有 ruby）。这些副本用于定位，不是修复后的原文件，更不能计为原文件成功。

有效 TTML 的 project／AMLL 导入导出本轮已做往返检查。project 保留领域身份、多来源及 raw 时间；AMLL 的损失计划仍适用。只有注音片段明确计时、基础 token 没有显式时间时，原片段时间保留，基础 token 仍为 Missing；不静默猜测。既有布局、歌手、sidecar 等未映射特性仍有损失报告，因此本轮不宣称任意 TTML2 文档全部无损。

## 验证

新增缺陷回归在旧实现先实际失败：ASS 最初 3 项中 2 失败；前端 6 个 MIME 用例失败；Hosting 的 ASS 新建返回不接受。审阅阶段又补出两个裸 `#` 时间推进失败，再修复通过。TTML 新增的是既有合法行为与非法命名空间拒绝的保护用例。

- `dotnet build MakoKaraGenerator.slnx --no-restore -m:1 -p:AllowMissingPrunePackageData=true`：零警告、零错误。普通恢复及代理恢复等待失败，最终使用本机现有固定依赖缓存；未升级依赖。
- Core 歌词回归 344、Application 导入 51、Hosting 导入／新建 56、Server 导入 HTTP 27，共 **478** 项通过。Server 首次按文件名过滤未匹配测试，改为真实 partial 类的方法名后执行；零匹配不计通过。
- 前端 MIME／边界／合同 3 文件 **3477** 项通过，TypeScript 检查通过。
- 当前源码新导出 CLR metadata，freeze 写入并复核；合同 **112** 路径／**125** 方法／**597** schemas、**3431** 示例（**2547** 反例）校验通过，前端同步完整副本。
- Downloads **20** 份 ASS、**3** 份 LRC 核心导入无 Error，TTML project／AMLL 往返逐值比较正文、token／ruby 片段及时间相等，project 完整文档相等；20 份 ASS 的行数／ruby／片段数另与源 karaoke 注释独立计数一致。文件名为 `Asgard ... enhanced.lrc` 的样本实际识别为普通 LRC，保留既有检测结果。
- 匹配发布的真实 HTTP 合成回归 **4** 项／**53** 次请求：两种上传 MIME、规划、新建、替换、撤销重做、project／AMLL 导出重导入；响应 schema 零问题。53 次往返的凭证字段全部使用 `***`／`Bearer ***` 脱敏；所观察自有进程身份在结束后均消失。
- 原始样本真实 HTTP **64** 次请求：Aorist ASS 为 **48/147/327**（行/ruby/片段），Sympatheia ASS 为 **40/99/195**；两者经两种 TTML 导出后重建，正文及全部时间内容摘要一致。3 个实际有效样本成功，两个原始 TTML 明确拒绝；7 个本次工程全部关闭成功，独立 Server 正常停止。
- 真实 Electron 使用独立临时 profile：合成 ASS 经新建检查进入歌词编辑器，正文只有一行且包含两个 token；选中基础字后显示两个独立 ruby 片段。原始 Aorist TTML 在文件检查中明确报告 body 错误。关窗正常退出，观察到的 11 个自有进程身份全部退出。截图经实际查看；本轮验证导入编辑流程，不验证成片渲染。

桌面脚本最初两次因折叠歌词列表和包含“新增”按钮的过宽选择器超时，第三次因上次清理未结束而端口占用；失败日志均保留。修正测试选择器并确认旧会话退出后，第四次完成上述 6 项检查。没有因这些脚本问题修改产品代码。

`pnpm build:desktop` 完成 Web、匹配 self-contained Server/Worker、固定 native GPU 接缝。发布目录为 `.local/backend-dist/20261103T082755358Z`，源码 `0be255d3197d52286341cd62080f352b317474b5`，`sourceDirty:false`。网络恢复不可用时，本次通过临时 `RestoreSources` 空目录与本机 NuGet 缓存恢复固定包，关闭在线 NuGetAudit；这不构成新的依赖漏洞审计。Web 构建仍有既有大 chunk 提示。

可提交的[验收摘要](evidence/2026-11-03-lyrics-import.json)包含样本哈希、计数、往返和桌面结果；原始歌词、私人 bootstrap、完整用户路径不进入 Git。本机完整证据位于 `.local/lyrics-compat/`，含 Core 实样摘要、独立 ASS 计数、构建／回归日志、合同校验、HTTP、`desktop4/` 截图与进程退出检查。可重跑的合成 HTTP 测试为 `frontend/test/integration/lyrics-compatibility.test.ts`。本轮没有做跨平台或性能验收。
