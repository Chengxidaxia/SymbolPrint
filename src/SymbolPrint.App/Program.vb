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
            ' 「界面 → 主题」：同样必须在建窗体之前定好；System 跟随 Windows
            Try
                Dim mode = SymbolPrint.Core.Model.AppTheme.System
                Try
                    mode = SymbolPrint.Core.Model.AppSettings.Load().General.Theme
                Catch
                End Try
                Dim dark = If(mode = SymbolPrint.Core.Model.AppTheme.Dark, True,
                              If(mode = SymbolPrint.Core.Model.AppTheme.Light, False, Theme.SystemPrefersDark()))
                Theme.ApplyTheme(dark)
            Catch ex As Exception
                UiLog.Write("主题应用失败，回退浅色：" & ex.Message)
            End Try

            Try
                Theme.ApplyFontSize(uiFontPt)
                Application.SetDefaultFont(New Drawing.Font("Microsoft YaHei UI", CSng(uiFontPt)))
            Catch
                ' 字体缺失时保持系统默认，不阻断启动
            End Try

            ' 「界面 → 语言」：同样必须在建窗体之前载入，否则控件的初始文案会是中文
            Try
                SymbolPrint.Core.Localization.L.LogSink = AddressOf UiLog.Write
                Dim lang = ResolveLanguage()
                ' --lang 只覆盖本次运行，不写回配置（方便中英文界面并排截图比对）
                For i = 0 To args.Length - 1
                    If String.Equals(args(i), "--lang", StringComparison.OrdinalIgnoreCase) AndAlso i + 1 < args.Length Then
                        ' 接受 zh_CN / zh_TW / zh_AL / en / en_US 等写法，统一走 L.Normalize
                        lang = SymbolPrint.Core.Localization.L.Normalize(args(i + 1))
                    End If
                Next
                SymbolPrint.Core.Localization.L.Load(lang)
            Catch ex As Exception
                UiLog.Write("语言载入失败，界面保持中文：" & ex.Message)
            End Try

            If Not EnsureMinOs() Then Return

            ' 未处理异常统一兜底，避免用户看到 .NET 的原始崩溃对话框
            AddHandler Application.ThreadException, Sub(sender, e)
                                                         UiLog.WriteCrash(e.Exception)
                                                         MessageBox.Show(SymbolPrint.Core.Localization.L.T("程序遇到未处理的错误：") & Environment.NewLine & Environment.NewLine &
                                                                         e.Exception.Message & Environment.NewLine & Environment.NewLine &
                                                                         SymbolPrint.Core.Localization.L.T("详细信息已写入日志：") & UiLog.LogPath,
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
                    SymbolPrint.Core.Localization.L.F("本程序面向 Windows 10 22H2（build 19045）及以上，当前系统 build {0}。", build) &
                    Environment.NewLine & Environment.NewLine &
                    SymbolPrint.Core.Localization.L.T("低于该版本时可能出现界面错位、WebView2 组件缺失等问题，这些不在支持范围内。") &
                    Environment.NewLine & Environment.NewLine & SymbolPrint.Core.Localization.L.T("仍要继续启动吗？"),
                    "SymbolPrint", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)
                Return r = DialogResult.Yes
            Catch
                Return True
            End Try
        End Function

        ''' <summary>
        ''' 按设置解析目标语言；System 跟随 Windows 显示语言。
        ''' 简体/繁体中文都归到中文族，避免繁体系统拿到英文界面。
        ''' </summary>
        Private Function ResolveLanguage() As String
            Dim mode = SymbolPrint.Core.Model.LanguageIds.Auto
            Try
                mode = SymbolPrint.Core.Model.AppSettings.Load().General.Language
            Catch
                ' 配置读不出来时按系统语言走
            End Try

            If Not String.IsNullOrWhiteSpace(mode) AndAlso
               Not String.Equals(mode, SymbolPrint.Core.Model.LanguageIds.Auto, StringComparison.OrdinalIgnoreCase) Then
                Return SymbolPrint.Core.Localization.L.Normalize(mode)
            End If

            Dim ui = System.Globalization.CultureInfo.CurrentUICulture
            Dim two = ui.TwoLetterISOLanguageName.ToLowerInvariant()
            If two = "zh" Then
                ' 繁体地区走繁体：台湾 / 香港 / 澳门
                Dim name = If(ui.Name, "").ToLowerInvariant()
                If name.Contains("tw") OrElse name.Contains("hk") OrElse name.Contains("mo") OrElse
                   name.Contains("hant") Then
                    Return SymbolPrint.Core.Localization.L.ZhTW
                End If
                Return SymbolPrint.Core.Localization.L.ZhCN
            End If
            Return SymbolPrint.Core.Localization.L.EnUS
        End Function

    End Module

End Namespace
