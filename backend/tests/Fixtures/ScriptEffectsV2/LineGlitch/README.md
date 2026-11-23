# 整行分片 Glitch（SDK v2，ColorOnly 直接图）

纯资源 package3 包 `makokara.v2.line-glitch`，图导出 `line-glitch`，输出目标为整行，
可绑定工程／分支／歌词行／clip／Node，不允许 tokens 绑定。没有 C#，不需要编译。

- strengthPx 默认 6px（0～12），sliceHeightPx 默认 12px（2～64），frequencyHz 默认 8Hz（0～24）。
- 一次捕获整行的基础字、填充、描边、阴影和注音，同一横向分片一起位移；频率为 0 冻结时间片。
- `ColorOnly` 只位移颜色图像，不改变几何，光标和粒子锚点保持真实几何。
- 不依赖背景，透明输出保留图效果。修改文件后须更新 manifest.json 中的 SHA-256。
