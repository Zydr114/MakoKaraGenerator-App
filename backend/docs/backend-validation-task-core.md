# B4-A 静态工程正式验证任务接线

> **0.69增量：** 下文首批单profile／拒分析／预算记录保留其历史范围；当前B4-E已增加完整`static-lyrics-scene-analysis-v1`，旧`static-lyrics-scene-v1`仍仅null。唯一snapshot-v1／Source自动选profile、Available canonical／Failed／NoStream完整保真，总4MiB深200k及实际wire同规则闭口，不截断。public旧sourceProfile保留并增sourceProfiles，私有export真实probe匹配EngineIdentity与双profiles，旧Worker不虚称兼容；purevideo保完整声明但禁止源音频IO，true grant重绑保Media身份，sourceSHA为原编码。preview／validation扣24／32头admission，export按实际metadata＋32在spawn/send前guard，不能承诺所有动态封包均admission拒绝。实际有限559／559及真正HTTP232请求／1864断言、三片各30完整解码帧／首帧oracle见[实施§23](archive/history/backend-extraction-progress.md#b4-analysis-static-profile-0690)／ADR-0213；当前HTTP只维护于[统一API](architecture/backend-api.md)。新profile不表示音频驱动视觉或完整B3／B4完成。


本切片使用原 `RuntimeFrameValidator` 在受管理 Worker 中运行两个独立会话，按工程帧率从时间零采样普通和透明输出。报告只对捕获的来源修订和指定帧计划成立，不代表完整成片、脚本或媒体闭包已通过。

## HTTP 与授权

- `POST /api/v1/project-sessions/{id}/validation-jobs`：`expectedRevision`、`framesPerOutput`、`metadata`；只允许既有工程来源，不接受路径或快照。202 返回共享 `JobResponse`，重试由既有 client 级回执固定。
- `GET /api/v1/validation-capabilities`：实际能力、固定配额、超时和禁用原因。
- 原 `GET /api/v1/jobs/{id}`、`POST /api/v1/jobs/{id}/cancel`、client state 和 SSE 继续共用唯一任务集合与账本；事件增加 `job.progress`，不传完整报告或像素。
- `GET /api/v1/jobs/{id}/validation-result`：完整报告原始 JSON 字节；大小与 SHA-256 等于终态报告引用。当前宿主任务严格按 client 所有权读取。
- `GET /api/v1/jobs/{id}/validation-diagnostics?cursor=...&limit=...`：实际发生诊断分页，默认及上限128；游标绑定任务和报告 SHA-256。非法、重复参数及外来游标明确拒绝。
- `GET /api/v1/recovery-jobs/{oldJobId}/validation-result`：沿既有本机 profile 恢复授权读取前一宿主账本登记的报告。新的 bootstrap 授权客户端可读取这些旧报告；旧客户端凭证不跨宿主恢复。此路由不回退当前宿主任务，不曝光孤立文件，不重放任务或把 unknown 改成成功。

## 接受、取消和提交

在同一工程 Writer 中验证精确修订、静态 profile、固定时长、帧计划、画布和快照预算，取得音频资源引用并登记共享 Job，避免关闭与登记竞态。快照音频字段是描述信息，本 profile 不读取音频文件。已接受任务不受 HTTP 断连、请求有效期过去或 client 租约到期影响。

调度仅管理执行许可，没有另一套任务状态或账本。实际执行上限为全局2、每client1、每project1；排队独立上限为全局16、每client4。被同client或project阻塞的排队项不占用其他client的全局执行许可。

显式任务取消产生 cancelled；Worker 自身截止时间产生 failed 和稳定 `WorkerTaskTimedOut`。project close 默认拒绝活动任务，wait 与 cancelAndWait 复用原生命周期；批取消在共享任务闸门内执行，避免刚释放许可的排队任务复活。宿主关闭主动取消正式验证、排水 Worker 后释放来源资源。

执行器必须先释放两个 Runtime 会话和 Worker，才返回完整报告。Hosting 再释放音频引用、校验完整报告预算、执行最后取消检查，并通过原 committing/cancel 原子闸门发布 sidecar 和记录终态。未通过的真实报告也发布，任务为 failed；预算、执行或发布错误不伪造报告。账本终态写入失败沿旧规则为 unknown，已经发布的报告不会反向回滚。

## 有限预算与保留

每输出1..600帧，双输出最多1200；最后一帧不超过工程显式正时长。画布不超过1920×1080，本地像素预算8MiB；任务快照扣除32字节私有头部后最多4194272字节。完整报告最多256KiB、256问题、512实际发生诊断，全局 sidecar16MiB，不截断报告。

`MAKO_BACKEND_VALIDATION_TIMEOUT_SECONDS` 仅由宿主配置，1..60、默认60秒，能力端点公布实际值；取消宽限2秒。Worker 路径复用 `MAKO_BACKEND_WORKER_PATH` 的绝对 apphost 路径或部署同目录默认路径，无同进程回退。

终态引用仅持有限元数据、SHA、大小和帧计划，不保留完整快照或报告数组。授权有效期沿原 JobRetention（默认1小时），文件在启动和接受新验证任务时清理；每次读取复核身份、大小、SHA、固定 profile/帧计划与摘要，无路径猜测或链接读取。

## 验证边界

Hosting 回归覆盖调度、排队配额、统一回执、取消与截止时间分类、失败报告、哈希损坏、报告存储配额、终态快照释放、关闭与宿主排水及重启读取。Server 定向 HTTP 使用可控执行器验证 transport 与认证合同；独立真实 Worker/原 Runtime 对照探针另行记录，不把接缝测试当成真实正式验证验收。
