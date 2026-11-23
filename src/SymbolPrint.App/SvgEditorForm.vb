Imports System
Imports System.Drawing
Imports System.IO
Imports System.Text.Json
Imports System.Windows.Forms
Imports Microsoft.Web.WebView2.Core
Imports Microsoft.Web.WebView2.WinForms

Namespace App

    ''' <summary>
    ''' 图案编辑器宿主：WebView2 载入 <程序目录>\editor\host.html，编辑器内核是 SVG-Edit 7.4.2（MIT）。
    ''' 选它是因为它是目前唯一能真正嵌进 WebView2 的成熟 SVG 编辑器：
    ''' 有节点/贝塞尔路径编辑、渐变、图层、导入导出，且纯前端、可离线。
    '''
    ''' 资源走虚拟主机映射而不是 NavigateToString —— SVG-Edit 是多文件（js/css/images/extensions），
    ''' about:blank 下相对路径全失效。映射到 https://symbolprint.editor/ 后 origin 稳定，
    ''' 以后要加 localStorage 之类也不会因为源变化而丢。
    ''' </summary>
    Friend NotInheritable Class SvgEditorForm
        Inherits Form

        Private Const VirtualHost As String = "symbolprint.editor"

        Private ReadOnly _web As WebView2
        Private ReadOnly _initialSvg As String

        ''' <summary>点「完成」后得到的 SVG 文本；取消时为空。</summary>
        Friend Property ResultSvg As String = ""

        ''' <summary>页面加载完后要执行的脚本（仅用于自动化验证/演示，正常调用留空）。</summary>
        Friend Property AutoScript As String = ""

        ''' <summary>
        ''' 非空时：页面稳定后把编辑器内容截成 PNG 存到该路径，然后自动关闭。
        ''' 走 WebView2 的 CapturePreviewAsync，不依赖屏幕可见性（窗口被遮挡/最小化都能抓到），
        ''' 仅用于自动化回归，正常调用留空。
        ''' </summary>
        Friend Property CapturePath As String = ""

        Friend Sub New(initialSvg As String)
            _initialSvg = If(initialSvg, "")

            Text = "图案编辑器 — SymbolPrint"
            StartPosition = FormStartPosition.CenterParent
            MinimumSize = New Size(960, 640)
            Size = New Size(1280, 820)
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

        ''' <summary>SVG-Edit 落盘目录（随程序一起发布）。</summary>
        Friend Shared Function EditorDir() As String
            Return Path.Combine(AppContext.BaseDirectory, "editor")
        End Function

        Private Async Sub OnFormLoad(sender As Object, e As EventArgs)
            Try
                Dim dir = EditorDir()
                If Not File.Exists(Path.Combine(dir, "host.html")) Then
                    Throw New FileNotFoundException(
                        "编辑器资源目录不完整，缺少 host.html：" & dir & Environment.NewLine &
                        "请重新解压完整发布包（editor 文件夹必须与 SymbolPrint.exe 同级）。")
                End If

                ' 先明确检测运行时：Win11 一般自带，Win10 22H2 需要单独装，缺了要给准信儿而不是一句 ex.Message
                Dim ver = CoreWebView2Environment.GetAvailableBrowserVersionString()
                If String.IsNullOrWhiteSpace(ver) Then
                    Throw New InvalidOperationException("未检测到 WebView2 运行时。")
                End If

                Await _web.EnsureCoreWebView2Async(Nothing)

                With _web.CoreWebView2
                    .SetVirtualHostNameToFolderMapping(VirtualHost, dir, CoreWebView2HostResourceAccessKind.DenyCors)

                    With .Settings
                        .AreDefaultContextMenusEnabled = False
                        .IsStatusBarEnabled = False
                        .AreDevToolsEnabled = True      ' 保留 F12，便于排查渲染问题
                        .IsZoomControlEnabled = False
                        .AreBrowserAcceleratorKeysEnabled = False
                        .IsWebMessageEnabled = True
                    End With

                    ' 初始图案与主题都必须在文档创建时就位，避免编辑器先画个空白再跳变
                    Dim literal = JsonSerializer.Serialize(_initialSvg)
                    Await .AddScriptToExecuteOnDocumentCreatedAsync("window.__SP_INITIAL_SVG__ = " & literal & ";")
                    If Theme.IsDark Then
                        Await .AddScriptToExecuteOnDocumentCreatedAsync("window.__SP_DARK__ = true;")
                    End If

                    AddHandler .WebMessageReceived, AddressOf OnWebMessage
                    AddHandler .NavigationCompleted, AddressOf OnNavigated

                    .Navigate("https://" & VirtualHost & "/host.html")
                End With
            Catch ex As Exception
                MessageBox.Show(
                    "图案编辑器启动失败。" & Environment.NewLine & Environment.NewLine &
                    ex.Message & Environment.NewLine & Environment.NewLine &
                    "若提示缺少 WebView2 运行时：Windows 11 通常自带；Windows 10（含 22H2）需要手动安装一次：" &
                    Environment.NewLine & "https://developer.microsoft.com/microsoft-edge/webview2/" & Environment.NewLine & Environment.NewLine &
                    "其它功能（盖章、验章）不受影响。",
                    "SymbolPrint", MessageBoxButtons.OK, MessageBoxIcon.Error)
                DialogResult = DialogResult.Cancel
                Close()
            End Try
        End Sub

        Private Async Sub OnNavigated(sender As Object, e As CoreWebView2NavigationCompletedEventArgs)
            If Not e.IsSuccess Then
                UiLog.Write("编辑器页面加载失败：" & e.WebErrorStatus.ToString())
                Return
            End If
            If String.IsNullOrEmpty(AutoScript) AndAlso String.IsNullOrEmpty(CapturePath) Then Return
            Try
                If Not String.IsNullOrEmpty(AutoScript) Then
                    Await _web.CoreWebView2.ExecuteScriptAsync(AutoScript)
                End If
                If Not String.IsNullOrEmpty(CapturePath) Then
                    ' 等编辑器就绪（iife 包解析 + 首帧），再抓图
                    Await Task.Delay(2600)
                    Using ms As New MemoryStream()
                        Await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms)
                        IO.File.WriteAllBytes(CapturePath, ms.ToArray())
                    End Using
                    UiLog.Write("编辑器预览已保存：" & CapturePath)
                    DialogResult = DialogResult.Cancel
                    Close()
                End If
            Catch ex As Exception
                UiLog.Write("编辑器自动脚本/截图失败：" & ex.Message)
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
                        Case "loaded"
                            ' 只是就绪信号，界面无需变化
                        Case "debug"
                            Dim msgEl As JsonElement
                            UiLog.Write("编辑器调试输出：" & If(root.TryGetProperty("message", msgEl), msgEl.GetString(), ""))
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
