Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports SymbolPrint.Core.Model

Namespace SymbolPrint.Cli

    ''' <summary>
    ''' `spcli settings` —— 把"高级选项"里的全部参数按分组打出来。
    ''' 这份输出就是未来 WinForms 高级选项面板的字段清单：面板靠 SettingAttribute 元数据自动生成，
    ''' 加参数不用改界面代码。
    ''' </summary>
    Public Module SettingsCommand

        Public Function Run(args As String()) As Integer
            Dim sub_ As String = If(args.Length > 1, args(1).ToLowerInvariant(), "show")
            Dim s = AppSettings.Load()

            Select Case sub_
                Case "show"
                    Show(s, args.Any(Function(a) a = "--all" OrElse a = "-a"))
                    Return 0
                Case "init"
                    Dim path = AppSettings.DefaultPath()
                    If IO.File.Exists(path) Then
                        Console.WriteLine($"已存在，未覆盖：{path}")
                    Else
                        s.Save(path)
                        Console.WriteLine($"已写入默认配置：{path}")
                    End If
                    Return 0
                Case "path"
                    Console.WriteLine(AppSettings.DefaultPath())
                    Return 0
                Case "json"
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(s,
                        New System.Text.Json.JsonSerializerOptions() With {.WriteIndented = True}))
                    Return 0
                Case Else
                    Console.WriteLine("用法: spcli settings [show|--all|init|path|json]")
                    Return 2
            End Select
        End Function

        Public Sub Show(s As AppSettings, showAdvanced As Boolean)
            Dim all = s.Describe()
            Dim groups = all.Select(Function(e) e.Group).Distinct().ToList()
            Dim shown As Integer = 0

            Console.WriteLine($"配置文件：{AppSettings.DefaultPath()}")
            Console.WriteLine($"共 {all.Count} 项参数（其中高级 {all.Where(Function(e) e.Advanced).Count()} 项）")
            Console.WriteLine()

            For Each g In groups
                Dim items = all.Where(Function(e) e.Group = g AndAlso (showAdvanced OrElse Not e.Advanced)).ToList()
                If items.Count = 0 Then Continue For
                Console.WriteLine($"[{g}]")
                For Each e In items
                    Dim flag As String = If(e.Advanced, "  (高级)", "")
                    Dim unit As String = If(String.IsNullOrEmpty(e.Unit), "", " " & e.Unit)
                    Dim range As String = ""
                    If Not Double.IsNaN(e.MinValue) AndAlso Not Double.IsNaN(e.MaxValue) Then
                        range = $"  [{e.MinValue:0.##}..{e.MaxValue:0.##}]"
                    ElseIf e.Choices IsNot Nothing AndAlso e.Choices.Length > 0 Then
                        range = "  {" & String.Join(" | ", e.Choices) & "}"
                    End If
                    Console.WriteLine($"  {e.Label,-18} = {e.ValueText}{unit}{range}{flag}")
                    If Not String.IsNullOrEmpty(e.Help) Then Console.WriteLine($"      └ {e.Help}")
                    shown += 1
                Next
                Console.WriteLine()
            Next
            Console.WriteLine($"已显示 {shown} 项。" & If(showAdvanced, "", " 加 --all 显示高级项。"))
        End Sub

    End Module

End Namespace
