# S3b 来源管理真实 HTTP 验收

独立 `branch_probe.py` 只执行四个来源管理 type；复用现有可信启动、自有 profile/素材、匹配 Worker 准备、脱敏 HTTP、持久化 fixture、终态等待与正常排水。S1/S2/S3a 探针运行范围不变。

```bash
python3 tools/backend-web-editing/branch_probe.py \
  --dist "$PWD/artifacts/backend-web-s3b-0790/deployment-final1" \
  --output "$PWD/artifacts/backend-web-s3b-0790/http-final1"
python3 -m py_compile tools/backend-web-editing/branch_probe.py
python3 tools/backend-web-editing/branch_probe.py --help
```

输出必须为仓库忽略的 `artifacts/` 下新目录。所有 fixture 从本轮真实保存且关闭的模板复制，沿用实际产品/schema版本，新不可变歌词按原始字节SHA寻址并更新入口，不接触用户工程。大输入使用小量来源元数据或合法时间，避免海量无关诊断；旧悬空关联是单独的警告兼容输入。

检查范围：

- `addTranslationBranch` 仅Trim名称，原语言值不归一化；创建空Translation/FollowPrimary来源，`branchResult.selectedBranchId`为实际新ID。
- `configureBranch` 全字段提交、保原名空白、显式null清语言，角色/三种时间策略读取一致；配置及关联保留全部原正文、元素、注音和原时间。自有保存闭包另核对完整来源样式覆盖及已有展示未被元数据操作改写。
- `linkPrimaryLine` 在none/independent/followPrimary下关联真实主行或null解除；不会把主行时间写成原始独立时间。原相同悬空关联允许no-op，保其真实警告语义。
- 主来源角色身份不可转移、不可删除、不可自关联；**主来源三种合法TimingMode沿原业务及展示校验**。独立空展示fixture验证其修改/undo/redo，不引入候选文案曾误写的额外禁止。
- `removeBranch`选择真实主来源；只有add/remove真实改变返回branchResult，configure/link/no-op/历史/错误省略，其他timingResult/feedback不混入。结构变更使用完整project刷新，不返回部分ID列表。
- 每类成功均精确查询→一次undo→一次redo；no-op配置/关联及预算拒绝保修订、dirty、历史、redo和游标。四type完整回执重试、补零修订、可空省略等价、各派生字段/目标/截止/修订改变复用拒绝；真实同修订并发只接受一个新增，相同回执并发只创建一次ID与历史。
- 必填/显式null/错误类型/数值或未知枚举/重复/跨type字段400，领域422带稳定PascalCase failureCode及真实command.*字段路径；只读四type拒绝，跨client及不存在会话中立404。
- 长于128字符的合法来源/行ID可以配置、关联、删除和保存重开；不照搬旧时间命令的128上限。完整单来源/受影响单行超1MiB及选择回执超64KiB均先413且原状态不变。
- 来源预算是**单来源完整响应**。两个各自可读的大名称来源可以成功提交、分页读取和保存重开，即使默认组合页413；状态快照由真实limit=1页按同一精确修订组装，绝不把组合页超限说成整个工程禁止。
- 删除原超大不可读来源可修复；新巨大primaryLineId关联造成目标单行不可读则拒绝；主来源ID本身过大导致删除后的真实选择回执超限，也须在提交前413。

所有接受的save/open/close任务等真实成功终态；新session逐来源比较完整投影。失败保留现场，新轮次用新目录。凭证仅在内存，扫描包含最终summary精确字节，确认启动器/Server正常退出及观察到的进程身份消失才原子发布passed。`http-elapsed.json`仅为本机短样本，不代表性能或吞吐保证；HTTP留档可由冻结合同的 `validate.py --http-log` 独立验证。

此范围不包括再次导入/元素重切、Web浏览器、场景作者、完整B3/B4、完整像素oracle或平台/性能验收。fixture直接构造不是新增HTTP工程格式或任意对象补丁能力。
