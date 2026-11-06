# 演唱波动网格（SDK v2 Warp 示例）

package3 包 `makokara.v2.wave-warp`，控制器导出 `wave-warp`。

- 正在唱的字（含注音）以 6×1 网格按正弦起伏：riseUs 内升起，唱完后 settleUs 内回落。
- amplitudePx 默认 6px（0～16），frequencyHz 默认 1.5Hz，wavelengthPx 默认 240px。
- 网格只改字的最终映射，不改排版；播放头、粒子出生点和 StarSweep 的星都读取变形后的位置。
- 共享字簇（连字）和缺时间的字保持原样。修改文件后须更新 manifest.json 中的 SHA-256。
