# 直接效果包公共预览验收工具

日期：**2026-10-21**。产品0.98.1／合同1.31.0的有限P2-A验收工具扩展。工具离线调用生产 `ProjectStore.Save` 建立自有工程，再通过公开授权目录、打开job及preview API操作；不表示公共包创作、浏览器验收、正式包验证／导出、场景测量或任意源码执行接口已经完成。

## 源码冻结后运行

所有.NET操作共用 `/tmp/mkg-ttml-dotnet.lock`，构建单节点。通过既有稳定 `tools/backend-web-host/host.py publish` 生成新的匹配self-contained Server／Worker部署；外层flock统一本轮所有.NET操作，原发布器内部B4锁保持。正式验收要求clean源码，部署目录在整个验收中保持不可变。

```bash
flock -o /tmp/mkg-ttml-dotnet.lock python3 tools/backend-web-host/host.py publish --dist <new-clean-dist>
flock -o /tmp/mkg-ttml-dotnet.lock dotnet build tools/backend-public-package-preview-probe/FixtureOracle/FixtureOracle.csproj -c Release -m:1 -p:UseSharedCompilation=false
python3 tools/backend-public-package-preview-probe/probe.py --dist <clean-dist> --output <new-evidence-directory> --oracle tools/backend-public-package-preview-probe/FixtureOracle/bin/Release/net10.0/FixtureOracle.dll
```

`--stages` 支持 `healthy,diagnostics,lifecycle,assets,audio,error,budget,runaway,b4`。`assets` 会对当前工程修订的所有 validated package manifest/file 调用 `package-asset`，验证完整 GET、HEAD、Range、ETag/304、匿名与跨 client 拒绝、旧修订/未知资源拒绝、连续普通预览四帧及 seek 换代，并在 project close 后验证资产 TTL 过期；此外独立打开 sprite fixture，逐项读取 validated image role（image/png），并执行普通/透明各四帧连续预览，透明组包含 seek 换代。每次必须使用新的证据目录，失败原样保留。`development*`是工具开发及直接Runtime预检，不是最终HTTP验收。`provenance.json`记录复用源文件Git blob／SHA及修改说明。

开发分支的 Server 回归另有 `PackagePreviewTransportTests.包预览连续播放换尺寸透明度回退和旧帧句柄按代次排水`。该测试使用注入的路由工厂，固定验证同一 package 输入的前向帧复用、输入换代、旧帧句柄失效、回退 seek、旧 generation 拒绝和 Worker 会话排水；它不产生真实 self-contained HTTP、浏览器、跨平台或资源闭包验收证据，不能并入本工具的真实 HTTP 请求／断言计数。

`lifecycle` 阶段还会在首次连续预览前读取同一工程修订的 `project-sessions/{id}/resources`，核对真实 HTTP 返回的 manifest/file 身份、包内 path/role、闭包摘要计数、validated/unsupported 状态以及资源 SHA/大小字段形状。该检查只证明公共资源读模型与预览输入使用同一工程修订；`assets` 阶段才会把已验证清单逐项投影为受控 asset 并读取其固定字节。资源清单本身不等于下载权限；本工具也不把原始字节读取记为图片、字体或视频解码成功。

专用launcher沿用既有host／smoke的私有bootstrap、随机loopback、信号转发及正常排水；明确设置Server既有选项 `MAKO_BACKEND_ASSET_TTL_SECONDS=2`，区别于默认600秒。客户端token／lease仍为120／300秒。自launcher启动便递归观察PID／startTicks，包含ready前的capability Workers。凭证只在内存及本次拥有的私有bootstrap文件中，正常退出删除bootstrap；最终扫描所有证据字节，检查未保存内存中的凭证。

## 独立oracle及精确比较

FixtureOracle独立读取保存后的工程，直接调用原Runtime会话；HTTP／Worker结果不参与构造期望像素或诊断。PNG完整字节及全部解码像素必须相等；独立Runtime原始BGRA保留并计算SHA，另与关闭实际效果贡献的反事实BGRA完整比较。HTTP没有原始BGRA端点，也没有完整snapshot source SHA；记录的直接source SHA只作独立来源证据，不能称HTTP source SHA已相等。Worker实际source绑定及哈希由真实协议／codec回归另外支持。 Oracle必须使用Release配置；SDK、script-refs及Roslyn实际DLL字节SHA必须与部署Worker一致，artifactHash字段因此强制完整比较。

Runtime诊断epoch是每次独立执行随机生成的UUID。直接Runtime及Worker各侧分别要求非空合法UUID、epoch单段出现及从1开始连续的完整sequence；不要求跨进程epoch字节相同。instance／artifact／stage／time／location／package／code／severity／message等确定字段全部对照。连续成功帧允许HTTP累计包含前帧发生记录。compile-source、image-effect及material／owner三类的完整确定字段及顺序严格相等，不制造runtime occurrence。

实际Runtime Error试样在100000us成功，500000us失败；失败清除latestFrame且不发布新PNG，原已发布帧租约仍遵循既有TTL。实际编译、Runtime Error、图片及材质诊断分别验证。受控传输回归覆盖Error同时含side diagnostics；实际Runtime可能在绘制前抛错，HTTP不冒称实测了该作者组合。

130条警告试样验证完整两页及固定页 `limit=1` 拒绝；实例只报告一次，避免Runtime历史重放重复报告。270条试样验证**私有Worker**预算 `WorkerDiagnosticsBudgetExceeded`，不混称公共 `PreviewDiagnosticsBudgetExceeded`。公共较小页预算拒绝由Hosting回归支持，不发明生产环境配置项。

## 资源寿命及隔离

音频试样包含自有1秒mono PCM16 WAV及精确size／SHA媒体身份，经生产持久化封闭。先创建preview再关闭project，超过配置资产TTL后才首次请求frame；授权音频字节必须仍完整可读，PNG必须与直接oracle相等。关闭preview并再次超过TTL后，公共内容应过期，自有resource字节应清理。此证据证明观察到的资源寿命，不声称存在公共引用计数端点；Writer原子捕获竞争由独立回归支持。

Initialize／Evaluate × input替换取消／deadline／close六组合使用实际作者marker、nonce、Worker／子进程PID、startTicks及private group；每例同时执行无关健康静态预览，检查原身份排水，并在适用场景恢复。只代表本机Linux进程组实证，不表示任意恶意脚本的OS sandbox。

## 独立部署变体

```bash
python3 tools/backend-public-package-preview-probe/variants.py historical --output <new-074-build>
python3 tools/backend-public-package-preview-probe/variants.py historical --output <new-074-version-build> --version-override 0.98.0
python3 tools/backend-public-package-preview-probe/variants.py deployment --dist <canonical-dist> --output <new-missing-refs> --kind missing-refs
python3 tools/backend-public-package-preview-probe/variants.py deployment --dist <canonical-dist> --output <new-old-worker> --kind historical-original-version --worker <new-074-build>/worker
python3 tools/backend-public-package-preview-probe/probe.py --variant --dist <declared-variant> --output <new-evidence> --oracle <oracle-dll>
```

历史Worker来自真实Git archive源码 `15246253987168aa5870ed0660721282fbf8e086`，记录完整源码／archive哈希及构建命令。version override明确为改变编译输入的历史源码重构建，不冒充原历史二进制；原旧engine身份拒绝不能单独证明diagnosticsVersion拒绝。

变体manifest声明每个文件差异。missingrefs仅绕过标准launcher预先拒绝缺refs的检查，以验证实际Server／Worker握手；canonical部署字节始终不改。missingrefs及current-version历史变体还实际验证静态渲染，original-version历史Worker可能同时拒绝静态engine身份；其音频准备能力按实际记录，不强制可用。
