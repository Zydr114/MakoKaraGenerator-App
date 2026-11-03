# 前端后端开发交接文档

**当前工作：2026-11-01，产品0.110.0／API合同1.42.0／HTTP协议1。** 新增静态外辉光、四层继承、模板/文字标记及Worker渲染支持。合同为112路径、125显式方法、597 schemas、3431例；原工程命令种类不变。行为、实施和有限验收见[静态外辉光](../docs/style-reference/glow.md)，客户端入口见[样式系统参考](../docs/style-reference/README.md)。历史W8、GPU流及性能证据保持原归属。

历史合入：2026-10-27 将 `dev/gpu-render` 的 P3/P4/P5 及两批 GPU 工作快进纳入 `main`，完整保留 95 个提交；该次有定向回归，没有新生产修改、发布或 fresh HTTP，见[10月27日合入记录](../docs/archive/history/backend-main-integration-20261027.md)。2026-10-28 将 `dev/performance-optimization` 快进纳入 `main`，完整保留另外 19 个提交；该次仅作 Git 集成、入口文档和静态核验，见[10月28日合入记录](../docs/archive/history/backend-main-integration-20261028.md)。原版本的 HTTP、硬件、媒体和性能证据仍按各验收报告归属。

前端从本文件启动，再按 [调用速查](frontend-workflows.md) 实现工作流；逐字段依据 [执行合同](api-contract.md)、[生成清单](api-index.md)、[OpenAPI](openapi.json) 和 [详细行为合同](../docs/architecture/backend-api.md)。这些文件必须取自**同一提交**，并按 [manifest](contract-manifest.json) 校对指纹。Server 不托管 OpenAPI，也不提供浏览器可直接读取的公共 bootstrap 接口。

## 1. 交接结论与完成距离

后端可独立运行，不依赖 Avalonia、桌面播放器或物理声卡。前端通过 HTTP 完成工程、歌词/时间、样式/编排、媒体/字体、预览、正式任务、模板工坊及作者配置库操作；不需要读取服务端文件、解析私有存储、编译脚本或重写渲染规则。

| 交付层 | 当前依据 | 边界 |
|---|---|---|
| API 形状与行为 | 同一提交的 OpenAPI、执行合同、行为合同与 manifest | schema 校验不证明真实身份、修订、租约或运行结果 |
| 后端代表工作流 | [补齐计划 §8～10](../docs/archive/plans/backend-api-completion-plan.md#8-p1p2-实施记录) 的匹配 Server/Worker 与 fresh HTTP 证据 | P1/P2、模板工坊、P4 作者流程各保留自己的输入和证据范围 |
| CPU/GPU显式选择 | [验收报告](../docs/archive/acceptance/render-backend-api-acceptance-20261026.md)、[实施计划](../docs/archive/plans/backend-render-backend-api-plan.md)与[行为§25](../docs/architecture/backend-api.md#render-backend-selection)，本机有限fresh HTTP及GPU不可用负例通过 | 合同形状、设备可用性与真正业务渲染结果分别判断；浏览器结果另行验收 |
| P5 API 交接历史 | [P5审查报告](../docs/archive/research/backend-api-p5-closure-audit-20261025.md)及原版本证据 | 不折算为本版CPU/GPU API、浏览器、平台或性能验收 |
| 前端产品 | 独立 Web 仓库接入并验证当前合同 | 对端历史 W3 报告不能代替当前版本浏览器验收 |

### 1.1 各阶段的真实边界

当前交接包含：空工程和可选歌词/音频创建、流式资源上传、精确修订编辑/历史/持久化、授权目录、媒体背景、真实 Worker 字体检查、只读编辑候选和原子应用、完整包/预设/包字体的统一 Runtime 输入、预览/测量/验证/导出/分析、CPU/GPU显式选择与固定预览后端切换、完整模板定义与分享、作者草稿/固定源码/编译/试样/采用及个人配置库。

各任务必须读取实际部署的 capabilities 和 sourceProfiles。旧图片独立 profile 的 2 MiB/4096 预算、旧 package 无音轨短片限制，以及旧静态工坊范围，均按历史 profile 解释，不能作为当前统一 Runtime 输入的全局限制。任意格式、输入组合、完整工程等价与系统安装能力仍以对应证据为准。

## 2. 当前前端可以使用什么

完整端点与命令见 [api-index.md](api-index.md)；按用户动作查路径、结果和修订域见 [frontend-workflows.md](frontend-workflows.md)。详细语义集中维护于 [backend-api.md](../docs/architecture/backend-api.md)，本文件只维护交接入口和启动方式。

### 2.1 工程命令：当前九十六种（按能力组列出）

工程写入口为 `POST /api/v1/project-sessions/{id}/commands`，请求外层是 `expectedRevision`、`command:{type,...}` 和顶层 `requestId/hostInstanceId/validUntilUtc`；覆盖歌词正文/来源/元素/独立注音时间轴、时间动作、composition、媒体替换、样式、预设分配及场景编排；全部 96 个 type 由 [生成清单](api-index.md#当前工程命令) 和 OpenAPI 严格分型。修改同一工程须串行，携带精确 expectedRevision 和顶层回执三字段；真实变化一次历史，no-op 不增修订并保留 redo。

读模型包括摘要/历史、精确修订 branches/window/settings/style/resources、编排与局部展示、预设分配、同源波形及真实 Worker 同帧几何/PNG 测量。changeSet.requiresFullRefresh=true 时须重新读取所需模型，不能把空 ID 数组当作无变化。前端维护尚未提交的草稿、选择和播放头，后端拥有工程状态与历史。

### 2.2 预览：已有异步帧接口，完整实时体验尚未交付

当前预览来源包括工程、编辑候选、预设候选、完整模板试样和 authorDraft。统一 Runtime 可消费实际能力允许的媒体、完整包/预设与包字体；客户端不选择私有 Worker 编码、不提交主机路径或 grant。创建固定来源后，使用 frame-requests → 状态/SSE → 认证 PNG 的同一链路；工程编辑后须显式 PUT input。

先认证读取 `GET /api/v1/render-backends`，使用公开值 `cpu` 或 `gpu`（GPU实际API为Vulkan）。创建/任务省略或null使用启动默认；已有预览更新省略或null保留当前选择，候选测量省略沿候选固定选择。前端维护偏好，并以返回的固定 `renderBackend` 显示实际选择；历史缺失值显示未知。只切设备使用 `PUT /api/v1/preview-sessions/{id}/render-backend`，携带精确generation与metadata，保留原固定来源/媒体；成功后采用响应generation重新请求帧。可用性是检查快照，实际成功仍看帧和任务结果；GPU失败不回落CPU。流程见[速查§5.3](frontend-workflows.md#53-cpugpu选择和已有预览切换)。

generation、sourceRevision 和递增 requestSeq 共同控制晚到结果；seek 也须使用更大的序号并采用响应的新 generation。只显示仍匹配最新身份的帧，释放旧 Bitmap/Blob URL。预览最大尺寸、帧租约、诊断及并发限制读取 runtime-info/响应 limits；当前默认输出最大 1280×720、裸帧 4 MiB、PNG 租约 30 秒。没有固定 30/60fps 服务承诺。完整调用顺序见 [速查 §5](frontend-workflows.md#5-预览候选与同帧测量)。

### 2.3 其他前端约束

- 鉴权用于 capabilities、SSE、PNG、音频、成片和其它内容读取；用带头的 fetch 后接 Blob/播放器。凭证不得放入 URL、localStorage、前端仓库、公开日志或截图。
- SSE 使用 fetch 流；EventSource 无法附加所需认证头。事件只提示变化，缺口或重连后从 client/preview/job 状态重同步。
- 修订与大整数按各自 wire 类型处理，尤其不能把所有时间、种子、sourceRevision 统一转成 JavaScript Number；各域见 [速查 §2](frontend-workflows.md#2-身份修订和请求回执)。
- 超时、断流或 500 不证明写入未执行。保持原 requestId、载荷与截止时间重试，并读取权威状态；修改意图才使用新 requestId。
- 作者草稿、个人配置库、模板库和工程分别管理修订。profile 草稿发现与显式 claim 使用草稿/所有权 CAS；async 编译固定源码，202 后读取 live job/result；没有公开的编译结果 recovery 路由。保存/采用合法源码不要求成功编译或运行验证。源码历史、完整包文件编辑和精确修订 `.mkfx` 下载沿作者闭包路由执行。

## 3. 在本仓库启动 headless 后端

### 3.1 本机前提

从仓库根执行，SDK 基线为 [global.json](../global.json) 的 .NET 10.0.112/latestFeature。发布入口依赖 Python 3、.NET SDK、flock 和实际 native 环境；已发布包不需要共享 ASP.NET runtime。渲染仍需要字体、Fontconfig/FreeType/ICU 等系统依赖，媒体准备需要可信 ffmpeg/ffprobe。

Server 与完整 Worker 必须使用同一源码、版本、Configuration、RID 和 self-contained 模式。Server publish 不自动携带 Worker；保留 Worker 整个发布目录及 script-refs。以下是已验证的 Linux x64 入口，服务只监听 loopback，浏览器开发 Origin 要显式允许。

### 3.2 匹配发布 Server 与 Worker

选择一个**尚不存在**的新目录；以下两条命令必须使用同一个目录：

```bash
python3 tools/backend-web-host/host.py publish \
  --dist "$PWD/artifacts/web-backend/0.106.0-render-backend"
```

[host.py](../tools/backend-web-host/host.py) 分别发布 Release/linux-x64/self-contained Server 与 Worker，记录产品版本、源码提交、dirty 指纹和完整部署 SHA。发布失败保留日志，重试使用新目录；两端发布期间输入变化会失败。恢复依赖需要代理时，可设置 http_proxy/https_proxy 为 `http://127.0.0.1:7897`；loopback HTTP 直连。参数和 stdout 结构见 [启动器说明](../tools/backend-web-host/README.md)，其中旧版本目录名是历史示例，以本文件的当前版本和实际 manifest 为准。

### 3.3 稳定 profile 与前台运行

```bash
python3 tools/backend-web-host/host.py run \
  --dist "$PWD/artifacts/web-backend/0.106.0-render-backend" \
  --profile "$PWD/artifacts/web-backend/dev-profile" \
  --origin http://localhost:5173 --origin http://127.0.0.1:5173
```

入口验证部署 SHA，固定 `/usr/bin/ffmpeg` 和 `/usr/bin/ffprobe`（可用绝对路径参数替换），监听随机 loopback 端口。不要假定 5000。启动器清除继承的 MAKO_BACKEND_*，仅保留说明中列出的资源策略配置。上传默认单文件/总字节限额为 null；导出可空宿主策略同样以实际 capabilities 为准，不能套用旧 16 MiB 上传或 10 秒/600 帧全局限制。资源数量、并发、协议、TTL 和内容验证仍生效。

profile 的 state/ 与 projects/ 跨重启复用；runs/ 每次新建私有目录、bootstrap 和日志，同一 profile 拒绝并发宿主。stdout 的稳定就绪行是：

```text
BACKEND_READY <实际地址> <hostInstanceId>
WEB_BACKEND_READY {"addresses":[...],"hostInstanceId":"...","credentialFile":"...","runDirectory":"...","version":"0.106.0","pid":1234}
```

可信前端启动器可解析 WEB_BACKEND_READY，读取私有 credentialFile 并向明确允许的页面交付连接信息；不能把 bootstrap 做成任意网页可取的接口。ready.version 应与本次合同产品版本匹配。stdout 不含 token，日志位于 runDirectory/server.log。

使用 Ctrl+C 或向**启动器 PID**发送 SIGTERM，等待 Server、Worker 和 native 子进程排水及启动器退出后再重启。正常退出仅删除本次自有 bootstrap，保留持久数据和日志；不按进程名杀进程或清空用户目录。

### 3.4 最小握手与能力核对

在另一终端设置本次实际 credentialFile 路径；下面只输出身份和能力：

```bash
export MAKO_BACKEND_CREDENTIAL_FILE=/替换为本次实际路径/bootstrap.json
python3 - <<'PY'
import json, os
from urllib.request import Request, build_opener, ProxyHandler
opener = build_opener(ProxyHandler({}))
with open(os.environ['MAKO_BACKEND_CREDENTIAL_FILE'], encoding='utf-8') as stream:
    bootstrap = json.load(stream)
base = bootstrap['addresses'][0]
def read(path, headers=None, body=None):
    payload = None if body is None else json.dumps(body).encode()
    with opener.open(Request(base + path, data=payload, headers=headers or {}), timeout=15) as response:
        return json.load(response)
print('health:', read('/api/v1/health/live'))
client = read('/api/v1/clients', {
    'Authorization': 'Bearer ' + bootstrap['bootstrapToken'],
    'Content-Type': 'application/json'
}, {'protocolVersion': '1'})
headers = {'Authorization': 'Bearer ' + client['accessToken'],
           'X-Mako-Client-Id': client['clientId'],
           'X-Mako-Host-Instance-Id': client['hostInstanceId']}
print('client:', client['clientId'], client['hostInstanceId'])
for path in ['/api/v1/capabilities', '/api/v1/runtime-info', '/api/v1/render-backends',
             '/api/v1/validation-capabilities', '/api/v1/export-capabilities',
             '/api/v1/analysis-capabilities']:
    print(path, json.dumps(read(path, headers), ensure_ascii=False))
PY
```

health/ready 只证明宿主存活；render-backends只公布本次设备可用性，不能代替某个预览/任务的实际渲染结果。创建音频工程前核对 audioPreparation.enabled；预览和正式任务分别核各自能力、sourceProfiles、formats/quality 与 limits。configured-unverified 不能代替真实 PNG 或任务结果。

登记返回的 serverTimeUtc 用于生成请求截止时间，tokenExpiresAtUtc/leaseExpiresAtUtc 用于安排 heartbeat。默认 access 为 120 秒、lease 为 300 秒；heartbeat 不返回新 token，resume 轮换 access。服务重启后旧 access 可能先 401 unauthorizedClient；通过可信启动器取新 bootstrap、重新登记和比较 hostInstanceId，不能只等待 hostRestarted。

工程文件按当前格式/产品版本门禁读取，不能手改版本字段冒充兼容。验证/导出前明确设置正 outputDurationUs；省略/null 表示尚未确定，不自动取音频或歌词末尾。保存 → 关闭 → 重开各自等待前一步 job 成功。

## 4. 调整后的开发步调

前端按 [调用速查](frontend-workflows.md) 接入同一提交的1.40.0合同：连接/状态/SSE和工程编辑后，读取render-backends、维护CPU/GPU偏好，接通固定预览切换及任务/报告选择回显，再验证媒体、完整模板工坊与作者配置库。W8 作者路由的后端 fresh HTTP 与浏览器集成分别验收；mock、规划 API 或内部 Worker 协议不能记为公共接口成功。

P1～P4 实现与匹配发布证据保留在 [补齐计划](../docs/archive/plans/backend-api-completion-plan.md)；前端浏览器、用户流程、跨平台安装及性能验收由对应项目独立登记，不重复把旧有限切片作为当前限制。

## 5. 文档与代码导航

| 文件 | 前端用途 |
|---|---|
| [frontend-workflows.md](frontend-workflows.md) | 当前用户动作、调用顺序、结果读取、修订域、恢复与错误处理 |
| [api-contract.md](api-contract.md) | 冻结执行规则、兼容策略、证据边界 |
| [api-index.md](api-index.md) | 全部路径/方法/认证/请求/响应与 96 个工程命令 |
| [openapi.json](openapi.json) / [examples.json](examples.json) | 客户端类型与 wire 校验；合成例子中的身份/时间/hash不可直接发请求 |
| [contract-manifest.json](contract-manifest.json) | 同一提交的源文件和行为/生成物指纹 |
| [详细行为合同](../docs/architecture/backend-api.md) | §1～5 公共传输/鉴权/回执/SSE/资源；§6～12 编辑/持久化/预览/正式任务；§13/17～19 编排与业务；§21 作者；§23/24 统一Runtime与完整模板；§25 CPU/GPU选择 |
| [CPU/GPU实施计划](../docs/archive/plans/backend-render-backend-api-plan.md) | 本批选择语义、提交与分层验收状态 |
| [API 目标目录](../docs/backend-api-target-catalog.md) | 全部动作/端点与 P1～P5 变动归属 |
| [补齐计划及实施记录](../docs/archive/plans/backend-api-completion-plan.md) | P1/P2、P3、P4 的实际提交、匹配发布与 fresh HTTP 范围 |
| [backend-web-host](../tools/backend-web-host/README.md) / [部署配置](../docs/architecture/backend-api.md#15-配置与部署) | Linux x64 发布/启动、资源策略、ready 与正常退出 |
| [历史交接快照](handoff-history.md) / [实施历史](../docs/archive/history/backend-extraction-progress.md) | 原版本能力、失败过程、旧验收数字及对端历史报告 |
| [Server](../src/MakoKaraGenerator.Backend.Server) / [Contracts](../src/MakoKaraGenerator.Backend.Contracts) / [Hosting](../src/MakoKaraGenerator.Backend.Hosting) | 路由、公开 DTO、状态和生命周期实现；Worker DTO为私有 IPC |

前端固定同一提交的上述合同文件生成类型并记录生成器版本。库更新不静默追改工程快照；新 API 或行为变更先形成可审阅合同差异，再同步行为源、OpenAPI、例子和 manifest。API 详情和当前能力不得取自 [未来草案](../docs/archive/plans/backend-api-v1-draft.md) 或历史 runbook。

## 6. W0 后端缺口临时补齐计划（依据 Web 设计 §9）

本节保留旧链接入口。原 G1～G6 施工顺序与 0.75.1 证据已移至 [历史快照 §6](handoff-history.md#6-w0-后端缺口临时补齐计划依据-web-设计-9)，不作为当前待办清单。

### 6.1 G1 实际合同与验收边界

当前 G1 仍只在新登记配额不足时尝试回收过期且无工程/任务、有效回执、在途请求或资源保护的 client；不自动 discard、不删除持久工程/ledger/报告。实际回收后旧 access/resume 都拒绝；有保护依据的身份可以保留并 resume。完整语义见 [行为 §3](../docs/architecture/backend-api.md#3-客户端登记心跳与状态)，旧证据见 [历史快照 §6.1](handoff-history.md#61-g1-实际合同与验收边界)。

### 6.2 本批后端启动与 HTTP 证据

此旧锚点原指 0.75.1/G1，原数字与失败过程保留于 [历史快照 §6.2](handoff-history.md#62-本批后端启动与-http-证据)。当前 P1～P4 证据从 [补齐计划 §8～10](../docs/archive/plans/backend-api-completion-plan.md#8-p1p2-实施记录) 读取；合同校验、真实 HTTP、像素、媒体与浏览器结果分别判断。

<a id="collaboration-established"></a>

## 7. 前后端协作建立记录（2026-10-16）

Codex 负责本后端仓库，Claude 负责独立 Web 前端；跨仓职责与交付规则见 [AGENTS.md §8.4](../docs/archive/history/AGENTS-20261029-history.md#frontend-backend-collaboration)。前端只同步固定提交的合同副本，不直接修改后端；后端 API 的字段、业务与生命周期以本仓库真实实现为准。

Agent Mail 首次双向联系、PING/PONG、确认及历史项目身份保留于 [历史快照 §7](handoff-history.md#collaboration-established)。任务用连续 G#/R# 与独立线程；投递/收悉不代表浏览器验收或自动唤醒。交付须给出提交/版本、兼容变化、部署/启动方式、是否需重发匹配 Server/Worker、真实 HTTP 证据与限制，前端整体同步合同后独立回报客户端及浏览器结果。
