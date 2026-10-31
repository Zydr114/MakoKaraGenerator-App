# 字体流探针

本目录保留无窗口的字体资源回归探针。桌面 Avalonia 启动探针已随后端仓库拆分移除。

构建并运行：

```bash
output=/tmp/mako-font-stream-probe
dotnet build tools/normal-startup-probe/FontStreamProbe.csproj --artifacts-path "$output/build" -m:1
mkdir -p "$output/no-fonts"
cat > "$output/no-fonts/fonts.conf" <<'XML'
<?xml version="1.0"?>
<!DOCTYPE fontconfig SYSTEM "urn:fontconfig:fonts.dtd">
<fontconfig></fontconfig>
XML
FONTCONFIG_FILE="$output/no-fonts/fonts.conf" FONTCONFIG_PATH="$output/no-fonts" \
  dotnet "$output/build/bin/FontStreamProbe/debug/FontStreamProbe.dll" no-fonts
```

该探针只验证 Skia/HarfBuzz 在无字体数据时的受控错误，不启动窗口、不依赖 Avalonia。
