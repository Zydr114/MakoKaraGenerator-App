# API 补齐代表 HTTP 流程

复用 `backend-basic-business-http-probe` 的上传、二进制请求记录、工程命令、保存/重开和凭证扫描，沿其继承链使用 `backend-web-host/smoke.py` 与 `host.py` 的匹配部署校验、随机 loopback、bootstrap、认证、进程身份观察和正常停机。没有第二套启动器或请求框架；完整歌曲只将既有 job 轮询模式延长，并每30秒 heartbeat 延续默认凭证/lease。`--phase p3` 和 `--phase p4` 只运行各自增量，不重放 P1/P2。

先准备并提交探针，等待本轮匹配 Server/Worker 发布后再运行：

```bash
python3 tools/backend-api-completion-probe/probe.py \
  --dist "$PWD/artifacts/api-completion/dist" \
  --output "$PWD/artifacts/api-completion/http-final1"
```

`--output` 必须不存在。工具不构建或发布，不修改工程文件。需要 Linux、Python 3、PATH 中的 ffmpeg/ffprobe 和经过 `host.py` 校验的 self-contained 发布。`--job-timeout-seconds` 默认1800，仅约束探针等待时间，不改变部署策略；Server 导出默认资源策略保持 null。探针先前准备时的源码基线为 `fb9af6c`；实际发布应包含本轮空工程创建修复及所有 P1/P2 目标实现，运行结果从部署 manifest 登记。

流程：

- 公开直接创建无素材空工程；上传原创增强 LRC，读取导入规划及完整报告/预览，再导入18句覆盖180秒的歌词，补充准确行/元素时间。
- 上传自制180秒、48kHz PCM16 双声道正弦音频（34,560,044字节，超过历史16MiB上传上限）并替换，核实际准备的时长/样本数；上传原创 PNG，设置背景、修改 fit，读取配置和资源完整字节。
- 实际 Worker 字体目录及整形检查。检查请求显式包含全部八字段；歌词规划、字体检查和测量均不携带 metadata。
- 编辑候选临时清除图片并改 Color/名称，正式工程仍保图片；请求候选帧与同一候选的真实 Worker 测量，完整 RGBA 对比；确认工程 summary/history 未改变。原子应用相同编辑，单次 undo/redo 核全部设置；再恢复图片。
- 保存、关闭、重开，核完整歌词/时间/背景设置及背景资源字节，取得实际背景预览 PNG。
- 同一已保存图片／完整音频输入先启动180秒导出，观察 running 与实际 live Worker 后主动取消，等待 cancelled 并核无报告／媒体；随后180秒、320×180、24fps、带音轨 MP4 standard 实际导出；Color 来源的2秒透明 ProRes及 draft/high 各一短片。原始报告/媒体 size/SHA 与下载引用一致，ffprobe 实际读取完整帧数、时长、尺寸、帧率与音视频 codec；透明片另在0.5秒解码一帧检查透明和可见像素。
- 最终保存关闭工程，沿既有 SIGTERM 停机检查所有观察到的 Server/Worker/子进程身份退出，并扫描凭证、固定 evidence SHA 清单。

180秒/24fps 是本轮完整代表歌曲，2秒片只核其格式/质量，不外推所有输入组合、浏览器、跨平台或性能矩阵。候选 Color 测量不代表 Image 测量支持；预览/测量像素一致性不等于独立 Runtime 内容 oracle。生成内容为本工具自制，无外部版权素材。

探针准备阶段仅能确认语法、CLI 和确定性输入；提交探针不代表真实 HTTP/Worker 流程已通过。运行失败保留当前 output，修复后另建目录，不覆盖旧证据。

可增加一组统一运行输入代表组合（同一服务生命周期与歌曲工程）：

```bash
python3 tools/backend-project-image-preview-probe/build_oracle.py \
  --dist /absolute/matched-dist --output /absolute/new-oracle
python3 tools/backend-api-completion-probe/probe.py \
  --dist /absolute/matched-dist --output /absolute/new-http \
  --oracle /absolute/new-oracle/publish/FixtureOracle.dll \
  --package-font /usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc
```

复用已有 image FixtureOracle 的生成／原 Runtime 渲染入口；新增 `completion-template` 仅把已有 scale 包 fixture、TokenFamily 高级配方（固定最大 uint64 seed、分支输入）与实际本机字体写入完整 `.mktpl`，经公共上传／preflight／import／apply进入工程。先验证 Color＋包字体，再验证视频＋同一高级预设／字体，三帧前进／回退逐完整 RGBA 对照原 Runtime，同时间测量逐 RGBA 对照预览、layout 测量、普通／透明各2帧 validation及48帧带音轨短导出。字体文件不下载；该代表命令将 Noto 内部家族等长改名为系统未安装的 Mako 家族，原文件／固定字体 SHA 与字节数写入fixture证据；该组不构成任意字体／完整输入交叉矩阵。

P3 工坊增量独立运行，不重放 P1/P2 或180秒导出。先对同一匹配发布构建已有 Oracle（已新增 `p3-package` fixture 入口），再运行：

```bash
python3 tools/backend-api-completion-probe/probe.py \
  --phase p3 --dist /absolute/matched-dist --output /absolute/new-p3-http \
  --oracle /absolute/new-oracle/publish/FixtureOracle.dll
```

此阶段复用原 `MixedEffectPackageFixture` 生成原创 controller/material/graph `.mkfx`，经公开上传和 inspect 取得固定包引用；完整模板 create/get/replace 保留包材质、两条配方的顺序/参数、最大 UInt64 seed 及未选样式。原工坊 options 提供单/多行目标，两者均请求实际 Worker PNG。短歌词工程验证 clips/高级 node 的只读候选与正式应用逐完整 RGBA 一致、readback、库更新不追改工程和显式 update；随后 `.mktpl` 导出/导入比较完整定义（仅归一化服务端包来源引用），保存并正常关闭。启动、认证、二进制证据、Worker身份观察、排水、凭证扫描和证据清单仍由原工具统一处理。

探针提交与语法/fixture 构建通过不代表 P3 fresh HTTP 已通过；每次运行必须使用新 output，实际提交、部署与结果由该目录 summary/evidence manifest 登记。

P4 作者与个人配置库代表流程独立运行，同样只需要 `p3-package` Oracle：

```bash
python3 tools/backend-api-completion-probe/probe.py \
  --phase p4 --dist /absolute/matched-dist --output /absolute/new-p4-http \
  --oracle /absolute/new-oracle/publish/FixtureOracle.dll
```

该流程要求部署 manifest 对应的 author compiler、authorDraft preview 和 `author-script-sample-v1` Runtime validation Worker capability 均已实际发布；能力缺失时应记录为部署前置失败。流程上传并 inspect 原创 `.mkfx`，验证作者草稿列表、完整 C# 源码读取与新修订、配置字段（参数 JSON、目标、读取范围、分支输入、最大 UInt64 seed、层），同步编译和固定源码的异步编译结果；异步任务接受后会推进源码修订，确认旧结果仍绑定旧 `draftRevision/sourceRevision`。随后使用服务端返回的 sample/target 创建 `authorDraft` 预览并换一次样本，执行一组 360 帧正式验证、报告/诊断读取和第二组验证取消，显式采用源码修订。

流程还覆盖个人配置库的 create、旧修订 CAS 拒绝、replace、copy、rename、delete，并从复制后的配置创建第二个作者草稿；另创建/读取 `ink` 材质配置，核无控制器绑定/订阅/种子，再从该配置创建/读取材质草稿，核三种 material slot 与单/多行非空目标。每次配置库写入后重新读取当前修订的包引用，复用 P3 full template 命令保存控制器配方与 ink 材质。完整模板回读核配方的 `configurationId`、完整样式及包文件身份；包来源由服务端规范化为模板库引用。随后请求已保存模板的试样 PNG，将模板应用到一行两秒歌词工程，并取得实际工程预览 PNG。

准备阶段的语法和合同形状检查不代表 P4 fresh HTTP 通过。实际运行只覆盖上述 Linux matched Server/Worker 代表流程；浏览器、重启恢复、任意脚本隔离、跨平台、任意输入组合和性能矩阵仍独立。每次运行必须使用新的 output，失败证据保留。受影响回归完成并提交后，可按以下顺序准备新的匹配发布、Oracle 和 HTTP 证据目录（示例目录均须不存在）：

```bash
python3 tools/backend-web-host/host.py publish \
  --dist "$PWD/artifacts/p4-acceptance/dist-r1"
python3 tools/backend-project-image-preview-probe/build_oracle.py \
  --dist "$PWD/artifacts/p4-acceptance/dist-r1" \
  --output "$PWD/artifacts/p4-acceptance/oracle-r1"
python3 tools/backend-api-completion-probe/probe.py \
  --phase p4 --dist "$PWD/artifacts/p4-acceptance/dist-r1" \
  --oracle "$PWD/artifacts/p4-acceptance/oracle-r1/publish/FixtureOracle.dll" \
  --output "$PWD/artifacts/p4-acceptance/http-r1"
```
