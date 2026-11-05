# 受控媒体原生执行封套

`mkg-media-exec-gate`在同一PID里等待单字节放行再exec内部工具；
`libmkg_media_envelope.so`通过sd-bus直接创建同用户transient service，并在exec前设置
MemoryMax、MemorySwapMax=0、OOMPolicy=kill、NoNewPrivileges与AddRef。
Managed owner核对manager和kernel限额、InvocationID、PID/startTicks和固定cgroup inode
之后才放行。强制取消写所固定目录FD下的cgroup.kill，不对unit名称执行StopUnit。

`CollectMode=inactive-or-failed`配合AddRef保留完成状态，读完结果、确认管道EOF和cgroup
排水后才Unref。启动时必须等Type=exec服务进入active，不能把activating阶段的PID当作
已经完成exec的gate。失败排水保留source和预算，不能计为成功释放。

最终可执行项目可导入 `build/MakoMediaEnvelope.Native.targets` 并显式设置
`MakoMediaEnvelopeEnabled=true`。该目标在SDK完成路径求值后构建，向输出及publish目录的
`media-tools/`复制gate与bridge；默认关闭。完整decoder依赖清单与Worker接线属于后续片。

有限实机验收工具：`tools/backend-script-render-upgrade/M10EnvelopeProbe`。所有操作在普通
用户权限下运行；能力不足返回具名错误，不申请root也不更改系统设置。
