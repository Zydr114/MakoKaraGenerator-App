# 静态外辉光

本文规定后端 `0.110.0` / HTTP 合同 `1.42.0` 的静态外辉光。实施与验收记录见文末；前端控件不属于本次修改范围。

## 行为与参数

辉光独立于描边和阴影。`LyricStyle.Glow = null` 表示关闭；完整 `GlowSpec` 的默认值如下。数值使用文字局部坐标的逻辑像素，随已有场景变换缩放。

| 字段 | 默认值 | 范围与含义 |
|---|---|---|
| `color` | 白色 `#FFFFFFFF` | RGBA 颜色；颜色 alpha 参与辉光透明度 |
| `opacity` | `1` | 有限 `[0,1]`，独立辉光透明度 |
| `blurRadiusPx` | `8` | 有限非负；传给 Skia 的高斯标准差 sigma，不是模糊直径 |
| `spreadPx` | `0` | 有限非负；字形轮廓以 round join 向外扩张的宽度 |

颜色 alpha 或 opacity 为零时不可见；两个距离均为零时无外部区域。`blurRadiusPx=0, spreadPx>0` 是实心外轮廓，`spreadPx=0, blurRadiusPx>0` 是柔和外光。扩大 spread 增加实心来源宽度，扩大 blur 使过渡更宽。辉光没有偏移、方向或已唱/未唱两套颜色。

绘制从真实整形字形的**不透明覆盖**开始，聚合当前绘制选区后外扩并统一模糊，最后减去原字形内部。因此透明填充仍能发光；不从填充材质、描边颜色或阴影反推轮廓。辉光在两态填充切分之前绘制一次，避免演唱游标切分光晕。每个基础字/注音域顺序为辉光、外描边、阴影、填充；未开启辉光时保留原有顺序。

辉光自身透明度为 `color.alpha × glow.opacity`，再按已有样式/对象合成规则应用公共透明度一次。外扩边界包含 `spreadPx + 3 × blurRadiusPx` 和抗锯齿余量；不能以旧装饰宽度上限静默裁切。原生计算溢出、表面或捕获预算不足须明确失败。

## 继承与编辑

工程、分支、行和 token 均支持；注音跟随基础 token 的有效外观；倒计时组和文字标记也保留辉光。

- 未指定 `glow`：继承。
- 显式 `glow: null`：关闭继承的辉光。
- 部分对象：仅覆盖提供的叶字段。
- 没有上层对象时，部分覆盖从透明色、opacity `1`、blur `0`、spread `0` 合并，避免只改距离意外启用白光。
- restore 整体移除整个局部辉光覆盖；restore 叶字段只移除该叶，不撤销显式整体 null。

HTTP 在 `styles.patch` / `styles.restore` 上新增 `glow`、`glow.color`、`glow.opacity`、`glow.blurRadiusPx`、`glow.spreadPx`；同批整体与叶冲突拒绝。完整对象和每个叶值必须遵守当前 [OpenAPI](../../web/openapi.json) 的严格字段、颜色编码和空值规则。

Core 来源追踪新增 `GlowSpec`、`GlowColor`、`GlowOpacity`、`GlowBlurRadiusPx`、`GlowSpreadPx` 五项，属性总数为 26；HTTP 编辑成员总数为 29。模板按整体或叶选择，保留原有应用层次与 no-op/历史语义。

## 脚本、材质与捕获

脚本通过 `StyleView.Glow` 读取中立 `GlowValue`，不能通过本次新增的静态字段逐帧写入辉光。现有 `StyleUpdate.Apply`、`ApplyMaterials` 及静态样式投影保留基线 Glow，修改填充、阴影、材质或透明度不会将它清空。

渲染颜色部件增加 `BaseGlow` / `RubyGlow`。包材质继续只替换原填充或描边槽，辉光独立参与颜色合成。`TargetColor` 的 `includeGlow` 可单独关闭，省略时为 `true`；所有 coverage 通道与原始材质区域排除辉光。包 graph capture 的 `includeGlow` 是可选布尔字段，包格式仍为 2。

## 格式与兼容边界

工程 schema `29`、歌词 schema `18`、模板 schema `7` / `templates-v7`。新格式贯通普通持久化、固定模板与 Worker 源快照；不新增旧项目自动迁移。程序版本用于现有 Server/Worker 引擎身份核验，必须成对更新。HTTP 协议仍为 1，包声明 `sdkVersion: "1.0.0"` 保持；该声明不是程序集的二进制版本。

## 实施与验收

设计已冻结，实现和当前版本验收进行中。完成后在此记录定向回归、合同校验、匹配发布与真实 HTTP/PNG 证据；它们不代表浏览器、所有字体、所有平台或性能验收。
