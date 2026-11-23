# SVG 星星扫句（SDK v2）

package3 包 `makokara.v2.star-sweep`，控制器导出 `star-sweep`。

- 包内 `assets/star.svg` 经 `resources/star.json` 声明为静态 SVG 矢量资源（`mako-svg-static-1`）。
- 纯视觉阶段为每个真实播放头绘制一颗旋转的星，并沿同一进度路径回溯 `tailCount` 颗渐隐小星。
- 星的位置读取最终几何的播放头映射，因此字被 Warp 或父组变换时星仍贴着实际字形走。
- 透明输出照常绘制；没有播放头（未唱或间奏）时不画。修改文件后须更新 manifest.json 中的 SHA-256。
