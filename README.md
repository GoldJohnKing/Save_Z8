# Save Z8 Desktop — 尼康 Z8/Z9 黑位修复工具

基于逆向工程确认的 Z8/Z9 ISO 黑位 −1 LSB 缺陷 (`y = x−1 for x ≤ 1008`)，提供批量 NEF 文件修复。

## 构建要求

- **Visual Studio 2022/2026** (Windows)
- **.NET 8.0 SDK** (Windows)
- **LibRaw 0.21+**（支持 Z8 NEF 含 HE/HE*）
  - 下载: https://www.libraw.org/download
  - 需要 `libraw.dll`（Release 构建）

## 构建步骤

### 1. 编译 LibRaw 原生包装器

```cmd
:: 假设 LibRaw 安装在 C:\libraw
cl /LD /O2 /EHsc native\libraw_wrapper.c /I C:\libraw\include /link /LIBPATH:C:\libraw\lib libraw.lib
:: 产出: libraw_wrapper.dll
```

或将 `native\libraw_wrapper.c` 和 LibRaw 源码加入 VS 解决方案，作为静态库项目编译。

### 2. 部署 DLL

将以下文件复制到 `libs/` 目录：
- `libraw_wrapper.dll`（上一步编译产出）
- `libraw.dll`（LibRaw 发行包）

项目 `.csproj` 已配置将 `libs/*.dll` 复制到输出目录。

### 3. 编译解决方案

在 Visual Studio 中打开 `SaveZ8.sln`，按 F5 编译运行。

## 使用

1. 启动程序
2. 拖放 NEF 文件到窗口（或点击"浏览文件"）
3. 选择输出目录
4. 选择修复模式：
   - **Notch（推荐）**: `c1008 < c1007/3`，不误伤健康帧
   - **Save Z8 原版**: `c1008 < c1007`，可能误触发 ISO 100
   - **禁用**: 仅解码导出
5. 点击"🚑 开始修复"

## 输出

- 格式: 未压缩 16-bit DNG
- BlackLevel: 4032 (= 1008 × 4)
- WhiteLevel: 65019
- 色彩矩阵: Z8 出厂标定
- EXIF: 从原 NEF 字节级复制 IFD 树

## 技术原理

详见: `Nikon_Z8_Firmware_Reverse_Engineering/docs/z8-iso64-blacklevel-defect.md`

缺陷机制: 逐 CFA 通道确定性 `stored = x − 1 for x ≤ 1008`（数字基座）。
修复: 逆映射 `+1 for stored ≤ T`，其中 T 由 notch 判据确定。

## 许可

仅供个人研究用途。请遵守当地法律法规。
