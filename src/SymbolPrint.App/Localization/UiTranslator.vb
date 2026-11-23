Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Runtime.CompilerServices
Imports System.Windows.Forms
Imports SymbolPrint.Core.Localization

Namespace App

    ''' <summary>
    ''' 把 <see cref="L"/> 的翻译应用到 WinForms 控件树上。
    '''
    ''' 为什么不逐处改字符串：界面文案有近千条，逐条包 L.T 容易漏、也难回退。
    ''' 这里在窗体建好之后统一扫一遍，只有「整条文本精确命中字典」才替换 ——
    ''' 文件名、用户输入、日志内容不会被误翻。
    '''
    ''' 自绘控件（导航项、结果条）拿不到 Control.Text，它们在自己的 OnPaint 里直接调 L.T。
    ''' </summary>
    Friend Module UiTranslator

        ''' <summary>控件 → 原始（中文）文本。切换语言时用它反查，避免把英文再当 key 查一次。</summary>
        Private ReadOnly _origin As New ConditionalWeakTable(Of Control, StrongBox(Of String))
        ''' <summary>输入框占位文字 → 原文（与 Text 是两个字段，必须分开记）。</summary>
        Private ReadOnly _phOrigin As New ConditionalWeakTable(Of Control, StrongBox(Of String))
        ''' <summary>列表列头 → 原文（ColumnHeader 不是 Control，单独一张表）。</summary>
        Private ReadOnly _colOrigin As New ConditionalWeakTable(Of ColumnHeader, StrongBox(Of String))
        ''' <summary>表格列头 → 原文（DataGridView 的列头是 HeaderText）。</summary>
        Private ReadOnly _gridColOrigin As New ConditionalWeakTable(Of DataGridViewColumn, StrongBox(Of String))
        ''' <summary>下拉框 → 原始选项数组，同理。</summary>
        Private ReadOnly _comboOrigin As New Dictionary(Of ComboBox, String())
        ''' <summary>列表框 → 原始选项数组（设置页左侧的分类就是 ListBox）。</summary>
        Private ReadOnly _listOrigin As New Dictionary(Of ListBox, String())

        ''' <summary>
        ''' 重入守卫。替换 ListBox/ComboBox 的 Items 会触发 SelectedIndexChanged，
        ''' 而设置页在那个事件里会 Rebuild 并反过来再调 ApplyLanguage —— 不加这个就直接栈溢出。
        ''' </summary>
        Private _applying As Boolean

        ''' <summary>遍历控件树，把 Text / 下拉选项换成当前语言。可重复调用（幂等）。</summary>
        Friend Sub ApplyLanguage(root As Control)
            If root Is Nothing OrElse _applying Then Return
            _applying = True
            Try
                Visit(root)
            Finally
                _applying = False
            End Try
        End Sub

        Private Sub Visit(ctl As Control)
            TranslateControl(ctl)

            Dim ts = TryCast(ctl, ToolStrip)
            If ts IsNot Nothing Then
                For Each it As ToolStripItem In ts.Items
                    TranslateStripItem(it)
                Next
            End If

            If ctl.Controls.Count = 0 Then Return
            For Each ch As Control In ctl.Controls
                Visit(ch)
            Next
        End Sub

        Private Sub TranslateStripItem(it As ToolStripItem)
            If it Is Nothing Then Return
            If Not String.IsNullOrEmpty(it.Text) Then it.Text = L.T(it.Text)
            Dim dd = TryCast(it, ToolStripDropDownItem)
            If dd IsNot Nothing Then
                For Each subItem As ToolStripItem In dd.DropDownItems
                    TranslateStripItem(subItem)
                Next
            End If
        End Sub

        Private Sub TranslateControl(ctl As Control)
            If ctl Is Nothing Then Return

            ' 列表列头：表格的"文件 / 大小 / 状态"就在这里，不属于 Control.Text
            Dim lv = TryCast(ctl, ListView)
            If lv IsNot Nothing Then
                For Each col As ColumnHeader In lv.Columns
                    Dim cbx As StrongBox(Of String) = Nothing
                    Dim orig As String
                    If _colOrigin.TryGetValue(col, cbx) Then
                        orig = cbx.Value
                    Else
                        orig = col.Text
                        If String.IsNullOrEmpty(orig) Then Continue For
                        _colOrigin.Add(col, New StrongBox(Of String)(orig))
                    End If
                    col.Text = L.T(orig)
                Next
            End If

            ' 表格列头（本项目的文件列表是 DataGridView，列头在 HeaderText 上）
            Dim dgv = TryCast(ctl, DataGridView)
            If dgv IsNot Nothing Then
                For Each col As DataGridViewColumn In dgv.Columns
                    Dim gbox As StrongBox(Of String) = Nothing
                    Dim orig As String
                    If _gridColOrigin.TryGetValue(col, gbox) Then
                        orig = gbox.Value
                    Else
                        orig = col.HeaderText
                        If String.IsNullOrEmpty(orig) Then Continue For
                        _gridColOrigin.Add(col, New StrongBox(Of String)(orig))
                    End If
                    col.HeaderText = L.T(orig)
                Next
            End If

            ' 输入框占位文字（"未加密则留空"这类提示）
            Dim tb = TryCast(ctl, TextBox)
            If tb IsNot Nothing AndAlso Not String.IsNullOrEmpty(tb.PlaceholderText) Then
                Dim pb As StrongBox(Of String) = Nothing
                Dim phOrig As String
                If _phOrigin.TryGetValue(tb, pb) Then
                    phOrig = pb.Value
                Else
                    phOrig = tb.PlaceholderText
                    _phOrigin.Add(tb, New StrongBox(Of String)(phOrig))
                End If
                tb.PlaceholderText = L.T(phOrig)
            End If

            ' 说明：ListBox / ComboBox 的 Items **不在这里翻译**。
            ' 它们的条目同时充当业务键（设置页用分组中文名去匹配元数据、下拉框用选项文本回写枚举），
            ' 一旦被替换成英文，保存/重建就会匹配不到。这类控件改为自绘时取 L.T（见 SettingsPage）。

            ' 下拉框：Items 保持中文，只把绘制改成自绘 —— 显示层翻译，业务键不动
            Dim cb = TryCast(ctl, ComboBox)
            If cb IsNot Nothing AndAlso cb.DrawMode = DrawMode.Normal Then
                cb.DrawMode = DrawMode.OwnerDrawFixed
                AddHandler cb.DrawItem, AddressOf DrawComboItem
            End If

            If Not AcceptsText(ctl) Then Return

            Dim box As StrongBox(Of String) = Nothing
            Dim original As String
            If _origin.TryGetValue(ctl, box) Then
                original = box.Value
            Else
                original = ctl.Text
                If String.IsNullOrEmpty(original) Then Return
                _origin.Add(ctl, New StrongBox(Of String)(original))
            End If

            Dim translated = L.T(original)
            If Not String.Equals(ctl.Text, translated, StringComparison.Ordinal) Then ctl.Text = translated
        End Sub

        ''' <summary>下拉框自绘：只在显示时翻译，SelectedIndex / Text 仍是中文，回写枚举不受影响。</summary>
        Private Sub DrawComboItem(sender As Object, e As DrawItemEventArgs)
            If e.Index < 0 Then Return
            Dim cb = TryCast(sender, ComboBox)
            If cb Is Nothing Then Return

            e.DrawBackground()
            Dim raw = cb.GetItemText(cb.Items(e.Index))
            Dim shown = L.T(raw)
            Dim flags = TextFormatFlags.Left Or TextFormatFlags.VerticalCenter Or TextFormatFlags.EndEllipsis
            Dim r = New Rectangle(e.Bounds.X + 2, e.Bounds.Y, Math.Max(1, e.Bounds.Width - 4), e.Bounds.Height)
            TextRenderer.DrawText(e.Graphics, shown, cb.Font, r, cb.ForeColor, flags)
            If (e.State And DrawItemState.Focus) <> 0 Then e.DrawFocusRectangle()
        End Sub

        ''' <summary>哪些控件的 Text 是"界面文案"而不是"用户数据"。</summary>
        Private Function AcceptsText(ctl As Control) As Boolean
            Return TypeOf ctl Is Label OrElse
                   TypeOf ctl Is Button OrElse
                   TypeOf ctl Is CheckBox OrElse
                   TypeOf ctl Is RadioButton OrElse
                   TypeOf ctl Is GroupBox OrElse
                   TypeOf ctl Is TabPage OrElse
                   TypeOf ctl Is LinkLabel OrElse
                   TypeOf ctl Is Form
        End Function

        ''' <summary>
        ''' 收集界面上出现的全部文案（--i18nreport 用）。
        ''' 注意：必须在中文界面下收集才是干净的 key 集合。
        ''' </summary>
        Friend Sub CollectTexts(root As Control, into As HashSet(Of String))
            If root Is Nothing Then Return

            Dim cb = TryCast(root, ComboBox)
            If cb IsNot Nothing Then
                For Each o In cb.Items
                    Dim s = If(o Is Nothing, "", o.ToString())
                    If s.Trim() <> "" Then into.Add(s)
                Next
            ElseIf AcceptsText(root) AndAlso Not String.IsNullOrEmpty(root.Text) Then
                into.Add(root.Text)
            End If

            For Each ch As Control In root.Controls
                CollectTexts(ch, into)
            Next
        End Sub

    End Module

End Namespace
