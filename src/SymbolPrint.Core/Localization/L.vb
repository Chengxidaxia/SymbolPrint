Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Reflection
Imports System.Text.Json

Namespace Localization

    ''' <summary>
    ''' 文案翻译层（纯逻辑，不依赖 WinForms，Core 与 App 共用）。
    '''
    ''' 设计取舍：**以中文原文作为 key**，不引入 "btn.save" 这类语义 key。
    ''' 这个项目已有约 950 处中文字面量，逐处改成语义 key 既慢又容易漏；
    ''' 用原文作 key 的好处是「漏翻自动回退中文」，界面永远不会出现空白或 key 名。
    '''
    ''' 用法：
    '''   L.T("保存")                  → 查字典，缺失回退中文
    '''   L.F("文件：{0}", name)        → 模板整体翻译后格式化
    ''' </summary>
    Public Module L

        Public Const ZhCN As String = "zh-CN"
        Public Const EnUS As String = "en-US"

        Private _lang As String = ZhCN
        Private ReadOnly _dict As New Dictionary(Of String, String)(StringComparer.Ordinal)

        ''' <summary>诊断输出通道；由宿主（App）赋值为日志函数，Core 自己不认识 UiLog。</summary>
        Public Property LogSink As Action(Of String) = Nothing

        Public ReadOnly Property Current As String
            Get
                Return _lang
            End Get
        End Property

        Public ReadOnly Property IsEnglish As Boolean
            Get
                Return String.Equals(_lang, EnUS, StringComparison.OrdinalIgnoreCase)
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
            Return _dict.ContainsKey(zh)
        End Function

        ''' <summary>取一份字典快照，便于"先记住英文字典、再切回中文收集 key"这类场景。</summary>
        Public Function Snapshot() As Dictionary(Of String, String)
            Return New Dictionary(Of String, String)(_dict, StringComparer.Ordinal)
        End Function

        ''' <summary>翻译一条文案；查不到就原样返回中文。</summary>
        Public Function T(zh As String) As String
            If String.IsNullOrEmpty(zh) Then Return zh
            If Not IsEnglish Then Return zh
            Dim en As String = Nothing
            If _dict.TryGetValue(zh, en) AndAlso Not String.IsNullOrEmpty(en) Then Return en
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

        ''' <summary>载入语言包。zh-CN 直接清空字典（原文即译文）。</summary>
        Public Sub Load(lang As String)
            _dict.Clear()
            _lang = If(String.Equals(lang, EnUS, StringComparison.OrdinalIgnoreCase), EnUS, ZhCN)
            If _lang = ZhCN Then Return

            Dim json = ReadResource("en-US.json")
            If String.IsNullOrEmpty(json) Then
                _lang = ZhCN
                Report("语言包 en-US.json 未找到，界面保持中文。")
                Return
            End If

            Try
                Using doc = JsonDocument.Parse(json)
                    Dim strings As JsonElement
                    If Not doc.RootElement.TryGetProperty("strings", strings) Then
                        _lang = ZhCN
                        Report("语言包缺少 strings 节点，界面保持中文。")
                        Return
                    End If
                    For Each p In strings.EnumerateObject()
                        If p.Value.ValueKind = JsonValueKind.String Then
                            Dim en = p.Value.GetString()
                            If Not String.IsNullOrEmpty(en) Then _dict(p.Name) = en
                        End If
                    Next
                End Using
            Catch ex As Exception
                _lang = ZhCN
                _dict.Clear()
                Report("语言包解析失败，界面保持中文：" & ex.Message)
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
