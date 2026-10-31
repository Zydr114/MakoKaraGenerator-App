# MakoKaraGenerator App 工作约定

本仓库整合本地后端与 Web 源码，根入口使用 App 内 `backend/`、`frontend/`。
`backend/AGENTS.md`、`frontend/AGENTS.md` 与其历史链接作为上游来源记录；本次独立整合中，
本文件明确覆盖其中旧仓库边界、外部协作、Agent Mail、分支、远程发布和个人绝对路径约定。
不得修改两个上游源仓库，不发送外部消息，不创建或推送 remote。用户已经授权并行子代理参与本次整合。

- Git 每步一个中文 `type(scope): 摘要` 提交，按所属路径暂存；保留他人并行改动。
- 禁止提交凭证、私人 profile、构建产物、node_modules、嵌套 .git；版本化源素材可保留。
- 本机代理 `127.0.0.1:7897`，loopback 直连；需要 root 的具体命令交用户执行。
- 本地运行只用本仓库构建，隔离 profile，绑定 loopback；凭证不得输出到日志或 URL。
- 保留原有 API、预览协议与 500ms 控制节奏；协议变更需独立审查，不夹带进整合。
- 按本次拥有的 PID 和进程身份停止子进程，禁止按进程名称批量终止。
