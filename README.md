# SymbolPrint · 电子盖章

把「章」写进文件自身：只往文件的空白区追加数据，**不改动原有字节**，文件照常打开、照常使用。
同时支持把印章图案直接贴到图片 / PDF 上，并能在事后验证文件有没有被改动。

> 当前版本 **1.2.2**（Release 标签 `r1.2.2`）。主程序是单个 `SymbolPrint.exe`，
> 少数运行时依赖以 `.dll` 形式放在同目录。

---

## 它做什么

| 能力 | 说明 |
|---|---|
| 盖章 | 把一份加密的盖章记录追加到目标文件里，原内容一个字节都不改 |
| 验章 | 校验信封结构、载荷完整性、记录自校验、原文件哈希、章指纹，逐项给出结论 |
| 摘章 | 精确摘掉本程序写入的那几段字节，文件恢复成盖章前的样子（逐字节一致） |
| 可视化盖章 | 全屏打开文件，印章跟随鼠标、滚轮缩放，右键确认落章；2560×1440 下约 4ms/帧 |
| 图案编辑器 | 内置 **SVG-Edit 7.4.2** 内核：贝塞尔路径/节点编辑、渐变、图层、导入导出，离线可用 |
| 章文件 | `.bin` 章：私钥 + 可选内置 SVG 图案，可加口令保护 |
| 批量 | 一次添加多个文件 / 整个文件夹，逐个盖章并显示进度与结果 |
| 多语言 | 简体 / 繁體 / 文言 / English，设置里切换，缺失自动回退简体 |

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
  想让别人在没有章的情况下也能验完整性，需要记录格式改成签名制（见下文「已知限制」）。
- 盖章默认**保留文件里已有的记录并追加新记录**（同一文件可叠多份）；验章以最后一条为准，
  要清干净请用「删除章」或 `unseal --all`。

## 系统要求

- **Windows 10 22H2（build 19045）或更高**；Windows 11 均可。
  低于该版本启动时会提示（不硬退，但不在支持范围内）。
- 图案编辑器需要 **Microsoft Edge WebView2 运行时**：Windows 11 一般自带，Windows 10 需要装一次。
  盖章、验章、摘章不依赖它。
- 解压后 **`editor\` 目录必须和 `SymbolPrint.exe` 同级**（编辑器内核在里面，缺了会明确报错而不是静默失败）。
- 发布包二选一：

| 包 | 体积 | 依赖 |
|---|---|---|
| `SymbolPrint-1.2.2-win-x64.zip` | **5.4 MB** | 需要 .NET 9 Desktop Runtime（装一次） |
| `SymbolPrint-1.2.2-win-x64-selfcontained.zip` | 44 MB | 免装运行时，解压即用 |

## 快速开始

### 精简包（推荐）

1. 解压到任意目录。主程序是 `SymbolPrint.exe`，**同目录的 .dll 是它的依赖，不要单独拷走 exe**。
2. 首次运行若提示缺少运行时，装 .NET 9 Desktop Runtime：
   - 下载页：<https://dotnet.microsoft.com/download/dotnet/9.0>（选 Desktop Runtime x64）
   - 或命令行：`winget install Microsoft.DotNet.DesktopRuntime.9`
3. 双击 `SymbolPrint.exe`。

### 免装运行时（自包含包）

解压即用，不要求目标机器装任何东西，代价是包体大（44 MB，单个 exe）。

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

自包含单文件（44 MB，无需运行时）：

```bash
dotnet publish src/SymbolPrint.App/SymbolPrint.App.vbproj -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o dist/self
```

> 若自包含包卡在复制 `editor\`（几百个小文件）上不结束，可以直接把精简包的
> `dist/fx/editor` 覆盖到 `dist/self/editor` —— 两版的编辑器目录逐字节相同。

## 多语言

界面支持 **四种语言**，在「高级设置 → 通用 → 语言 / Language」里切换，**重启后生效**；
默认 `System` 跟随 Windows 显示语言（繁体地区自动走繁体）。

| 标识 | 语言 | 说明 |
|---|---|---|
| `zh_CN` | 简体中文 | 原文即译文，不加载语言包 |
| `zh_TW` | 繁體中文 | 臺灣用語（檔案 / 設定 / 預設 / 載入 / 位元組） |
| `zh_AL` | 文言 | 风格化改写：鈐印、驗印、製印、詳設 |
| `en_US` | English | |

实现方式：**以简体中文原文作为翻译 key**，语言包里查不到就回退简体，
所以界面永远不会出现空白或 `btn.save` 这种 key 名。

```json
{
  "strings": {
    "盖章": "鈐印",
    "验章": "驗印"
  }
}
```

- 语言包：`assets/i18n/{zh_TW,zh_AL,en_US}.json`（每包 278 条），编译时内嵌进 `SymbolPrint.Core`，不额外落文件
- 翻译层：`SymbolPrint.Core.Localization.L`（`L.T` / `L.F`），Core 与 App 共用 ——
  验章结论这类 Core 产生的消息也能翻
- 界面应用：`App.UiTranslator.ApplyLanguage(control)` 遍历控件树，只替换**整条精确命中**的文本，
  文件名、用户输入不会被误翻

**两个不能翻译的地方**（踩过）：

| 位置 | 原因 | 做法 |
|---|---|---|
| `ListBox` / `ComboBox` 的 `Items` | 条目同时是业务键（分组名匹配元数据、选项文本回写枚举），替换成英文后保存/重建会匹配不到 | 保持中文，只在自绘（`DrawItem`）里取 `L.T` |
| 动态拼接的 `$"文件：{x}"` | 整条文本含变量，字典匹配不上 | 改写成 `L.F("文件：{0}", x)`，模板整体翻译 |

漏翻的地方不会报错，只是显示简体。用下面这条命令可以导出真实清单（而不是靠 grep 猜）：

```bash
SymbolPrint.exe --i18nreport out.txt           # 导出界面上全部文案 + 覆盖率 + 未翻译清单
SymbolPrint.exe --lang zh_TW --page settings   # 临时按指定语言跑并直达页面（不写回配置）
SymbolPrint.exe --lang zh_AL                   # 文言界面（趣味向，术语保留现代写法）
```

覆盖率：主窗体 84 / 84；验章报告的检查项与结论、弹窗提示均已接入。

## 主题与窗口

- 「高级设置 → 通用 → 主题」：System（跟随 Windows）/ Light / Dark，**重启后生效**
- 深色不是把浅色反相：底色用中性灰蓝而非纯黑，正文不用纯白，强调色提亮保证深底对比度
- 默认窗口 1920×1080；屏幕装不下（1080p 去掉任务栏）时占可用区 96%，约 1843×998

## 高级设置

「高级设置」页**只列出当前版本真正会读取的参数**，避免出现"改了不起作用"的假开关。

勾选顶部「显示高级参数」会再展开一轮**会影响结果、但一般不需要动**的项
（如 PBKDF2 迭代次数、备份后缀）。

> ⚠️ **预留参数一律不显示**（配置文件里有、但没有任何代码读取）。
> 给 `AppSettings` 新增 `[Setting]` 属性后，必须同步加进
> `SettingsPage.InEffectKeys` 白名单，否则界面上根本不会出现这一项 —— 1.2.2 之前
> 语言设置就是这么"消失"的。

### 已接入并生效

| 分组 | 参数 |
|---|---|
| 通用 | 主题、**语言**、自动备份、备份后缀、打开文件前结构校验 |
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
GCM Nonce 长度（.NET 的 AES-GCM 只接受 12 字节）、
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
src/SymbolPrint.Core/    核心库：密码学、信封、载体、盖章/验章/摘章、i18n 翻译层
src/SymbolPrint.App/     WinForms 界面（VB.NET）
src/SymbolPrint.Cli/     命令行
assets/editor/svgedit/    SVG-Edit 7.4.2 发行版（随包复制到 <程序目录>\editor\）
assets/i18n/             语言包（编译时内嵌，不随包分发）
tests/vectors/           字节级一致性测试向量
tools/                   开发期小工具
cs/                      C# 版核心与一致性校验器（不参与主程序构建，见下）
```

## 开发期诊断参数

```bash
# 界面布局：文字截断 / 控件重叠 / 越界
SymbolPrint.exe --audit out.txt --size 1480x900
SymbolPrint.exe --sweep  out.txt        # 按宽度逐档扫描，附带单次重排耗时
SymbolPrint.exe --hovertest out.txt     # 用真实鼠标验证按钮悬停是否变色

# 可视化盖章的绘制性能
SymbolPrint.exe --visualbench out.txt   # 每帧耗时（改前/改后对照，旧路径原样复刻实测）
SymbolPrint.exe --visualshot out.png    # 渲染界面，核对绘制结果

# 多语言
SymbolPrint.exe --i18nreport out.txt    # 文案清单 + 覆盖率
SymbolPrint.exe --lang <zh_CN|zh_TW|zh_AL|en_US> [--page stamp|verify|make|settings]
```

## C# 版一致性校验

`cs/` 是独立的 C# 实现，**不参与主程序构建**，作用是把格式和密码学用另一种语言重写一遍，
与 VB 版逐字节比对 —— 两边只要有一个字节不同，这里立刻红。

```bash
cd cs
dotnet build SymbolPrint.Cli/SymbolPrint.Cli.csproj -c Release
SymbolPrint.Cli/bin/Release/net9.0-windows/spcli-cs.exe conformance
```

覆盖 10 项：SHA3-512、HMAC-SHA3-512、HKDF、PBKDF2、AES-GCM、ECIES，
以及**章文件格式（stamp_bin）**和**盖章记录 v1（seal_record）**。当前 10/10 全部通过。

这项检查的价值在于：格式定义不会因为某一边的"顺手改动"而悄悄漂移。
改了 VB 版任何一种字节布局，都应重新跑一次 conformance。

## 版本演进（1.0.3 → 1.2.2）

| 版本 | 主要变化 |
|---|---|
| 1.1.0 | 图案编辑器内核换成 SVG-Edit 7.4.2（原自研编辑器能力不足） |
| 1.1.1 | WebView2 缓存改到 `%LOCALAPPDATA%\SymbolPrint\WebView2`，绿色版目录不再被撑大 |
| 1.1.2 | 可视化盖章性能修复：134 ms/帧 → 4 ms/帧（页面图与阴影预先烘焙，只重绘章所在矩形） |
| 1.2.0 | 界面中英双语 |
| 1.2.1 | 扩到简 / 繁 / 文言 / 英四种语言；验章报告与弹窗文案接入翻译 |
| 1.2.2 | 修复设置页看不到语言项（`InEffectKeys` 白名单漏项） |

## 已知限制

- **无章验证只能证明记录没被改，不能证明来源**。记录里的原文件哈希是用章公钥加密的，
  没有私钥就取不出来。要让对方在没有章的情况下也能验"文件有没有被改"，需要把
  关键摘要移到明文并用私钥签名（记录格式 v3），尚未实现。
- 载体只支持上表列出的格式；其它格式会明确提示不支持，不会硬写。
- 印章图案一旦烧进图片 / PDF 就无法撤销（只有尾部记录可摘）。

## 说明

- 目标框架 `net9.0-windows`，界面为 WinForms（VB.NET），高 DPI 按 PerMonitorV2 处理。
- 图案编辑器依赖系统已安装的 WebView2 运行时（Win10/11 一般自带）。

## 许可

尚未指定开源许可。在明确许可前，请勿直接用于商业分发。

### 第三方组件

| 组件 | 版本 | 许可 | 说明 |
|---|---|---|---|
| [SVG-Edit](https://github.com/SVG-Edit/svgedit) | 7.4.2 | MIT（另有 Apache-2.0 / X11 等依赖，见 `editor/licenseInfo.json`） | 图案编辑器内核，代码原样随包分发在 `editor/` 目录，未做修改 |
| PDFsharp | 1.50 | MIT | PDF 读写 |
| BouncyCastle.Cryptography | 2.7.0 | MIT | 密码学原语 |
| Microsoft.Web.WebView2 | 1.0.3351.48 | 微软 SDK 许可 | 承载编辑器页面 |
| Svg (SVG.NET) | 3.4.8 | MS-PL | 图案光栅化 |

SVG-Edit 的完整许可文本见 `editor/LICENSE-MIT.txt`，各依赖的许可归属见 `editor/licenseInfo.json`。
