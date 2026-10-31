# 本机Backend.Server启动与协议访问

以下保留0.60.0本机服务的启动例子；当前0.71.0全部已实现路由、DTO、认证与配置只维护于[统一API规范](architecture/backend-api.md)，静态Worker部署要求见[Server合同](backend-preview-server-static.md)，正式验证任务与实际终态见[实施§19](backend-extraction-progress.md#b4-static-validation-0650)，独立静态导出接缝及有限出口终态见[实施§20](backend-extraction-progress.md#b4-static-export-0660)／[独立合同](backend-export-task-contracts.md)。独立音频分析任务及其证据见[实施§21](backend-extraction-progress.md#b4-audio-analysis-0670)，结果不自动应用工程，固定PCM16 WAV有限出口与最终SHA已验收。0.68显式采用／清除及保存重开、clear后静态恢复有限出口见[实施§22](backend-extraction-progress.md#b4-analysis-apply-0680)；当前commands支持五种具名操作，该批采用不扩大static渲染profile。0.69完整分析静态profile／纯视频保声明／真实旧Worker能力门禁及source部署证据见[实施§23](backend-extraction-progress.md#b4-analysis-static-profile-0690)，0.70固定MP3／native FLAC独立分析及旧WAV兼容见[实施§24](backend-extraction-progress.md#b4-compressed-analysis-0700)，该0.70历史批次includeAudio:true仍PCM16 WAV；0.70.1两Backend默认prepare入口的自包含卫生／未发布candidate释放有限完成见[实施§25](backend-extraction-progress.md#b4-self-contained-preparation-0701)，六家族受限probe／decode保原采样率，不套analysis预算；App／Media默认不变，仍Host直接native，准备Worker／deadline另批。0.71固定MP3 LayerIII／native FLAC音轨export的有限出口见[实施§26](backend-extraction-progress.md#b4-compressed-export-0710)：完整strict源采样帧预算300秒、双execution profiles，保旧WAV／纯视频；当前Worker部署须匹配实际EngineIdentity、双source／三audio／双execution profiles，旧0.70.1 Workerdisabled。修后正常solution与新540／原186、真正HTTP385请求3560断言及独立审查／最终SHA已核。完整B3／B4仍未完成。本文件是启动说明，不另作当前协议列表，也不代表Web界面或跨平台打包完成。服务不创建Avalonia窗口或物理声卡，默认媒体准备仍要求本机FFmpeg／ffprobe。

## 1. 发布与启动

本机缺ASP.NET运行时也可以使用独立包，无需安装root权限的软件。以下Linux x64命令从仓库根执行：

```bash
dotnet publish src/MakoKaraGenerator.Backend.Server/MakoKaraGenerator.Backend.Server.csproj \
  -c Release -r linux-x64 --self-contained true -m:1 \
  -o artifacts/backend-server/0.60.0
```

为每次运行创建独立临时资源目录；恢复profile与临时资源分开，不能跟着每次host身份变化。选择自己允许服务打开／保存工程的目录，并替换下例的绝对路径：

```bash
mako_backend_run_dir=$(mktemp -d "${XDG_RUNTIME_DIR:-/tmp}/makokara-backend.XXXXXX")
export MAKO_BACKEND_RESOURCE_ROOT="$mako_backend_run_dir/resources"
export MAKO_BACKEND_CREDENTIAL_FILE="$mako_backend_run_dir/bootstrap.json"
export MAKO_BACKEND_STATE_ROOT="$HOME/.local/state/MakoKaraGenerator/backend"
export MAKO_BACKEND_ALLOWED_ROOTS='{"projects":"/absolute/path/to/projects"}'
./artifacts/backend-server/0.60.0/MakoKaraGenerator.Backend.Server --urls http://127.0.0.1:0
```

准备完成后标准输出仅给出 `BACKEND_READY <实际地址> <hostInstanceId>`；使用其中的实际地址与Host。`0.0.0.0`、通配地址及非loopback监听被拒绝。`MAKO_BACKEND_ALLOWED_ROOTS`是可信启动配置，HTTP不能增加根；目录句柄只接受根内合法相对路径，不接受绝对路径、`..`或链接。App与服务同时打开同一工程会产生写租约冲突；只读打开可用于查询。

随机启动凭证写入 `MAKO_BACKEND_CREDENTIAL_FILE` 指定的新文件；Unix创建权限为0600，已有文件不覆盖。默认未设置该变量时位于临时资源根的 `bootstrap.json`。可信启动器也可通过 `MAKO_BACKEND_BOOTSTRAP_TOKEN` 环境变量交付32～256字符的凭证；此时默认不生成凭证文件。凭证不放URL或日志。

默认恢复profile为 `LocalApplicationData/MakoKaraGenerator/backend/state`，与App的profile独立；显式 `MAKO_BACKEND_STATE_ROOT` 可固定另一目录。正常终止使用SIGTERM，服务先停止接受新操作，再等待已接受保存事务。重启更换hostInstanceId，旧客户端和写请求失效；恢复候选／任务账本按实际持久化内容查询，不自动重放结果不明的写入。

## 2. 登记客户端与查询能力

`GET /health/live`只提供最小存活信息。登记使用 `POST /api/v1/clients`、JSON正文 `{"protocolVersion":"1"}`，Authorization Bearer值为启动凭证。成功结果含clientId、hostInstanceId、accessToken、resumeToken与实际截止时间。后续受保护请求，包括capabilities、SSE和媒体，都使用访问凭证与 `X-Mako-Client-Id`；可同时携带 `X-Mako-Host-Instance-Id`，拒绝旧主机身份。

下面的Python例子直接从私有启动文件读取凭证，只打印能力和不透明身份，不打印凭证：

```python
import json
import os
from urllib.request import Request, urlopen

with open(os.environ["MAKO_BACKEND_CREDENTIAL_FILE"], encoding="utf-8") as stream:
    bootstrap = json.load(stream)
endpoint = bootstrap["addresses"][0]
request = Request(endpoint + "/api/v1/clients",
    data=json.dumps({"protocolVersion": "1"}).encode(),
    headers={"Authorization": "Bearer " + bootstrap["bootstrapToken"],
             "Content-Type": "application/json"}, method="POST")
with urlopen(request) as response:
    client = json.load(response)
headers = {"Authorization": "Bearer " + client["accessToken"],
           "X-Mako-Client-Id": client["clientId"],
           "X-Mako-Host-Instance-Id": client["hostInstanceId"]}
with urlopen(Request(endpoint + "/api/v1/capabilities", headers=headers)) as response:
    capabilities = json.load(response)
print(client["clientId"], client["hostInstanceId"])
print(capabilities["productVersion"], capabilities["features"])
```

默认访问令牌120秒、客户端租约300秒。已认证客户端向 `POST /api/v1/clients/{clientId}/heartbeat`发送心跳续期；令牌失效后用启动凭证再次登记，正文增加原resumeClientId与resumeToken，取得同一clientId的新访问凭证。旧访问凭证随即失效，未保存工程保持；已断开页面不会自动discard。

## 3. Web Origin、SSE与音频字节

普通HTTP客户端可省略Origin。浏览器开发地址须由启动者显式配置，例如 `MAKO_BACKEND_ALLOWED_ORIGINS=http://127.0.0.1:5173`；多个完整Origin用分号分隔。Origin不包含路径、query或通配符。服务只对允许的Origin响应CORS，不把当前启动凭证交给任意网页。

SSE使用带上述鉴权头的 `fetch`流请求 `GET /api/v1/events`；重连携带Last-Event-ID或after游标。每条普通事件有id、event与JSON data，缺口发送 `resync.required`并结束该流；先读取客户端state再按返回eventCursor订阅。流会在有界心跳周期内重新检查访问凭证，失效发送 `client.expired`后关闭；恢复凭证后重新连接，不能将断流解释为工程关闭。

音频等资源通过 `GET /api/v1/assets/{assetId}/content`读取，同样携带访问凭证和clientId；Range示例为 `Range: bytes=0-4095`。支持GET／HEAD、ETag、If-None-Match与单段Range／If-Range；过期资源返回410。读取响应持有资源引用到结束，工程引用也会保护资源。当前方式适用于fetch字节；没有全服务token的URL或直接HTMLAudio票据接口。

## 4. 当前写入与恢复边界

写请求必须有requestId、hostInstanceId及validUntilUtc；JSON入口使用Metadata，时间命令沿原顶层字段，上传使用 `X-Mako-Request-Id`／`X-Mako-Host-Instance-Id`／`X-Mako-Valid-Until-Utc`。重试须保持同一操作、载荷和截止时间；请求ID在同一client跨操作共享，不可改ID盲目重放未知结果。capabilities公布实际配额、重试窗口和资源／事件上限。

目录入口为file-roots／file-handles及open-jobs；save-jobs捕获预期修订，close显式指定requireClean／save／discard和活动任务处置。202仅表示已接受，查询 `GET /api/v1/jobs/{jobId}`确认终态。保存的committing阶段不可取消，期间编辑不会被保存结果覆盖。recovery-candidates与recovery-jobs提供当前profile实际保留的恢复信息；主机重启后重新登记，结果不明任务由实际工程／输出身份核对。

工程仍要求精确产品版本一致。0.60.0验证须使用该版本创建的工程，不能把旧0.59.0文件当作自动迁移输入。预览／Worker／帧、导出与作者API按后续阶段接入，以capabilities和实施记录为准。
