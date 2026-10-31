# W8 作者工作流 HTTP 验收

从仓库根使用同源码的 self-contained Server/Worker 发布目录和不存在的新证据目录：

```bash
python3 tools/backend-w8-authoring-http-probe/probe.py \
  --dist artifacts/w8-authoring-20261030/dist-final1 \
  --output artifacts/w8-authoring-20261030/http-final1
```

探针创建自己的 profile，执行 C# 增删改名、manifest 与图/着色器文本保存、同步/异步编译、no-op、修订历史、下载再上传检查、配置库固定包、具体模板错误、活动 owner 拒绝、Server 重启后的显式 claim/回执重放与删除。代表作者试样通过真实 CPU Worker 产帧并完整解码 PNG。记录脱敏 HTTP、任务终态、PID/startTicks 排水和凭证扫描。

Hosting 回归单独覆盖两个下载槽及释放、旧修订导出和完整包验证。HTTP、合同留档形状、浏览器、跨平台和性能是独立证据；本探针不宣称浏览器、长片或性能验收。失败目录保留；再次运行须用新目录。
