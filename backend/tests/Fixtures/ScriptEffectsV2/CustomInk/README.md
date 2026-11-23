# 自定义未唱／描边波纹材质（SDK v2）

纯资源 package3 包 `makokara.v2.custom-ink`，材质导出 `custom-ink`，允许 fill／stroke。
没有 C# 控制器，不需要编译；导入后在“未唱”或“描边”材质来源选择“双色波纹”。

- frequencyHz 默认 2Hz（0～20），amplitude 默认 0.15（0～0.5），colorA／colorB 为直乘 sRGB RGBA。
- 波纹按固定逻辑画布坐标与歌曲时间 `host:timeSeconds` 变化；频率为 0 冻结时间项。
- `coverageMode=unmasked`：shader 只产生颜色，宿主按真实字形／注音或外描边区域覆盖一次。
  不读取背景，透明输出继续执行；基础外观为蓝色纯色 `#3380CCBF`。
- 修改图、程序、SkSL 或本文件后须更新 manifest.json 中对应 SHA-256。
