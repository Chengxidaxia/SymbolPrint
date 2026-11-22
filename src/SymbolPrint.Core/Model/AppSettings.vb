Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Text.Json
Imports System.Text.Json.Serialization

Namespace Model

    ''' <summary>
    ''' 设置项元数据：标签 / 分组 / 单位 / 取值范围 / 可选项 / 是否"高级"。
    ''' 高级选项面板完全靠这些元数据自动生成控件 —— 加一个参数只要加一个属性 + 一个特性，
    ''' 不用再改界面代码（VB 版和 C# 版都用同一套元数据规则）。
    ''' </summary>
    <AttributeUsage(AttributeTargets.Property)>
    Public NotInheritable Class SettingAttribute
        Inherits Attribute

        Public Property Label As String
        Public Property Group As String
        Public Property Unit As String
        Public Property MinValue As Double
        Public Property MaxValue As Double
        Public Property Choices As String()
        Public Property Advanced As Boolean
        Public Property Help As String

        Public Sub New(label As String,
                       Optional group As String = "",
                       Optional unit As String = "",
                       Optional minValue As Double = Double.NaN,
                       Optional maxValue As Double = Double.NaN,
                       Optional choices As String() = Nothing,
                       Optional advanced As Boolean = False,
                       Optional help As String = "")
            Me.Label = label
            Me.Group = group
            Me.Unit = unit
            Me.MinValue = minValue
            Me.MaxValue = maxValue
            Me.Choices = choices
            Me.Advanced = advanced
            Me.Help = help
        End Sub

        Public ReadOnly Property HasRange As Boolean
            Get
                Return Not Double.IsNaN(MinValue) AndAlso Not Double.IsNaN(MaxValue)
            End Get
        End Property
    End Class

    Public Enum CompressionAlgorithm As Byte
        None = 0
        Brotli = 1
    End Enum

    ''' <summary>文本（txt/md）里的隐形编码方案。</summary>
    Public Enum TextEncodingProfile As Byte
        ''' <summary>U+200B/200C/200D/2060/FEFF，真零宽，3 字节/比特。</summary>
        ZeroWidthBinary = 0
        ''' <summary>变体选择符 U+FE00–FE0F（4 bit/字符）+ U+E0100–E01EF，体积小但兼容性略差。</summary>
        VariationSelectors = 1
        ''' <summary>ASCII 空格/Tab（1 字节/比特）。会被编辑器 trim、破坏 Markdown 语义，默认禁用。</summary>
        AsciiWhitespace = 2
    End Enum

    Public Enum TextInsertPosition As Byte
        ''' <summary>追加在文件末尾（换行后），最不打扰原文。</summary>
        FileEnd = 0
        ''' <summary>每行行尾分散（更隐蔽，但更容易被 trim）。</summary>
        PerLineTail = 1
        ''' <summary>插在文件开头（首个不可见位置）。</summary>
        FileStart = 2
    End Enum

    Public Enum ImageAnchor As Byte
        TopLeft = 0
        TopCenter = 1
        TopRight = 2
        MiddleLeft = 3
        Center = 4
        MiddleRight = 5
        BottomLeft = 6
        BottomCenter = 7
        BottomRight = 8
        ''' <summary>按绝对像素/毫米偏移定位。</summary>
        Custom = 9
    End Enum

    Public Enum AppTheme As Byte
        System = 0
        Light = 1
        Dark = 2
    End Enum

    Public Enum RasterEngine As Byte
        ''' <summary>WebView2 渲染 SVG → PNG（保真度最高，与编辑器所见一致）。</summary>
        WebView2 = 0
        ''' <summary>.NET 自带的 SVG 渲染（无浏览器依赖，但滤镜/字体会降级）。</summary>
        Builtin = 1
    End Enum

    ' ==================== 各分组参数 ====================

    Public Class GeneralOptions
        <Setting("界面语言", "通用", choices:={"zh-CN", "en-US"}, help:="重启后生效。")>
        Public Property Language As String = "zh-CN"

        <Setting("主题", "通用", choices:={"System", "Light", "Dark"})>
        Public Property Theme As AppTheme = AppTheme.System

        <Setting("临时目录", "通用", help:="留空则用系统临时目录。大文件操作会在这里落临时文件，建议放在空间充足的盘。")>
        Public Property TempDirectory As String = ""

        <Setting("并行度", "通用", unit:="线程", minValue:=1, maxValue:=64, help:="批量盖章时的并发文件数。")>
        Public Property MaxParallelism As Integer = 1

        <Setting("保留临时文件", "通用", advanced:=True, help:="调试用；正常使用请关闭。")>
        Public Property KeepTempFiles As Boolean = False

        <Setting("最近文件数量", "通用", minValue:=0, maxValue:=50)>
        Public Property RecentFilesLimit As Integer = 10

        <Setting("自动备份原文件", "通用", advanced:=True, help:="盖章前把原文件复制一份 .bak，失败时可回滚。")>
        Public Property AutoBackupOriginal As Boolean = True

        <Setting("备份后缀", "通用", advanced:=True, choices:={".bak", ".orig", ".symbolprint-backup"})>
        Public Property BackupSuffix As String = ".bak"

        <Setting("打开文件前校验", "通用", advanced:=True, help:="加载时先检查结构（zip/7z/pdf）是否完整，避免把损坏文件改坏。")>
        Public Property ValidateBeforeOpen As Boolean = True
    End Class

    Public Class CryptoOptions
        <Setting("ECC 曲线", "加密", choices:={"nistP256", "nistP384", "nistP521"})>
        Public Property Curve As String = "nistP256"

        <Setting("PBKDF2 迭代次数", "加密", unit:="次", minValue:=1000, maxValue:=2000000, advanced:=True,
                 help:="越高越抗暴力破解，也越慢。默认 40000 在普通机器上约 250ms。")>
        Public Property Pbkdf2Iterations As Integer = 40000

        <Setting("PBKDF2 盐长度", "加密", unit:="字节", minValue:=8, maxValue:=64, advanced:=True)>
        Public Property Pbkdf2SaltBytes As Integer = 16

        <Setting("派生密钥长度", "加密", unit:="字节", minValue:=16, maxValue:=64, advanced:=True)>
        Public Property DerivedKeyBytes As Integer = 32

        <Setting("AES 密钥长度", "加密", unit:="bit", choices:={"128", "192", "256"})>
        Public Property AesKeyBits As Integer = 256

        <Setting("GCM Nonce 长度", "加密", unit:="字节", minValue:=12, maxValue:=16, advanced:=True,
                 help:="GCM 标准值 12；改大只在极其特殊的互操作场景有意义。")>
        Public Property GcmNonceBytes As Integer = 12

        <Setting("GCM Tag 长度", "加密", unit:="字节", minValue:=12, maxValue:=16, advanced:=True)>
        Public Property GcmTagBytes As Integer = 16

        <Setting("HKDF info 字符串", "加密", advanced:=True, help:="ECIES 的密钥派生上下文，改了两边必须一致。")>
        Public Property HkdfInfo As String = "SymbolPrint-ECIES-v1"

        <Setting("章私钥默认口令保护", "加密", help:="新建章时默认勾选口令保护。")>
        Public Property ProtectStampKeyByDefault As Boolean = False

        <Setting("解锁保持时间", "加密", unit:="分钟", minValue:=0, maxValue:=480, advanced:=True,
                 help:="0 表示本次会话内一直保持解锁。")>
        Public Property UnlockKeepMinutes As Integer = 0
    End Class

    Public Class CompressionOptions
        <Setting("压缩算法", "压缩", choices:={"None", "Brotli"})>
        Public Property Algorithm As CompressionAlgorithm = CompressionAlgorithm.Brotli

        <Setting("Brotli 质量", "压缩", minValue:=0, maxValue:=11,
                 help:="0 最快、11 最小。9 是体积/速度平衡点；11 在 100MB 上可能要几十秒。")>
        Public Property BrotliQuality As Integer = 9

        <Setting("最小压缩体积", "压缩", unit:="字节", minValue:=0, maxValue:=1048576, advanced:=True,
                 help:="小于该值直接不压缩，省 CPU。")>
        Public Property MinSizeToCompress As Integer = 256

        <Setting("处理块大小", "压缩", unit:="KB", minValue:=16, maxValue:=8192, advanced:=True,
                 help:="影响进度刷新粒度与内存占用。")>
        Public Property BlockSizeKb As Integer = 1024

        <Setting("变大就放弃压缩", "压缩", advanced:=True, help:="压缩后反而更大时原样保存。")>
        Public Property SkipIfLarger As Boolean = True

        <Setting("章字节上限", "压缩", unit:="字节", minValue:=1024, maxValue:=1073741824, advanced:=True)>
        Public Property SealBytesMax As Long = 64L * 1024 * 1024
    End Class

    Public Class CarrierSettings
        <Setting("载体优先级", "载体", advanced:=True, help:="按顺序尝试；逗号分隔。")>
        Public Property PreferredOrder As String = "trailer,zip-comment,7z-tail,rtf-group,zero-width"


        <Setting("ZIP 注释区上限", "载体", unit:="字节", minValue:=1024, maxValue:=65535, advanced:=True,
                 help:="ZIP 规范硬上限 65535，超了只能重打包。")>
        Public Property ZipCommentMaxBytes As Integer = 65535


        <Setting("文本编码方案", "载体", choices:={"ZeroWidthBinary", "VariationSelectors", "AsciiWhitespace"},
                 help:="AsciiWhitespace 会破坏 Markdown 语义且会被编辑器 trim，慎用。")>
        Public Property TextEncodingProfile As TextEncodingProfile = TextEncodingProfile.ZeroWidthBinary

        <Setting("文本插入位置", "载体", choices:={"FileEnd", "PerLineTail", "FileStart"})>
        Public Property TextInsertPosition As TextInsertPosition = TextInsertPosition.FileEnd

        <Setting("零宽字符集", "载体", advanced:=True,
                 help:="用于二进制 0/1 的两个码点，逗号分隔十六进制。")>
        Public Property ZeroWidthPair As String = "200B,200C"

        <Setting("RTF 组名", "载体", advanced:=True, help:="RTF 忽略型目标组的名字。")>
        Public Property RtfGroupName As String = "symprint"

        <Setting("打开时自动验章", "载体", advanced:=True)>
        Public Property VerifyOnOpen As Boolean = False
    End Class

    Public Class ImageOptions
        <Setting("最大宽度", "图案", unit:="px", minValue:=16, maxValue:=20000)>
        Public Property MaxWidthPx As Integer = 1200

        <Setting("最大高度", "图案", unit:="px", minValue:=16, maxValue:=20000)>
        Public Property MaxHeightPx As Integer = 1200

        <Setting("不透明度", "图案", unit:="%", minValue:=1, maxValue:=100)>
        Public Property OpacityPercent As Integer = 100

        <Setting("默认锚点", "图案",
                 choices:={"Custom", "TopLeft", "TopCenter", "TopRight", "MiddleLeft", "Center", "MiddleRight", "BottomLeft", "BottomCenter", "BottomRight"},
                 help:="Custom 表示沿用界面上的百分比定位；选具体锚点后改为「锚点 + 边距 + 偏移」计算。")>
        Public Property Anchor As ImageAnchor = ImageAnchor.Custom

        <Setting("水平偏移", "图案", unit:="px", minValue:=-20000, maxValue:=20000, advanced:=True)>
        Public Property OffsetXPx As Integer = 24

        <Setting("垂直偏移", "图案", unit:="px", minValue:=-20000, maxValue:=20000, advanced:=True)>
        Public Property OffsetYPx As Integer = 24

        <Setting("页边距", "图案", unit:="px", minValue:=0, maxValue:=2000)>
        Public Property MarginPx As Integer = 24

        <Setting("旋转角度", "图案", unit:="度", minValue:=-180, maxValue:=180)>
        Public Property RotationDegrees As Integer = 0

        <Setting("渲染引擎", "图案", choices:={"WebView2", "Builtin"})>
        Public Property Rasterizer As RasterEngine = RasterEngine.WebView2

        <Setting("背景透明", "图案", advanced:=True)>
        Public Property TransparentBackground As Boolean = True

        <Setting("抗锯齿", "图案", advanced:=True)>
        Public Property AntiAlias As Boolean = True

        <Setting("JPEG 输出质量", "图案", unit:="%", minValue:=1, maxValue:=100, advanced:=True,
                 help:="仅在目标图片为 JPEG 或需要重新编码时使用。")>
        Public Property JpegQuality As Integer = 92

        <Setting("保留 EXIF", "图案", advanced:=True, help:="重编码图片时保留原始 EXIF。")>
        Public Property PreserveExif As Boolean = True
    End Class

    Public Class PdfOptions
        <Setting("页码范围", "PDF", help:="all / first / 1,3-5")>
        Public Property PageSelection As String = "all"

        <Setting("渲染 DPI", "PDF", unit:="DPI", minValue:=72, maxValue:=1200)>
        Public Property Dpi As Integer = 200

        <Setting("图案宽度", "PDF", unit:="mm", minValue:=1, maxValue:=500)>
        Public Property WidthMm As Double = 40.0

        <Setting("锚点", "PDF",
                 choices:={"Custom", "TopLeft", "TopCenter", "TopRight", "MiddleLeft", "Center", "MiddleRight", "BottomLeft", "BottomCenter", "BottomRight"},
                 help:="Custom 表示沿用界面上的百分比定位；选具体锚点后按「锚点 + 偏移(mm)」计算。")>
        Public Property Anchor As ImageAnchor = ImageAnchor.Custom

        <Setting("水平偏移", "PDF", unit:="mm", minValue:=-500, maxValue:=500, advanced:=True)>
        Public Property OffsetXMm As Double = 10.0

        <Setting("垂直偏移", "PDF", unit:="mm", minValue:=-500, maxValue:=500, advanced:=True)>
        Public Property OffsetYMm As Double = 10.0

        <Setting("置于内容之下", "PDF", advanced:=True, help:="避免遮挡正文。")>
        Public Property BehindContent As Boolean = False

    End Class

    Public Class VerifyOptions
        <Setting("严格模式", "验证", help:="任何一项校验失败都判为失败（关闭时仅警告）。")>
        Public Property StrictMode As Boolean = True

        <Setting("无章时提示局限性", "验证", help:="没有章的文件只能证明「当前内容未被改动」，不能证明来源。")>
        Public Property WarnIfNoSeal As Boolean = True

        <Setting("校验原文件摘要", "验证")>
        Public Property CheckOriginalHash As Boolean = True

        <Setting("校验章身份", "验证", help:="用章内公钥重算指纹，确认章文件未被替换。")>
        Public Property CheckStampId As Boolean = True

        <Setting("校验记录自身哈希", "验证")>
        Public Property CheckOwnHash As Boolean = True

        <Setting("时间戳单调检查", "验证", advanced:=True, help:="多枚章时检查盖章时间是否倒挂。")>
        Public Property CheckTimestampOrder As Boolean = True

        <Setting("显示原始十六进制", "验证", advanced:=True)>
        Public Property ShowRawHex As Boolean = False

        <Setting("报告格式", "验证", choices:={"Text", "Json", "Markdown"})>
        Public Property ReportFormat As String = "Text"

        <Setting("验章后复制报告", "验证", advanced:=True)>
        Public Property CopyReportToClipboard As Boolean = False
    End Class

    Public Class ProgressOptions
        <Setting("显示进度条", "进度")>
        Public Property Enabled As Boolean = True

        <Setting("延迟出现", "进度", unit:="ms", minValue:=0, maxValue:=5000,
                 help:="操作短于该时间就完全不显示，避免闪烁。")>
        Public Property DelayMs As Integer = 250

        <Setting("刷新间隔", "进度", unit:="ms", minValue:=16, maxValue:=1000, advanced:=True)>
        Public Property ThrottleMs As Integer = 60

        <Setting("显示不确定进度", "进度", advanced:=True, help:="耗时未知的计算显示转圈动画。")>
        Public Property ShowIndeterminate As Boolean = True

        <Setting("显示字节明细", "进度", advanced:=True)>
        Public Property ShowBytesDetail As Boolean = True

        <Setting("ESC 键取消", "进度", advanced:=True)>
        Public Property CancelOnEsc As Boolean = True
    End Class

    Public Class UiOptions
        <Setting("字号", "界面", unit:="pt", minValue:=7, maxValue:=24)>
        Public Property FontSize As Integer = 9

        <Setting("覆盖前确认", "界面")>
        Public Property ConfirmBeforeOverwrite As Boolean = True

        <Setting("默认展开高级选项", "界面", advanced:=True)>
        Public Property ShowAdvancedByDefault As Boolean = False

        <Setting("记住窗口位置", "界面", advanced:=True)>
        Public Property RememberWindowBounds As Boolean = True

        <Setting("启动时打开上次文件", "界面", advanced:=True)>
        Public Property ReopenLastFile As Boolean = False

        <Setting("显示章描述", "界面", advanced:=True)>
        Public Property ShowStampDescription As Boolean = True
    End Class

    ' ==================== 根对象 ====================

    Public NotInheritable Class AppSettings

        Public Const FormatVersion As Integer = 1

        Public Property Version As Integer = FormatVersion
        Public Property General As New GeneralOptions()
        Public Property Crypto As New CryptoOptions()
        Public Property Compression As New CompressionOptions()
        Public Property Carriers As New CarrierSettings()
        Public Property Image As New ImageOptions()
        Public Property Pdf As New PdfOptions()
        Public Property Verify As New VerifyOptions()
        Public Property Progress As New ProgressOptions()
        Public Property Ui As New UiOptions()

        ' ---- 持久化 ----

        Private Shared ReadOnly JsonOpts As JsonSerializerOptions = CreateJsonOptions()

        Private Shared Function CreateJsonOptions() As JsonSerializerOptions
            Dim o As New JsonSerializerOptions()
            o.WriteIndented = True
            o.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            o.Converters.Add(New JsonStringEnumConverter())
            Return o
        End Function

        ''' <summary>配置文件路径：便携式，放在程序目录 config 下；可用环境变量 SYMBOLPRINT_CONFIG 覆盖。</summary>
        Public Shared Function DefaultPath() As String
            Dim env = Environment.GetEnvironmentVariable("SYMBOLPRINT_CONFIG")
            If Not String.IsNullOrEmpty(env) Then Return env
            Return IO.Path.Combine(AppContext.BaseDirectory, "config", "settings.json")
        End Function

        Public Shared Function Load(Optional path As String = Nothing) As AppSettings
            If String.IsNullOrEmpty(path) Then path = DefaultPath()
            Try
                If Not File.Exists(path) Then Return New AppSettings()
                Dim json = File.ReadAllText(path)
                Dim s = JsonSerializer.Deserialize(Of AppSettings)(json, JsonOpts)
                If s Is Nothing Then Return New AppSettings()
                s.Normalize()
                Return s
            Catch
                ' 配置坏掉不能让程序起不来：回退默认值
                Return New AppSettings()
            End Try
        End Function

        Public Sub Save(Optional path As String = Nothing)
            If String.IsNullOrEmpty(path) Then path = DefaultPath()
            Normalize()
            Dim dir = IO.Path.GetDirectoryName(IO.Path.GetFullPath(path))
            If Not String.IsNullOrEmpty(dir) AndAlso Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)
            File.WriteAllText(path, JsonSerializer.Serialize(Me, JsonOpts), System.Text.Encoding.UTF8)
        End Sub

        ''' <summary>把所有越界/非法的值拉回合法范围（配置文件被手改也不会炸）。</summary>
        Public Sub Normalize()
            General.MaxParallelism = Clamp(General.MaxParallelism, 1, 64)
            General.RecentFilesLimit = Clamp(General.RecentFilesLimit, 0, 50)
            If String.IsNullOrEmpty(General.BackupSuffix) Then General.BackupSuffix = ".bak"

            Crypto.Pbkdf2Iterations = Clamp(Crypto.Pbkdf2Iterations, 1000, 2000000)
            Crypto.Pbkdf2SaltBytes = Clamp(Crypto.Pbkdf2SaltBytes, 8, 64)
            Crypto.DerivedKeyBytes = Clamp(Crypto.DerivedKeyBytes, 16, 64)
            Crypto.GcmNonceBytes = Clamp(Crypto.GcmNonceBytes, 12, 16)
            Crypto.GcmTagBytes = Clamp(Crypto.GcmTagBytes, 12, 16)
            If Crypto.AesKeyBits <> 128 AndAlso Crypto.AesKeyBits <> 192 AndAlso Crypto.AesKeyBits <> 256 Then Crypto.AesKeyBits = 256
            Dim curveName As String = If(Crypto.Curve, "").ToLowerInvariant()
            If Not (New String() {"nistp256", "nistp384", "nistp521"}).Contains(curveName) Then Crypto.Curve = "nistP256"

            Compression.BrotliQuality = Clamp(Compression.BrotliQuality, 0, 11)
            Compression.BlockSizeKb = Clamp(Compression.BlockSizeKb, 16, 8192)
            Compression.MinSizeToCompress = Clamp(Compression.MinSizeToCompress, 0, 1048576)
            Compression.SealBytesMax = Math.Max(1024L, Compression.SealBytesMax)

            Carriers.ZipCommentMaxBytes = Clamp(Carriers.ZipCommentMaxBytes, 1024, 65535)
            If String.IsNullOrEmpty(Carriers.RtfGroupName) Then Carriers.RtfGroupName = "symprint"

            Image.OpacityPercent = Clamp(Image.OpacityPercent, 1, 100)
            Image.JpegQuality = Clamp(Image.JpegQuality, 1, 100)
            Image.MarginPx = Clamp(Image.MarginPx, 0, 2000)

            Pdf.Dpi = Clamp(Pdf.Dpi, 72, 1200)
            If Pdf.WidthMm <= 0 Then Pdf.WidthMm = 40.0

            Progress.DelayMs = Clamp(Progress.DelayMs, 0, 5000)
            Progress.ThrottleMs = Clamp(Progress.ThrottleMs, 16, 1000)

            Ui.FontSize = Clamp(Ui.FontSize, 7, 24)
        End Sub

        Private Shared Function Clamp(v As Integer, lo As Integer, hi As Integer) As Integer
            If v < lo Then Return lo
            If v > hi Then Return hi
            Return v
        End Function

        ''' <summary>按高级选项面板的结构导出全部参数（分组 → 项）。UI 直接照着渲染即可。</summary>
        Public Function Describe() As List(Of SettingEntry)
            Dim list As New List(Of SettingEntry)()
            For Each holder In {CType(Me, Object), CType(General, Object), CType(Crypto, Object), CType(Compression, Object),
                                CType(Carriers, Object), CType(Image, Object), CType(Pdf, Object), CType(Verify, Object),
                                CType(Progress, Object), CType(Ui, Object)}
                For Each p In holder.GetType().GetProperties(BindingFlags.Public Or BindingFlags.Instance)
                    Dim attr = TryCast(p.GetCustomAttribute(Of SettingAttribute)(), SettingAttribute)
                    If attr Is Nothing Then Continue For
                    Dim e As New SettingEntry()
                    e.Group = If(String.IsNullOrEmpty(attr.Group), "其他", attr.Group)
                    e.Label = attr.Label
                    e.Key = p.Name
                    e.Unit = attr.Unit
                    e.Help = attr.Help
                    e.Advanced = attr.Advanced
                    e.MinValue = attr.MinValue
                    e.MaxValue = attr.MaxValue
                    e.Choices = attr.Choices
                    e.Value = p.GetValue(holder)
                    e.ValueType = p.PropertyType
                    list.Add(e)
                Next
            Next
            Return list
        End Function
    End Class

    Public NotInheritable Class SettingEntry
        Public Property Group As String
        Public Property Label As String
        Public Property Key As String
        Public Property Unit As String
        Public Property Help As String
        Public Property Advanced As Boolean
        Public Property MinValue As Double
        Public Property MaxValue As Double
        Public Property Choices As String()
        Public Property Value As Object
        Public Property ValueType As Type

        Public ReadOnly Property ValueText As String
            Get
                If Value Is Nothing Then Return ""
                Return Value.ToString()
            End Get
        End Property
    End Class

End Namespace
