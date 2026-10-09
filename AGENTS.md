# AGENTS.md — Save Z8 项目 Agent 操作指引

先读 README.md 了解产品定位。

## 事实速查

- 缺陷模型：Z8/Z9 逐 CFA 通道 `stored = x−1 (x ≤ 1008)`；修复 = notch 判据（逐象限 `c1008·3 < c1007` → `y ≤ 1007 则 +1`），唯一路径，无开关
- 输出固定为 `<NEF 目录>\Save_Z8\<名>_fixed.dng`
- 像素域：DNG 内存 NEF 14-bit 原值 `x<<2`；BL=4032/WL=63568 与 Adobe 的 1008/15892 等价（勿混用两套域）
- HE/HE* NEF：external/libraw = y-g-jiang fork（branch nikon-he-production，b577a7e8，版本串 0.22.0-Release），含 nikonHe/nikonHeStar 真解码器；分流判据 = raw strip 首四字节 `FF 10 FF 50` 且 NEFCompression∈{13,14}。实测 ISO64：HE\*（DSC_7593）notch 清晰、修复 27.4M px；HE（DSC_7594）压缩把 notch 涂抹，判据仅单象限触发——HE 上的修复语义待 ACR 交叉验证。Z8 的 NEFCompression 在 MakerNote tag 0x51+10（老机身才用 0x93；MakerNote 内偏移自相对内部 TIFF 头）

## 架构

```
src/SaveZ8.Core/
  Native/LibRawNative.cs     P/Invoke（extern "C" wrapper，wchar 路径，返回 cam_mul）
  TiffIfdExtractor.cs        NEF IFD 解析：EXIF/GPS/Interop 搬迁源、预览提取、NEFInfo(0xC7D5) 暴露
  BlackLevelFixer.cs         notch 修复（unsafe 双遍）
  NikonLensCorrection.cs     NEFInfo → OpcodeList2/3 映射（核心资产）
  DngWriter.cs               DNG 写盘：Entry/IfdBlock 模型，三遍布局（块偏移→多指针数组→指针解析）
  NefProcessor.cs            管线编排
src/SaveZ8.UI/              Avalonia 11 + CommunityToolkit.Mvvm；输出路径固定、状态可点击定位
tools/diag/                 命令行全管线诊断（不在 sln，需单独 build）
tests/SaveZ8.Tests/         xUnit：修复语义 + DNG 结构回读 + 镜头校正映射
```

## DNG 结构（对齐 ADC 18.6，改动前必读）

渲染一致性依赖与 Adobe DNG Converter 逐字节对齐（用户已实测确认）。布局：

- **IFD0** = 缩略图(NST=1) + 基线/色彩标签 + BaselineExposure/Noise/Sharpness（ADC 标定的 Z8 常量，见代码）+ AsShotNeutral(=1/cam_mul，逐帧) + EXIF/GPS + SubIFDs
- **SubIFDs → [raw IFD, 全尺寸预览, 中图预览]**；预览 = NEF 内嵌 JPEG 原样搬入（513/514，Comp=6/RGB/SPP=3）
- **raw IFD**（NST=0）：CFA、黑白场、**ActiveArea=(0,0,H,W) 全幅** 与 **DefaultScale+CropOrigin+CropSize 成对出现**——ActiveArea 定 warp 归一化基（校正强度），DefaultCrop 定显示取景；缺任一即出几何差异。Crop 值从最大预览动态推导，勿硬编码
- OpcodeList2/3 写在 raw IFD

## 镜头校正映射

固定线性转换，逐帧系数全部来自该照片 NEF（映射表为格式换算，无自创数据）：

- NEFInfo（raw SubIFD tag 0xC7D5）：`Nikon\0`+版本+自包含 TIFF，entry 值相对内部 TIFF 头(+10)。块 5=畸变 k1,k2,k3@0x14/0x1c/0x24、t4@0x40；块 6=暗角 v1,v2,v3@0x24/0x34/0x44；块 7=CA（idx 5,7,11→R，14,16,20→B）；块 8+0x04≠0=暗角开启
- 绿平面 `(off,a,b,c)=M_G·(1,k1,k2,k3)` 后整体 `×(1+t4ref)/(1+t4)`；R/B = G+CA 线性项；暗角 `P=(v3,v2,v1)·21/128`（反序）
- opcode 恒**大端**（与 TIFF 字节序无关）。WarpRectilinear id=1，params=planes+3×6f64+cx,cy；FixVignetteRadial id=3，params=5f64+cx,cy

**重标定**（固件/ADC 升级时）：ADC 装于 `C:\Program Files\Adobe\Adobe DNG Converter\`。直接改写 NEF 中 block5/6/7 的有理数整数造合成变体（ADC 忽略 +0x50，无校验和障碍）→ ADC 转换 → 基向量扫描解线性映射。

## 坑（勿重复）

1. TIFF：值域恒 4 字节；count=1 LONG 必须内联；SubIFDs count>1 时值域指向 u32 数组；条目按 tag 排序
2. DNG tag 号改动必须对照 ADC 实际字节验证（曾把 DefaultCropSize=50720 写成 50820 → ACR 拒开，rawpy 却正常）
3. opcode 数据大端；Nikon MakerNote 自相对可原样搬迁
4. cmd 批处理必须 ASCII+CRLF；Avalonia Button 无 TextDecorations、Grid 无 Padding
5. UI exe 运行时构建会锁文件：先 `taskkill /IM SaveZ8.UI.exe /F`
6. 像素回归模型必须逐 CFA 象限取阈值（象限间可不同），全局阈值会假报失配

## 验证回路（任何改动后）

```bash
/mnt/c/Program\ Files/dotnet/dotnet.exe test tests/SaveZ8.Tests/SaveZ8.Tests.csproj -c Release   # 单测
/mnt/c/Program\ Files/dotnet/dotnet.exe build tools/diag/diag.csproj -c Release                  # diag
.../build/bin/diag/Release/net8.0-windows/diag.exe <NEF>                                          # E2E
```

- 结构：自写 Python IFD 遍历核对 273/279、CFAPattern∈{0,1,2}、opcode 位置、Crop/ActiveArea 与 ADC 参照一致
- 像素：rawpy 回读 DNG == (notch 修复后 NEF)<<2，0 失配
- 终验由用户在 ACR 实测：畸变/暗角/曝光/预览

## 构建

`build.bat`（仅 Release）→ `out\SaveZ8.UI.exe` 单文件（self-contained、原生库内嵌压缩、无 PDB）。路径由 `Directory.Build.props` 统一：`build\obj`、`build\bin`、`build\publish`、`out`。原生链：libraw 子模块 nmake（`libs\libraw.dll` 存在跳过）→ wrapper 恒重建。冒烟：`out\SaveZ8.UI.exe --selftest <nef>`。

## 待办

- 400mm 端验证镜头校正映射（理论焦距无关，仅缺实测）
- 暗角写入条件（block8+0x04≠0）为两文件归纳，未证伪

## 参考

JO.html（y-g-jiang.github.io/JO.html）：修复算法与 DNG 标签集出处；对照表见 README.md。
