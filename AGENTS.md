# MakoKaraGenerator App 工作约定

本仓库整合本地后端与 Web 源码，根入口使用 App 内 `backend/`、`frontend/`。
`backend/AGENTS.md`、`frontend/AGENTS.md` 与其历史链接作为上游来源记录；本次独立整合中，
本文件明确覆盖其中旧仓库边界、外部协作、Agent Mail、分支、远程发布和个人绝对路径约定。
不得修改两个上游源仓库，不发送外部消息。用户已授权将 App 自身仓库关联并同步至私有远程
`https://github.com/Zydr114/MakoKaraGenerator-App`；仅此远程获准创建和推送，暂不添加许可证。
用户已经授权并行子代理参与本次整合。

- Git 每步一个中文 `type(scope): 摘要` 提交，按所属路径暂存；保留他人并行改动。
- 禁止提交凭证、私人 profile、构建产物、node_modules、嵌套 .git；版本化源素材可保留。
- 本机代理 `127.0.0.1:7897`，loopback 直连；需要 root 的具体命令交用户执行。
- 本地运行只用本仓库构建，隔离 profile，绑定 loopback；凭证不得输出到日志或 URL。
- 保留原有 HTTP API、浏览器静态预览协议与 500ms 控制节奏；用户已授权在 App 内按完整计划实施持续 GPU 共享纹理路径，P0 硬门禁通过后继续 P1～P3。可修改本仓库 `backend/`、`frontend/`、`desktop/` 与原生接缝，新持续协议独立审查，不破坏既有浏览器入口；不得修改两个兄弟源仓库。
- 按本次拥有的 PID 和进程身份停止子进程，禁止按进程名称批量终止。
