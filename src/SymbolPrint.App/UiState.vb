Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text.Json

Namespace App

    ''' <summary>
    ''' 界面"轻状态"：窗口位置、上次打开的文件列表。
    ''' 刻意不塞进 AppSettings —— 那个文件是给用户看的配置（会手改、会分享），
    ''' 而这些是程序自己的运行痕迹，坏了也不该影响配置。
    ''' </summary>
    Friend NotInheritable Class AppUiState

        Public Property HasBounds As Boolean = False
        Public Property X As Integer = 0
        Public Property Y As Integer = 0
        Public Property Width As Integer = 0
        Public Property Height As Integer = 0

        ''' <summary>上次参与盖章的文件（只在「启动时打开上次文件」开启时写入）。</summary>
        Public Property LastFiles As New List(Of String)()

        Private Shared _cached As AppUiState = Nothing

        Friend Shared Function StatePath() As String
            Dim dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SymbolPrint")
            Return Path.Combine(dir, "ui-state.json")
        End Function

        Friend Shared Function Load() As AppUiState
            If _cached IsNot Nothing Then Return _cached
            Try
                Dim p = StatePath()
                If File.Exists(p) Then
                    Dim json = File.ReadAllText(p)
                    Dim st = JsonSerializer.Deserialize(Of AppUiState)(json)
                    If st IsNot Nothing Then
                        _cached = st
                        Return st
                    End If
                End If
            Catch
                ' 状态文件损坏/无权限：直接用默认值，不能因此启动失败
            End Try
            _cached = New AppUiState()
            Return _cached
        End Function

        Friend Sub Save()
            Try
                Dim p = StatePath()
                Dim dir = Path.GetDirectoryName(p)
                If Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)
                Dim json = JsonSerializer.Serialize(Me, New JsonSerializerOptions With {.WriteIndented = True})
                File.WriteAllText(p, json)
            Catch
                ' 存不下就算了，下次用默认值
            End Try
        End Sub

        ''' <summary>窗口位置是否还落在某块屏幕上（拔掉副屏后不能把窗口恢复到看不见的地方）。</summary>
        Friend Function BoundsVisible() As Boolean
            If Not HasBounds OrElse Width < 400 OrElse Height < 300 Then Return False
            Dim r As New Drawing.Rectangle(X, Y, Width, Height)
            For Each s In System.Windows.Forms.Screen.AllScreens
                If s.WorkingArea.IntersectsWith(r) Then Return True
            Next
            Return False
        End Function

    End Class

End Namespace
