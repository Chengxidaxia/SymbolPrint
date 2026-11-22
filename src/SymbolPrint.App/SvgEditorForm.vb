Imports System
Imports System.Drawing
Imports System.IO
Imports System.Reflection
Imports System.Text.Json
Imports System.Windows.Forms
Imports Microsoft.Web.WebView2.Core
Imports Microsoft.Web.WebView2.WinForms

Namespace App

    ''' <summary>
    ''' SVG 图案编辑器宿主：WebView2 载入内嵌的 editor.html（编辑器本体是 HTML/JS，见 assets/editor）。
    ''' 图案渲染与编辑器共用同一个浏览器内核，所见即所得；导出的 SVG 回传给做章页存进章文件。
    ''' </summary>
    Friend NotInheritable Class SvgEditorForm
        Inherits Form

        Private ReadOnly _web As WebView2
        Private ReadOnly _initialSvg As String

        ''' <summary>点「完成」后得到的 SVG 文本；取消时为空。</summary>
        Friend Property ResultSvg As String = ""

        ''' <summary>页面加载完后要执行的脚本（仅用于自动化验证/演示，正常调用留空）。</summary>
        Friend Property AutoScript As String = ""

        Friend Sub New(initialSvg As String)
            _initialSvg = If(initialSvg, "")

            Text = "图案编辑器 — SymbolPrint"
            StartPosition = FormStartPosition.CenterParent
            MinimumSize = New Size(880, 600)
            Size = New Size(1120, 760)
            BackColor = Theme.Bg
            Font = Theme.FontBase
            KeyPreview = True
            Try
                Icon = Theme.AppIcon()
            Catch
            End Try

            _web = New WebView2() With {.Dock = DockStyle.Fill}
            Controls.Add(_web)

            AddHandler Load, AddressOf OnFormLoad
        End Sub

        Private Async Sub OnFormLoad(sender As Object, e As EventArgs)
            Try
                ' 先明确检测运行时：Win11 一般自带，Win10 22H2 需要单独装，缺了要给准信儿而不是一句 ex.Message
                Dim ver = CoreWebView2Environment.GetAvailableBrowserVersionString()
                If String.IsNullOrWhiteSpace(ver) Then
                    Throw New InvalidOperationException("未检测到 WebView2 运行时。")
                End If

                Await _web.EnsureCoreWebView2Async(Nothing)

                With _web.CoreWebView2.Settings
                    .AreDefaultContextMenusEnabled = False
                    .IsStatusBarEnabled = False
                    .AreDevToolsEnabled = True      ' 保留 F12，便于排查渲染问题
                    .IsZoomControlEnabled = False
                    .AreBrowserAcceleratorKeysEnabled = False
                End With

                AddHandler _web.CoreWebView2.WebMessageReceived, AddressOf OnWebMessage
                AddHandler _web.NavigationCompleted, AddressOf OnNavigated
                _web.NavigateToString(BuildHtml())
            Catch ex As Exception
                MessageBox.Show(
                    "图案编辑器需要 Microsoft Edge WebView2 运行时，本机没有检测到。" & Environment.NewLine & Environment.NewLine &
                    "Windows 11 通常自带；Windows 10（含 22H2）需要手动安装一次：" & Environment.NewLine &
                    "https://developer.microsoft.com/microsoft-edge/webview2/" & Environment.NewLine & Environment.NewLine &
                    "装好后重开本窗口即可，其它功能（盖章、验章）不受影响。" & Environment.NewLine & Environment.NewLine &
                    "详细信息：" & ex.Message,
                    "SymbolPrint", MessageBoxButtons.OK, MessageBoxIcon.Error)
                DialogResult = DialogResult.Cancel
                Close()
            End Try
        End Sub

        ''' <summary>把初始 SVG 注入 HTML 的占位符（用 JSON 序列化保证引号/换行安全）。</summary>
        Private Function BuildHtml() As String
            Dim html = LoadEditorHtml()
            Dim literal = JsonSerializer.Serialize(_initialSvg)
            Return html.Replace("__INITIAL_SVG__", literal)
        End Function

        Private Shared Function LoadEditorHtml() As String
            Dim asm = Assembly.GetExecutingAssembly()
            Using s = asm.GetManifestResourceStream("editor.html")
                If s Is Nothing Then Throw New InvalidOperationException("内嵌的 editor.html 丢失（打包问题）。")
                Using r As New StreamReader(s, System.Text.Encoding.UTF8)
                    Return r.ReadToEnd()
                End Using
            End Using
        End Function

        Private Async Sub OnNavigated(sender As Object, e As CoreWebView2NavigationCompletedEventArgs)
            If Not e.IsSuccess OrElse String.IsNullOrEmpty(AutoScript) Then Return
            Try
                Await _web.CoreWebView2.ExecuteScriptAsync(AutoScript)
            Catch ex As Exception
                UiLog.Write("编辑器自动脚本执行失败：" & ex.Message)
            End Try
        End Sub

        Private Sub OnWebMessage(sender As Object, e As CoreWebView2WebMessageReceivedEventArgs)
            Try
                Using doc = JsonDocument.Parse(e.WebMessageAsJson)
                    Dim root = doc.RootElement
                    Dim typeEl As JsonElement
                    Dim typeName = If(root.TryGetProperty("type", typeEl), typeEl.GetString(), "")

                    Select Case typeName
                        Case "ok"
                            Dim svgEl As JsonElement
                            ResultSvg = If(root.TryGetProperty("svg", svgEl), svgEl.GetString(), "")
                            DialogResult = DialogResult.OK
                            Close()
                        Case "cancel"
                            DialogResult = DialogResult.Cancel
                            Close()
                        Case "error"
                            Dim msgEl As JsonElement
                            Dim msg = If(root.TryGetProperty("message", msgEl), msgEl.GetString(), "未知错误")
                            MessageBox.Show(msg, "图案编辑器", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    End Select
                End Using
            Catch ex As Exception
                UiLog.Write("编辑器消息解析失败：" & ex.Message)
            End Try
        End Sub

        Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
            ' 右上角关闭等同取消，避免调用方拿到半成品
            If DialogResult = DialogResult.None Then DialogResult = DialogResult.Cancel
            MyBase.OnFormClosing(e)
        End Sub
    End Class

End Namespace
