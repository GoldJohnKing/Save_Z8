# Save Z8 Desktop — 尼康 Z8/Z9 黑位修复工具

基于逆向工程确认的 Z8/Z9 ISO 黑位 −1 LSB 缺陷（逐 CFA 通道 `stored = x−1 for x ≤ 1008`），
批量将 NEF 修复并导出为 Adobe DNG，保留完整元数据、镜头校正与预览。

- **修复判据**：Notch（`c1008·3 < c1007`，逐 CFA 象限）——对健康帧（如 ISO 100）零误触发
- **输出**：16-bit CFA DNG，写入 NEF 同目录的 `Save_Z8\` 文件夹；无损 JPEG 压缩（Compression=7）恒开启（标准片约 -30%、HE 片 -60% 以上，逐位无损，rawpy/LibRaw 双回读 0 失配验证）
- **元数据**：EXIF / GPS / Interop / MakerNote / XMP 完整搬迁
- **镜头校正**：Nikon NEFInfo（SubIFD `0xC7D5`）→ DNG `WarpRectilinear`（畸变+色差）与
  `FixVignetteRadial`（暗角）OpcodeList，数值与 Adobe DNG Converter 对齐
- **预览**：NEF 内嵌缩略图 / 中图 / 全尺寸 JPEG 三级搬入 DNG（SubIFD + IFD0 双路径）

## 参考项目：JO（Save Z8 算法出处）

本项目的修复算法（黑位 −1 LSB 反演与 notch 判据优化）源自
**[JO.html](https://y-g-jiang.github.io/JO.html)**（作者 y-g-jiang）网页工具中的
"Save Z8" 功能，在此致谢。与 JO 的关系：

| 组件 | JO 的实现 | 本项目 |
|---|---|---|
| NEF 解码 | joraw2.wasm（**LibRaw 0.22.2** WASM 封装，含 `nikonHe`/`nikonHeStar` 补丁） | LibRaw fork（nikon-he-production 分支，同源补丁）原生 DLL + C 包装器 |
| 修复算法 | Pyodide + numpy（网页内 Python） | C#（`BlackLevelFixer`） |
| DNG 写盘 | Pyodide 手写 TIFF/DNG（`convert_to_dng`） | C#（`DngWriter`，结构对齐该参考实现） |

JO 的 DNG 写盘器是本项目 DNG 核心标签集的参考蓝本；镜头校正 Opcode 映射
（Nikon 私有格式 → Adobe DNG OpcodeList）则为本项目独立逆向，并与
Adobe DNG Converter 18.6 的官方输出做过数值标定（详见 `src/SaveZ8.Core/NikonLensCorrection.cs` 头注）。

## 构建要求

- Windows + **Visual Studio 2022/2026**（MSVC x64）
- **.NET 8.0 SDK**
- **git**（拉取 LibRaw 子模块）

## 一键构建与发布

```cmd
build.bat
```

脚本自动完成：子模块初始化 → LibRaw 编译（JO fork，含 HE/HE\* 解码器）→ `libraw_wrapper.dll` 编译 →
单文件发布（Release，self-contained，原生库内嵌压缩）。

**产物**（单文件，直接分发，无需安装 .NET 运行时）：

```
out\SaveZ8_v<版本>.exe   ~46 MB 单文件（含 libraw / Avalonia / Skia 全部依赖，文件名含版本号）
```

冒烟自检（无头跑完整管线，验证原生库加载）：

```cmd
out\SaveZ8_v<版本>.exe --selftest 某文件.nef
```

构建路径规范（由 `Directory.Build.props` 统一）：

```
build\obj\<Project>\            中间文件
build\bin\<Project>\Release\    编译输出（开发调试用）
build\publish\                  单文件发布暂存
out\                            最终分发目录
libs\                           原生产物（libraw.dll / libraw_wrapper.dll / libraw.lib）
```

首次构建 LibRaw 需数分钟；之后 `libs\libraw.dll` 存在即跳过（删除可强制重建）。
`libraw_wrapper.dll` 每次全量重建（秒级）。

## 使用

1. 运行 `out\SaveZ8_v<版本>.exe`
2. 拖入或选择 NEF 文件
3. 点击"开始修复"——输出自动写入每个 NEF 所在目录的 `Save_Z8\<文件名>_fixed.dng`
4. 完成后点击列表中的"已修复"可直接在资源管理器中定位产物

修复在文件级完成：`y ≤ threshold → y+1`（逐 CFA 象限判定），像素级验证
（45.7M 像素 0 失配）见 `tests/`。列表中的"修复像素"= 被执行 +1 纠正的样本数，
即该帧真实值落在黑位区（受缺陷波及）的像素规模，随画面暗部占比变化。

## 命令行诊断工具

```cmd
build\bin\diag\Release\net8.0-windows\diag.exe <NEF 路径>
```

对单个文件执行完整管线（解码 → 元数据提取 → 修复 → DNG 写盘）并输出各步骤诊断。

## 项目结构

```
src\SaveZ8.Core\         核心库（解码/修复/元数据/镜头校正/DNG 写盘）
src\SaveZ8.UI\           Avalonia 桌面界面
src\SaveZ8.UI\Assets\    应用图标 SVG 源稿 + ICO
tests\SaveZ8.Tests\      xUnit 测试（修复语义 + DNG 结构回读 + 镜头校正映射）
tools\diag\              命令行诊断工具
native\libraw_wrapper.cpp  LibRaw C 包装器（P/Invoke 边界）
external\libraw\         LibRaw 子模块（y-g-jiang fork，nikon-he-production 分支）
samples\                 验证样本（LFS）：标准无损 / HE\* / HE 各一
```

## 已知限制

- 暗角校正在"暗角控制=关闭"的文件上不写入（与 Adobe 行为一致）
- 镜头校正标定基于 Z8 固件 3.11 + ADC 18.6（28mm 端实测验证，映射为纯几何转换）
- HE/HE* NEF 经 y-g-jiang LibRaw fork（nikon-he-production 分支，含 nikonHe/nikonHeStar
  解码器）真解码；标准 14-bit 无损压缩路径与 0.21.3 逐字节一致。已知边界：HE 格式的有损
  压缩会部分涂抹黑位 notch，逐象限判据可能欠触发（ISO64 实测 HE 仅单象限修复），HE 上的
  修复语义待 ACR 交叉验证后定论
