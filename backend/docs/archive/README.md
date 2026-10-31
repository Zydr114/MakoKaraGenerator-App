# 历史文档归档

`document-map-20261029.json` 逐项列出整理前根目录文档及旧工作入口的名称、
目的地、保留理由、是否保留导航页；移动的正文另固定基线 Git blob 与 SHA-256。
`decisions/`、`history/`、`plans/`、`research/`、`acceptance/` 和 `legacy-ui/`
按历史类型组织。旧 `roadmap.md`、`development.md`、`decisions.md` 和受保护
合同或证据所需的部分旧路径保留导航，源代码注释中的旧章节仍可由导航找到正文。

`document-links-baseline-20261029.json` 是迁移前的逐项基线，含各链接所在文件、
行号、目标及类型，以及 `docs/performance/` 和示例包跟踪文件的 SHA-256。基线记录 202 项：
153 项指向已移除的桌面源码或测试，9 项指向已移除的 UI 兼容工具，5 项为缺失
图片或许可入口，35 项为锚点未匹配。这里的类别按目标路径
统计；完整逐项结果以 JSON 为准。锚点检查采用工具内的 GitHub 风格近似规则，
不能代替对每种 Markdown 渲染器的验证。

离线检查依赖 Python 3 和 `markdown-it-py==4.2.0`，安装到独立环境后从仓库根执行：

```bash
python3 -m venv artifacts/document-archive-venv
artifacts/document-archive-venv/bin/pip install -r tools/document-archive-requirements.txt
artifacts/document-archive-venv/bin/python tools/check-document-archive.py \
  --manifest docs/archive/document-map-20261029.json \
  --link-baseline docs/archive/document-links-baseline-20261029.json \
  --evidence-baseline docs/archive/document-links-baseline-20261029.json \
  --output artifacts/document-archive-check.json
```

检查扫描 Git 跟踪及非忽略的 Markdown（排除无关的用户探针），使用 CommonMark 解析
本地行内链接、引用链接和图片，以及标题/显式 HTML ID 的锚点；代码块内的路径、
HTML 链接、远程链接和非 Markdown 目标的片段不作校验。以基线规范化源文件和目标路径
后，分别统计保留、修复和新增的问题；新增问题、映射缺失、归档正文与基线除链接
重定位外不同、性能证据集合/哈希变化，或示例包跟踪文件集合/哈希变化时返回非零。正文核对需要本仓库可读取
`4e70334` Git 对象；链接修复的可复现规则由 `tools/organize-document-archive.py` 定义。
历史证据中的路径原文和证据文件不被重写，导航页只为它们提供可访问的旧路径。
