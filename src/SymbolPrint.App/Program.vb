Imports System
Imports System.Windows.Forms
Imports SymbolPrint.Core.Util

Namespace App

    Friend Module Program

        <STAThread>
        Friend Sub Main(args As String())
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)
            Application.EnableVisualStyles()
            Application.SetCompatibleTextRenderingDefault(False)

            ' 「界面 → 字号」：必须在任何窗体创建之前应用，否则已建控件仍持旧字体
            Dim uiFontPt As Integer = 9
            Try
                uiFontPt = SymbolPrint.Core.Model.AppSettings.Load().Ui.FontSize
            Catch
            End Try
            Try
                Theme.ApplyFontSize(uiFontPt)
                Application.SetDefaultFont(New Drawing.Font("Microsoft YaHei UI", CSng(uiFontPt)))
            Catch
                ' 字体缺失时保持系统默认，不阻断启动
            End Try

            If Not EnsureMinOs() Then Return

            ' 未处理异常统一兜底，避免用户看到 .NET 的原始崩溃对话框
            AddHandler Application.ThreadException, Sub(sender, e)
                                                         UiLog.WriteCrash(e.Exception)
                                                         MessageBox.Show("程序遇到未处理的错误：" & Environment.NewLine & Environment.NewLine &
                                                                         e.Exception.Message & Environment.NewLine & Environment.NewLine &
                                                                         "详细信息已写入日志：" & UiLog.LogPath,
                                                                         "SymbolPrint", MessageBoxButtons.OK, MessageBoxIcon.Error)
                                                     End Sub
            AddHandler AppDomain.CurrentDomain.UnhandledException, Sub(sender, e)
                                                                       Dim ex = TryCast(e.ExceptionObject, Exception)
                                                                       If ex IsNot Nothing Then UiLog.WriteCrash(ex)
                                                                   End Sub

            Application.Run(New MainForm(args))
        End Sub

        ''' <summary>
        ''' 支持范围：Windows 10 22H2（build 19045）及以上。
        ''' 用运行时检查而不是改目标框架来表达——把 TFM 写成 net9.0-windows10.0.19041.0
        ''' 会带进 24MB 的 WinRT 投影包，而本程序一个 WinRT API 都不用。
        ''' 低于 19045 不强退：让用户知情后自己决定（LTSC/精简版系统的 build 值不一定可靠）。
        ''' </summary>
        Private Function EnsureMinOs() As Boolean
            Try
                Dim build = Environment.OSVersion.Version.Build
                If build >= 19045 Then Return True
                Dim r = MessageBox.Show(
                    "本程序面向 Windows 10 22H2（build 19045）及以上，当前系统 build " & build & "。" &
                    Environment.NewLine & Environment.NewLine &
                    "低于该版本时可能出现界面错位、WebView2 组件缺失等问题，这些不在支持范围内。" &
                    Environment.NewLine & Environment.NewLine & "仍要继续启动吗？",
                    "SymbolPrint", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)
                Return r = DialogResult.Yes
            Catch
                Return True
            End Try
        End Function

    End Module

End Namespace
