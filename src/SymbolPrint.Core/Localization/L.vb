Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Reflection
Imports System.Text.Json

Namespace Localization

    ''' <summary>
    ''' 文案翻译层（纯逻辑，不依赖 WinForms，Core 与 App 共用）。
    '''
    ''' 设计取舍：**以简体中文原文作为 key**。
    ''' 这个项目已有约 950 处中文字面量，逐处改成语义 key 既慢又容易漏；
    ''' 用原文作 key 的好处是「漏翻自动回退简体中文」，界面永远不会出现空白或 key 名。
    '''
    ''' 支持的语言（标识符与语言包文件名一致）：
    '''   zh_CN  简体中文 —— 原文即译文，不加载语言包
    '''   zh_TW  繁體中文 —— 逐条替换，注意词汇差异（檔案 / 設定 / 預設…）
    '''   zh_AL  文言     —— 风格化改写，不追求逐字对应
    '''   en_US  English
    '''
    ''' 用法：
    '''   L.T("保存")                  → 查语言包，缺失回退简体
    '''   L.F("文件：{0}", name)        → 模板整体翻译后格式化
    ''' </summary>
    Public Module L

        Public Const ZhCN As String = "zh_CN"
        Public Const ZhTW As String = "zh_TW"
        Public Const ZhAL As String = "zh_AL"
        Public Const EnUS As String = "en_US"

        ''' <summary>可选语言（不含 System），顺序即设置页下拉顺序。</summary>
        Public ReadOnly AllLanguages As String() = {ZhCN, ZhTW, ZhAL, EnUS}

        Private _lang As String = ZhCN
        Private ReadOnly _dict As New Dictionary(Of String, String)(StringComparer.Ordinal)

        ''' <summary>诊断输出通道；由宿主（App）赋值为日志函数，Core 自己不认识 UiLog。</summary>
        Public Property LogSink As Action(Of String) = Nothing

        Public ReadOnly Property Current As String
            Get
                Return _lang
            End Get
        End Property

        ''' <summary>当前是否为英文（个别地方需要按语言调字号时用）。</summary>
        Public ReadOnly Property IsEnglish As Boolean
            Get
                Return String.Equals(_lang, EnUS, StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        ''' <summary>当前语言是否属于中文族（简体 / 繁体 / 文言）。</summary>
        Public ReadOnly Property IsChineseFamily As Boolean
            Get
                Return _lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        ''' <summary>已载入的译文条数。</summary>
        Public ReadOnly Property Count As Integer
            Get
                Return _dict.Count
            End Get
        End Property

        ''' <summary>这条文案当前语言包里有译文没有（与当前显示语言无关，用于统计覆盖率）。</summary>
        Public Function Has(zh As String) As Boolean
            If String.IsNullOrEmpty(zh) Then Return False
            If _lang = ZhCN Then Return True
            Return _dict.ContainsKey(zh)
        End Function

        ''' <summary>取一份字典快照，便于"先记住某语言的字典、再切回简体收集 key"这类场景。</summary>
        Public Function Snapshot() As Dictionary(Of String, String)
            Return New Dictionary(Of String, String)(_dict, StringComparer.Ordinal)
        End Function

        ''' <summary>翻译一条文案；查不到就原样返回简体中文。</summary>
        Public Function T(zh As String) As String
            If String.IsNullOrEmpty(zh) Then Return zh
            If _lang = ZhCN Then Return zh
            Dim v As String = Nothing
            If _dict.TryGetValue(zh, v) AndAlso Not String.IsNullOrEmpty(v) Then Return v
            Return zh
        End Function

        ''' <summary>翻译一条带占位符的模板再格式化，例：L.F("文件：{0}", name)。</summary>
        Public Function F(zhFormat As String, ParamArray args As Object()) As String
            Dim tpl = T(zhFormat)
            If args Is Nothing OrElse args.Length = 0 Then Return tpl
            Try
                Return String.Format(tpl, args)
            Catch
                Return tpl
            End Try
        End Function

        ''' <summary>规范化语言标识：接受 zh-CN / zh_cn / en 等写法，统一成 zh_CN / en_US。</summary>
        Public Function Normalize(lang As String) As String
            Dim s = If(lang, "").Trim().Replace("-"c, "_"c).ToLowerInvariant()
            Select Case s
                Case "zh_cn", "zh", "zh_hans", "chs", "schinese", "simplified"
                    Return ZhCN
                Case "zh_tw", "zh_hk", "zh_mo", "zh_hant", "cht", "tchinese", "traditional"
                    Return ZhTW
                Case "zh_al", "lzh", "wenyan", "classical", "cchinese"
                    Return ZhAL
                Case "en_us", "en", "en_gb", "english"
                    Return EnUS
                Case Else
                    Return ZhCN
            End Select
        End Function

        ''' <summary>语言标识对应的显示名（设置页下拉用）。</summary>
        Public Function DisplayName(lang As String) As String
            Select Case Normalize(lang)
                Case ZhTW : Return "zh_TW (繁體)"
                Case ZhAL : Return "zh_AL (文言)"
                Case EnUS : Return "en_US"
                Case Else : Return "zh_CN (简体)"
            End Select
        End Function

        ''' <summary>载入语言包。zh_CN 直接清空字典（原文即译文）。</summary>
        Public Sub Load(lang As String)
            _dict.Clear()
            _lang = Normalize(lang)
            If _lang = ZhCN Then Return

            Dim resName = _lang & ".json"
            Dim json = ReadResource(resName)
            If String.IsNullOrEmpty(json) Then
                _lang = ZhCN
                Report("语言包 " & resName & " 未找到，界面保持简体中文。")
                Return
            End If

            Try
                Using doc = JsonDocument.Parse(json)
                    Dim strings As JsonElement
                    If Not doc.RootElement.TryGetProperty("strings", strings) Then
                        _lang = ZhCN
                        Report("语言包缺少 strings 节点，界面保持简体中文。")
                        Return
                    End If
                    For Each p In strings.EnumerateObject()
                        If p.Value.ValueKind = JsonValueKind.String Then
                            Dim v = p.Value.GetString()
                            If Not String.IsNullOrEmpty(v) Then _dict(p.Name) = v
                        End If
                    Next
                End Using
            Catch ex As Exception
                _lang = ZhCN
                _dict.Clear()
                Report("语言包解析失败，界面保持简体中文：" & ex.Message)
            End Try
        End Sub

        Private Sub Report(msg As String)
            Dim sink = LogSink
            If sink IsNot Nothing Then
                Try
                    sink(msg)
                Catch
                End Try
            End If
        End Sub

        Private Function ReadResource(name As String) As String
            Try
                Dim asm = Assembly.GetExecutingAssembly()
                Using s = asm.GetManifestResourceStream(name)
                    If s Is Nothing Then Return ""
                    Using r As New StreamReader(s, Text.Encoding.UTF8)
                        Return r.ReadToEnd()
                    End Using
                End Using
            Catch
                Return ""
            End Try
        End Function

    End Module

End Namespace
