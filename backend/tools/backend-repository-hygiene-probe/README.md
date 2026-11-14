# 仓库卫生有限 HTTP 验收

本工具接受 `tools/backend-web-host/host.py publish` 生成的匹配、clean、self-contained
Server／Worker 发布，使用该入口的启动器、认证、脱敏证据、任务终态、进程观察、SIGTERM
排水与凭证扫描设施。不会改动旧 compressed-analysis／export 验收工具及其历史范围。

```bash
python3 tools/backend-repository-hygiene-probe/probe.py \
  --dist "$PWD/artifacts/repository-hygiene-20261029/dist-final1" \
  --analysis-oracle "$PWD/artifacts/repository-hygiene-20261029/analysis-oracle/RuntimeOracle" \
  --output "$PWD/artifacts/repository-hygiene-20261029/http-final1"
```

`--analysis-oracle` 必须是从当前生产源码独立新发布的
`tools/backend-analysis-http-probe/RuntimeOracle/RuntimeOracle.csproj` 可执行文件。工具保存其
完整部署文件指纹及源码输入；发布来源由本次验收记录登记，不从二进制文件名推定。
输出目录必须不存在，每次失败保留原证据，修复后使用新的目录。

有限矩阵复用仓库已有的完整 M4A AAC／ALAC、Ogg Vorbis／Opus 四份固定音频。每份都经过
真实上传、Worker 音轨准备、工程创建、Host 分类、实际 Worker 分析，完整 canonical 字节及
全部采样／hash／point 元信息与原 `AudioFeatureAnalyzer.CreateDefault` 的独立结果一致。
每个工程显式固定 CPU，再导出 0.1 秒／30fps／3 帧 MP4，保留带音轨的完整报告、下载长度
与 SHA-256、独立 ffprobe 音视频轨与帧数，以及完整视频／音频解码长度和摘要。实际准备后的
音频时长须覆盖 0.1 秒；测试不以容器声明时长推定覆盖。

首个工程额外运行普通1帧＋透明1帧正式验证，两个诊断分页路由都核对重复 cursor／limit
优先拒绝、严格整数拒绝的原文案及 field，以及合法整数／未知查询参数的既有策略。

同一服务生命周期还运行一个有限的两库保存／重载切片：使用 tracked 的
`samples/ScriptEffectsV2/WordEmphasis` 完整包原字节，公开上传并检查文件清单；个人脚本配置库和
个人模板库各自经历 create、旧 revision 的409拒绝（拒绝前后完整目录 inventory 字节相同）、replace，
读取完整配置／模板定义、包闭包、入口 SHA 与全部包文件。随后正常停机，以同一个 profile 启动新 host
和新 client，重新读取两库并逐字段、逐文件核对。该切片不编译或执行脚本，也不覆盖作者草稿恢复。

运行前后核验全部生产输入、当前工具及其导入 helper／固定音频／Oracle 源码、完整发布与
Oracle 目录文件字节不变。文档整理不在生产冻结范围；本目录 README 属于工具冻结输入，
运行期间不能编辑。正常退出后记录所有观察到的 PID／startTicks 排水和凭证扫描；进程采用
既有 20ms 采样，只对实际观察到的身份作退出结论。

本工具不覆盖负例分类器触达（该项由旧实现红／修复绿单测证明）、独立渲染像素或有损音频
质量对照、浏览器、跨平台、长媒体、性能、恢复矩阵或完整仓库运行验收。

HTTP轮询期间每30秒通过当前认证 client heartbeat；只在当前有限任务等待时维持默认120秒访问令牌，
不改变生产TTL。停机、冻结完整性和凭证扫描分别执行；某一步清理失败会保留独立失败文件并汇总到
`manifest.json`，不会覆盖主流程失败。
