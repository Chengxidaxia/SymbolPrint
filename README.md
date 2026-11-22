# SymbolPrint · 电子盖章

把「章」写进文件自身：只往文件的空白区追加数据，**不改动原有字节**，文件照常打开、照常使用。
同时支持把印章图案直接贴到图片 / PDF 上，并能在事后验证文件有没有被改动。

> 当前版本 **1.0.3**（Release 标签 `r1.0.3`）。主程序是单个 `SymbolPrint.exe`，
> 少数运行时依赖以 `.dll` 形式放在同目录。

---

## 它做什么

| 能力 | 说明 |
|---|---|
| 盖章 | 把一份加密的盖章记录追加到目标文件里，原内容一个字节都不改 |
| 验章 | 校验信封结构、载荷完整性、记录自校验、原文件哈希、章指纹，逐项给出结论 |
| 摘章 | 精确摘掉本程序写入的那几段字节，文件恢复成盖章前的样子（逐字节一致） |
| 可视化盖章 | 全屏打开文件，印章跟随鼠标、滚轮缩放，右键确认落章 |
| 图案编辑器 | 内置 SVG 图案编辑（WebView2 承载的网页编辑器） |
| 章文件 | `.bin` 章：私钥 + 可选内置 SVG 图案，可加口令保护 |
| 批量 | 一次添加多个文件 / 整个文件夹，逐个盖章并显示进度与结果 |

## 支持的载体

同一份记录按文件真实结构选择写入位置（**先看文件头，扩展名只作辅助**，
所以改过后缀的文件也能选对载体）：

| 载体 | 适用文件 | 写入位置 |
|---|---|---|
| 尾部追加 | PNG / JPEG / GIF / BMP / TIFF / WebP / ICO / PDF / PSD | 文件末尾 |
| ZIP 注释区 | ZIP / DOCX / PPTX / XLSX / JAR / APK / EPUB | EOCD 注释区（不动归档条目） |
| 7z 尾部 | 7Z | NextHeader 与 StartHeader 之间 |
| RTF 组 | RTF | 忽略型目标组 `{\*\symprint ...}` |
| 零宽字符 | TXT / MD / Markdown / LOG | 文本末尾 |
| 章保留区 | 章 `.bin` 自身 | 预留的保留区 |

## 安全模型（请先读这一段）

- 章文件里存的是**私钥**。谁的 `.bin` 在谁手里，谁就能盖这个章 —— 请像对待私钥一样保管。
- 可用 `--pass` 给章加口令保护；未解锁的章不能盖章。
- 验章能回答两件事：**这份记录有没有被改**（记录自校验 / 载荷完整性），
  **这个文件是不是当初盖的那份**（原文件 SHA3-512）。
- 不带章验证（`verify` 不给 `--seal`）只能证明记录本身完整，**不能证明来源**，结果里会明确标注这一点。
- 盖章默认**保留文件里已有的记录并追加新记录**（同一文件可叠多份）；验章以最后一条为准，
  要清干净请用「删除章」或 `unseal --all`。

## 系统要求

- **Windows 10 22H2（build 19045）或更高**；Windows 11 均可。
  低于该版本启动时会提示（不硬退，但不在支持范围内）。
- 图案编辑器需要 **Microsoft Edge WebView2 运行时**：Windows 11 一般自带，Windows 10 需要装一次。
  盖章、验章、摘章不依赖它。
- 发布包二选一：

| 包 | 体积 | 依赖 |
|---|---|---|
| `SymbolPrint-1.0.3-win-x64.zip` | **3.8 MB** | 需要 .NET 9 Desktop Runtime（装一次） |
| `SymbolPrint-1.0.3-win-x64-selfcontained.zip` | 46 MB | 免装运行时，解压即用 |

## 快速开始

### 精简包（推荐）

1. 解压到任意目录。主程序是 `SymbolPrint.exe`，**同目录的 .dll 是它的依赖，不要单独拷走 exe**。
2. 首次运行若提示缺少运行时，装 .NET 9 Desktop Runtime：
   - 下载页：<https://dotnet.microsoft.com/download/dotnet/9.0>（选 Desktop Runtime x64）
   - 或命令行：`winget install Microsoft.DotNet.DesktopRuntime.9`
3. 双击 `SymbolPrint.exe`。

### 免装运行时（自包含包）

解压即用，不要求目标机器装任何东西，代价是包体大（46 MB，单个 exe）。

### 从源码构建

需要 .NET SDK 9.0（`global.json` 指定 `9.0.318`，允许前滚到更高特性带）。

```bash
git clone https://github.com/Chengxidaxia/SymbolPrint.git
cd SymbolPrint
dotnet build SymbolPrint.sln -c Release
dotnet run --project src/SymbolPrint.App -c Release
```

### 打包

精简包（框架依赖，主 exe 238 KB + 同目录依赖库）：

```bash
dotnet publish src/SymbolPrint.App/SymbolPrint.App.vbproj -c Release -r win-x64 `
  --self-contained false `
  -p:DebugType=None -p:DebugSymbols=false `
  -p:AllowedReferenceRelatedFileExtensions=none `
  -o dist/fx
```

自包含单文件（46 MB，无需运行时）：

```bash
dotnet publish src/SymbolPrint.App/SymbolPrint.App.vbproj -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o dist/sc
```

## 主题与窗口

- 「高级设置 → 通用 → 主题」：System（跟随 Windows）/ Light / Dark，**重启后生效**
- 深色不是把浅色反相：底色用中性灰蓝而非纯黑，正文不用纯白，强调色提亮保证深底对比度
- 默认窗口 1920×1080；屏幕装不下（1080p 去掉任务栏）时占可用区 96%，约 1843×998

## 高级设置

「高级设置」页默认只列出**当前版本真正会读取**的参数，避免出现"改了不起作用"的假开关。
勾选顶部「显示高级与预留参数」才会展开另外两类：

- **高级参数**：会影响结果，但一般不需要动（如 PBKDF2 迭代次数、备份后缀）；
- **预留参数**：配置里有、当前版本没有代码读取，改了不会生效，界面上会逐行标注。

界面上出现的每一项都**真的是程序会读的**，没有"改了没效果"的开关。

### 1.0.2 已接入并生效

| 分组 | 参数 |
|---|---|
| 通用 | 自动备份、备份后缀、打开文件前结构校验 |
| 加密 | PBKDF2 迭代、ECC 曲线、AES 位数、GCM Tag 长度、PBKDF2 盐长度、派生密钥长度、HKDF info、做章默认口令保护、章解锁保持时间 |
| 压缩 | Brotli 质量、压缩算法、变大就放弃、最小压缩体积、章字节上限 |
| 载体 | 载体优先级、ZIP 注释区上限、零宽字符集、RTF 组名 |
| 图案 | 不透明度、旋转、默认锚点、水平/垂直偏移、页边距、最大宽高、抗锯齿、JPEG 质量、背景透明 |
| PDF | 页码范围、渲染 DPI、图案宽度(mm)、锚点、偏移(mm)、置于内容之下 |
| 验证 | 严格模式、无章提示、原文件摘要、章身份、记录自哈希、时间戳顺序、显示原始哈希、报告格式、验章后复制报告 |
| 进度 | 显示进度条、不确定进度、字节明细、延迟出现、刷新间隔、ESC 取消 |
| 界面 | 字号、章描述显示、默认展开高级、覆盖前确认、记住窗口位置、启动时打开上次文件 |

### 记录格式 v2 与兼容性

1.0.2 起新盖的记录使用 v2 格式（头部记录各字段长度与 HKDF info），
**旧 v1 记录仍然可以验证**：解析按版本分支，老文件不需要重新盖章。
加密长度类参数只影响新记录。

### 为什么有些设计期参数被移除了

这些项从未被任何代码读取，或只在特定条件下有意义，留着会让人以为改了有效果，已从设置中删除：
外部 7-Zip 路径（7z 支持是纯托管实现，不调用外部程序）、允许重打包归档、临时目录、保留临时文件、
最近文件数量、并行度（逐个盖章是有意为之，并发会让进度与错误定位变复杂）、
PDF 不透明度（与界面统一不透明度重复）、PDF 增量更新（PDFsharp 只能整份重写）、
GCM Nonce 长度（.NET 的 AES-GCM 只接受 12 字节）、语言与主题（需要完整的 i18n 与深色主题体系）、
文本编码方案与插入位置（当前只实现了零宽二进制 + 末尾插入）、打开时自动验章。

## 命令行

`spcli` 与 GUI 共用同一套核心库，适合脚本化与回归验证：

```
spcli selftest                     密码学/载体自检
spcli hash <文件>                   流式算 SHA3-512
spcli make   <输出.bin> [--label 名] [--desc 描述] [--seal-bytes 文件] [--pass 口令] [--svg 图案.svg]
spcli stamp  <文件> --seal <章.bin> [--pass 口令] [--quality 0-11]
spcli verify <文件> [--seal <章.bin>] [--pass 口令]
spcli unseal <文件> [--all]         摘章；--all 摘到干净为止
spcli e2etest                      全格式端到端（做章 → 盖章 → 验章 → 篡改检测）
spcli carriertest                  载体自测（ZIP 注释区不动原有字节）
spcli vectors write|check [文件]    双版本字节级一致性向量
spcli svg2png <in.svg> <out.png> [--width 512]
```

贴图参数：`--mark-svg` / `--mark-png` / `--mark-x` / `--mark-y` / `--mark-w` /
`--mark-opacity` / `--mark-rotate` / `--mark-page` / `--no-backup`。

## 目录结构

```
src/SymbolPrint.Core/    核心库：密码学、信封、载体、盖章/验章/摘章
src/SymbolPrint.App/     WinForms 界面（VB.NET）
src/SymbolPrint.Cli/     命令行
assets/                  程序图标、图案编辑器页面
tests/vectors/           字节级一致性测试向量
tools/                   开发期小工具
cs/                      早期 C# 原型（未参与构建，仅作历史留存）
```

## 界面自检（开发用）

界面布局问题（文字截断、控件重叠、越界）可以在改完后自己量：

```bash
SymbolPrint.exe --audit out.txt --size 1480x900
SymbolPrint.exe --sweep  out.txt        # 按宽度逐档扫描，附带单次重排耗时
SymbolPrint.exe --hovertest out.txt     # 用真实鼠标验证按钮悬停是否变色
```

## 说明

- 目标框架 `net9.0-windows`，界面为 WinForms（VB.NET），高 DPI 按 PerMonitorV2 处理。
- 图案编辑器依赖系统已安装的 WebView2 运行时（Win10/11 一般自带）。

## 许可

尚未指定开源许可。在明确许可前，请勿直接用于商业分发。
