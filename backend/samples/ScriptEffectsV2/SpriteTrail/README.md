# 图片粒子与固定拖尾（SDK v2）

package3 包 `makokara.v2.sprite-trail`，控制器导出 `sprite-trail`。

- 每条真实播放头按歌曲时间常速发射，宿主解析出生、运动、寿命和拖尾；不在帧回调里累计余量。
- 纯视觉阶段用 `image` 参数指定的包内图片绘制旋转星芒，拖尾样本按距今时间淡出（0.25 倍 alpha）。
- 粒子使用 `Custom` 渲染关闭自动圆点；图片自身 alpha 与颜色乘数各乘一次，完整仿射含旋转。
- transition 默认 jump，可选 bridge（bridgeUs=180000，bridgeGapLimitUs=2000000）。
- 例图 `assets/spark.png` 为自行生成的 16×16 白色星芒，无外部素材依赖。修改文件后须更新 manifest.json 中的 SHA-256。
