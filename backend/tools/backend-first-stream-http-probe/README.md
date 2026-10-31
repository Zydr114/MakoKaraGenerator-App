# 首音轨准备回归

仅使用新输出目录、自有MOV/profile与匹配Server＋完整Worker；不创建PCM资源或波形HTTP接口。

```bash
python3 tools/backend-first-stream-http-probe/probe.py \
  --dist /本次匹配发布目录 \
  --output "$PWD/artifacts/first-stream/http-fixed"
```

工具构造1秒MOV：首音轨8kHz单声道，第二音轨48kHz双声道且显式default；单轨MOV为控制。
独立`-map 0:a:0` PCM oracle必须为8000样本。真实HTTP验证Worker准入、创建与设置时长、保存关闭重开、完整成功终态、自有进程身份排水及凭证扫描。
默认双轨与单轨均须为1000000微秒；`--expected-dual-duration-us 6000000`只用于留档修前缺陷，不能记作修复验收。

完整Info、sampleCount、源采样率、全部金字塔层float bits的真实Worker回归在`WorkerFirstAudioStreamTests`；它的独立首轨PCM oracle不能由同一路有缺陷的loader替代。可通过`MAKO_FIRST_STREAM_WORKER`指定同版本完整Worker apphost，默认使用当前工作树Debug Worker，先以同一锁构建Worker后再跑定向测试。

此工具不替代浏览器、实时预览、G11缓存、性能或全平台验收。发布目录SHA由原`host.py`检查；保留修前和修后独立证据目录，不覆盖旧结果。
