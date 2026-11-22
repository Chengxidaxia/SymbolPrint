Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Text
Imports System.Windows.Forms

Namespace App

    ''' <summary>
    ''' 全局 UI 状态（给「拖动缩放」用）。
    '''
    ''' 为什么需要它：拖动窗口边缘时 Resize 每帧触发几十次，而 SplitContainer 只要改一次
    ''' SplitterDistance 就会把它两侧整棵控件树重排一遍（实测单次 40ms+，连带 DataGridView 更贵）。
    ''' 所以拖动期间冻结这些「按比例重算」的动作，松手后统一做一次，既不卡也不会留下裁断。
    ''' </summary>
    Friend Module UiState
        Friend IsUserResizing As Boolean

        ''' <summary>开始拖动缩放：页面可以借这个时机关掉 AutoScroll 等重型计算。</summary>
        Friend Event ResizeBegin As EventHandler

        ''' <summary>拖动结束：页面把关掉的东西恢复，并补一次完整重排。</summary>
        Friend Event ResizeEnd As EventHandler

        Friend Sub Begin()
            IsUserResizing = True
            RaiseEvent ResizeBegin(Nothing, EventArgs.Empty)
        End Sub

        Friend Sub Finish()
            IsUserResizing = False
            RaiseEvent ResizeEnd(Nothing, EventArgs.Empty)
        End Sub
    End Module

    ''' <summary>
    ''' 布局自检：把每个控件里的文字用 TextRenderer 实测一遍，和它「真正能用到的像素」比，
    ''' 再把兄弟控件的矩形两两求交。文字截断、控件重叠必须能被数出来，而不是靠看截图猜。
    ''' 只在 --audit 模式下调用，正常运行零开销。
    ''' </summary>
    Friend Module UiAudit

        ' 文字宽度允许 1px 舍入误差；控件位置允许 8px（DPI 缩放下 WinForms 的 Bounds 会有几像素取整差，
        ' 真正的裁切都是整行/整块级别的，不会只差几像素）
        Private Const Tolerance As Integer = 1
        Private Const BoundsTolerance As Integer = 8

        Friend Function Run(root As Control) As List(Of String)
            Dim issues As New List(Of String)()
            Walk(root, issues)
            Return issues
        End Function

        ''' <summary>诊断用：打印所有可滚动容器的真实状态，回答「滚动条为什么出现」。</summary>
        Friend Function Probe(root As Control) As List(Of String)
            Dim out As New List(Of String)()
            ProbeWalk(root, out)
            Return out
        End Function

        Private Sub ProbeWalk(c As Control, out As List(Of String))
            Dim sc = TryCast(c, ScrollableControl)
            If sc IsNot Nothing AndAlso sc.AutoScroll Then
                Dim maxR = 0, maxB = 0
                For Each ch As Control In c.Controls
                    If Not ch.Visible Then Continue For
                    If ch.Right > maxR Then maxR = ch.Right
                    If ch.Bottom > maxB Then maxB = ch.Bottom
                Next
                out.Add($"{PathOf(c)} 可视 {c.ClientSize.Width}x{c.ClientSize.Height} " &
                        $"Padding {c.Padding.Left},{c.Padding.Top},{c.Padding.Right},{c.Padding.Bottom} " &
                        $"Display {sc.DisplayRectangle.Width}x{sc.DisplayRectangle.Height} " &
                        $"子最大右/下 {maxR}/{maxB} 子数 {c.Controls.Count} " &
                        $"HBar={sc.HorizontalScroll.Visible} VBar={sc.VerticalScroll.Visible}")
            End If
            For Each ch As Control In c.Controls
                ProbeWalk(ch, out)
            Next
        End Sub

        Private Sub Walk(c As Control, issues As List(Of String))
            For Each child As Control In c.Controls
                If Not child.Visible Then Continue For
                CheckText(child, issues)
                CheckEditorText(child, issues)
                CheckBounds(child, issues)
                If child.Controls.Count > 0 Then Walk(child, issues)
            Next
            CheckOverlap(c, issues)
            CheckScrollOverflow(c, issues)
        End Sub

        ''' <summary>
        ''' 输入类控件的文字同样会「截断」，而且是最常见的一种：下拉框选项太长、路径框太窄。
        ''' 这类控件以前完全没被量过，所以界面上看着被切了、自检却报 0 项。
        ''' </summary>
        Private Sub CheckEditorText(c As Control, issues As List(Of String))
            Dim availW As Integer
            Dim text As String

            If TypeOf c Is ComboBox Then
                Dim cb = DirectCast(c, ComboBox)
                text = If(cb.DropDownStyle = ComboBoxStyle.DropDownList,
                          If(cb.SelectedItem Is Nothing, "", cb.GetItemText(cb.SelectedItem)),
                          cb.Text)
                ' 右侧下拉箭头占位；DropDownList 还要再留一点内边距
                availW = c.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6
            ElseIf TypeOf c Is NumericUpDown Then
                text = DirectCast(c, NumericUpDown).Value.ToString()
                ' 右侧微调按钮
                availW = c.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6
            ElseIf TypeOf c Is TextBox Then
                Dim tb = DirectCast(c, TextBox)
                If tb.Multiline Then Return
                text = tb.Text
                availW = c.ClientSize.Width - 5
            Else
                Return
            End If

            If String.IsNullOrEmpty(text) OrElse availW <= 0 Then Return
            Dim needW = TextRenderer.MeasureText(text, c.Font, New Size(Integer.MaxValue, Integer.MaxValue),
                                                 TextFormatFlags.NoPadding Or TextFormatFlags.SingleLine).Width
            If needW > availW + Tolerance * 2 Then
                issues.Add($"[输入框文字被切] {PathOf(c)} 需宽 {needW}px，可用 {availW}px：「{Shorten(text, 30)}」")
            End If
        End Sub

        ''' <summary>
        ''' 可滚动容器里的子控件不会「越界」，但会撑出横向滚动条——用户看到的就是「东西被切了、还得左右拖」。
        ''' 这里量的是「内容比可视区宽/高多少」，是之前完全没有覆盖的一类问题。
        ''' </summary>
        Private Sub CheckScrollOverflow(p As Control, issues As List(Of String))
            Dim sc = TryCast(p, ScrollableControl)
            If sc Is Nothing OrElse Not sc.AutoScroll Then Return
            Dim vis = p.ClientSize
            If vis.Width <= 0 OrElse vis.Height <= 0 Then Return

            Dim maxRight = 0
            Dim maxBottom = 0
            Dim widest As Control = Nothing
            For Each ch As Control In p.Controls
                If Not ch.Visible Then Continue For
                If ch.Right > maxRight Then
                    maxRight = ch.Right
                    widest = ch
                End If
                If ch.Bottom > maxBottom Then maxBottom = ch.Bottom
            Next

            ' 横向溢出：子控件比可视区宽 → 一定会出现横向滚动条
            If maxRight > vis.Width + BoundsTolerance Then
                Dim nm = If(widest Is Nothing, "?", PathOf(widest))
                issues.Add($"[横向溢出] {PathOf(p)} 内容宽 {maxRight} > 可视 {vis.Width}（多 {maxRight - vis.Width}px），" &
                           $"会出现横向滚动条，最宽的是 {nm}")
            End If
        End Sub

        ' ==================== 文字 ====================

        Private Function IsTextControl(c As Control) As Boolean
            Return TypeOf c Is Label OrElse TypeOf c Is Button OrElse
                   TypeOf c Is CheckBox OrElse TypeOf c Is RadioButton
        End Function

        Private Sub CheckText(c As Control, issues As List(Of String))
            If Not IsTextControl(c) Then Return
            Dim text = c.Text
            If String.IsNullOrEmpty(text) Then Return

            Dim f = c.Font
            Dim availW = c.ClientSize.Width - 4           ' 2px 边框/内边距余量
            Dim availH = c.ClientSize.Height - 2
            If availW <= 0 OrElse availH <= 0 Then
                issues.Add($"[零尺寸] {PathOf(c)} 只有 {c.ClientSize.Width}x{c.ClientSize.Height}")
                Return
            End If

            ' 单行固有宽度（中文按真实字形量，不按字符数估）
            Dim needW = TextRenderer.MeasureText(text, f, New Size(Integer.MaxValue, Integer.MaxValue),
                                                 TextFormatFlags.NoPadding Or TextFormatFlags.SingleLine).Width
            Dim singleH = TextRenderer.MeasureText(text, f, New Size(Integer.MaxValue, Integer.MaxValue),
                                                   TextFormatFlags.NoPadding Or TextFormatFlags.SingleLine).Height

            ' 复选框/单选的圆点占位
            Dim glyph As Integer = If(TypeOf c Is CheckBox OrElse TypeOf c Is RadioButton, 18, 0)

            ' 宽度：一行放得下就直接通过。行高由字体决定（DPI 变化时字体高度也会变），
            ' 所以只在「明显不够」时才报——±6px 属于字体 ascent/descent 的排布余量。
            If needW + glyph <= availW + Tolerance AndAlso singleH <= availH + 6 Then Return

            ' 一行放不下，看看能不能换行放下（多行 Label 是允许的）
            If TypeOf c Is Label Then
                ' 换行宽度按控件真实宽度算（Label 自己就是这么换的），高度按整块高度算：
                ' 真被切掉会是整行级别的差（≈一行字高），不会只差 2px。
                Dim wrapW = Math.Max(40, c.ClientSize.Width)
                Dim wrapped = TextRenderer.MeasureText(text, f, New Size(wrapW, Integer.MaxValue),
                                                       TextFormatFlags.NoPadding Or TextFormatFlags.WordBreak Or TextFormatFlags.TextBoxControl)
                If wrapped.Height <= c.ClientSize.Height + 2 Then Return
                issues.Add($"[换行后仍截断] {PathOf(c)} 文字需 {needW}x{wrapped.Height}px，" &
                           $"可用 {availW}x{availH}px：「{Shorten(text)}」")
                Return
            End If

            issues.Add($"[文字超出] {PathOf(c)} 需宽 {needW + glyph}px，可用 {availW}px：「{Shorten(text)}」")
        End Sub

        ' ==================== 边界 ====================

        Private Sub CheckBounds(c As Control, issues As List(Of String))
            Dim p = c.Parent
            If p Is Nothing Then Return
            Dim sc = TryCast(p, ScrollableControl)
            If sc IsNot Nothing AndAlso sc.AutoScroll Then Return

            Dim b = c.Bounds
            Dim cw = p.ClientSize.Width
            Dim ch = p.ClientSize.Height
            If b.Right > cw + BoundsTolerance Then
                issues.Add($"[右侧越界] {PathOf(c)} 右边缘 {b.Right} > 容器宽 {cw}")
            End If
            If b.Bottom > ch + BoundsTolerance Then
                issues.Add($"[底部越界] {PathOf(c)} 底边 {b.Bottom} > 容器高 {ch}")
            End If
        End Sub

        ' ==================== 重叠 ====================

        Private Function IsLeaf(c As Control) As Boolean
            Return TypeOf c Is Label OrElse TypeOf c Is Button OrElse TypeOf c Is CheckBox OrElse
                   TypeOf c Is RadioButton OrElse TypeOf c Is TextBox
        End Function

        Private Sub CheckOverlap(p As Control, issues As List(Of String))
            Dim items = p.Controls.Cast(Of Control)().Where(Function(x) x.Visible AndAlso IsLeaf(x)).ToList()
            For i = 0 To items.Count - 1
                For j = i + 1 To items.Count - 1
                    Dim a = items(i)
                    Dim b = items(j)
                    Dim r = Rectangle.Intersect(a.Bounds, b.Bounds)
                    If r.Width > 4 AndAlso r.Height > 4 Then
                        issues.Add($"[控件重叠] {PathOf(a)} 与 {PathOf(b)} 交叠 {r.Width}x{r.Height}px")
                    End If
                Next
            Next
        End Sub

        ' ==================== 输出 ====================

        Private Function PathOf(c As Control) As String
            Dim parts As New List(Of String)()
            Dim cur As Control = c
            Dim depth As Integer = 0
            While cur IsNot Nothing AndAlso depth < 6
                Dim name = If(String.IsNullOrEmpty(cur.Name), cur.GetType().Name, cur.Name)
                Dim label = If(TypeOf cur Is Label OrElse TypeOf cur Is Button, name & "「" & Shorten(cur.Text, 12) & "」", name)
                parts.Insert(0, label)
                cur = cur.Parent
                depth += 1
            End While
            Return String.Join(" / ", parts)
        End Function

        Private Function Shorten(s As String, Optional n As Integer = 24) As String
            If s Is Nothing Then Return ""
            Dim one = s.Replace(vbCrLf, " ").Replace(vbLf, " ")
            If one.Length <= n Then Return one
            Return one.Substring(0, n) & "…"
        End Function

    End Module

End Namespace
