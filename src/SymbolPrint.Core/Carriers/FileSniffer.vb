Imports System
Imports System.IO
Imports System.Text

Namespace Carriers

    ''' <summary>文件真实容器类型（按文件头判定）。</summary>
    Public Enum ContainerKind
        Unknown = 0
        Png
        Jpeg
        Gif
        Bmp
        Tiff
        Webp
        Ico
        Pdf
        Psd
        Zip
        SevenZip
        Rtf
        StampBin
        PlainText
    End Enum

    ''' <summary>
    ''' 按文件头（魔数）判断真实容器类型，扩展名只作辅助。
    ''' 为什么必须看内容：用户经常改后缀（.docx 改 .zip、图片改 .bin），
    ''' 只看扩展名会挑错载体——比如把 zip 当尾部追加来写，就破坏归档结构了。
    ''' </summary>
    Public Module FileSniffer

        Private Const HeadLen As Integer = 64

        ''' <summary>读文件头判定类型；文件不存在或读不了返回 Unknown。</summary>
        Public Function KindOf(filePath As String) As ContainerKind
            Try
                If Not File.Exists(filePath) Then Return ContainerKind.Unknown
                Dim head = CarrierExt.ReadRange(filePath, 0, Math.Min(HeadLen, CInt(Math.Min(Int64.MaxValue, New FileInfo(filePath).Length))))
                Return KindOf(head, filePath)
            Catch
                Return ContainerKind.Unknown
            End Try
        End Function

        ''' <summary>用已有的头部字节判定（便于单测与预览）。</summary>
        Public Function KindOf(head As Byte(), Optional filePath As String = Nothing) As ContainerKind
            If head Is Nothing OrElse head.Length < 4 Then Return ContainerKind.Unknown

            ' 章文件优先：它自己是 .bin，但也有明确魔数
            If Not String.IsNullOrEmpty(filePath) AndAlso Model.StampFile.HasMagic(filePath) Then Return ContainerKind.StampBin

            If Starts(head, &H89, &H50, &H4E, &H47, &HD, &HA, &H1A, &HA) Then Return ContainerKind.Png
            If Starts(head, &HFF, &HD8, &HFF) Then Return ContainerKind.Jpeg
            If Starts(head, &H47, &H49, &H46, &H38) Then Return ContainerKind.Gif
            If Starts(head, &H42, &H4D) Then Return ContainerKind.Bmp
            If Starts(head, &H49, &H49, &H2A, &H0) OrElse Starts(head, &H4D, &H4D, &H0, &H2A) Then Return ContainerKind.Tiff
            If Starts(head, &H52, &H49, &H46, &H46) AndAlso head.Length >= 12 AndAlso
               StartsAt(head, 8, &H57, &H45, &H42, &H50) Then Return ContainerKind.Webp
            If Starts(head, &H0, &H0, &H1, &H0) Then Return ContainerKind.Ico
            If Starts(head, &H25, &H50, &H44, &H46) Then Return ContainerKind.Pdf
            If Starts(head, &H38, &H42, &H50, &H53) Then Return ContainerKind.Psd
            If Starts(head, &H37, &H7A, &HBC, &HAF, &H27, &H1C) Then Return ContainerKind.SevenZip
            ' ZIP：本地文件头 / 空归档 / 分卷
            If Starts(head, &H50, &H4B, &H3, &H4) OrElse Starts(head, &H50, &H4B, &H5, &H6) OrElse Starts(head, &H50, &H4B, &H7, &H8) Then Return ContainerKind.Zip
            If Starts(head, &H7B, &H5C, &H72, &H74, &H66) Then Return ContainerKind.Rtf

            If LooksLikeText(head) Then Return ContainerKind.PlainText
            Return ContainerKind.Unknown
        End Function

        ''' <summary>给人看的类型名（日志/报错用）。</summary>
        Public Function KindName(kind As ContainerKind) As String
            Select Case kind
                Case ContainerKind.Png : Return "PNG 图片"
                Case ContainerKind.Jpeg : Return "JPEG 图片"
                Case ContainerKind.Gif : Return "GIF 图片"
                Case ContainerKind.Bmp : Return "BMP 图片"
                Case ContainerKind.Tiff : Return "TIFF 图片"
                Case ContainerKind.Webp : Return "WebP 图片"
                Case ContainerKind.Ico : Return "ICO 图标"
                Case ContainerKind.Pdf : Return "PDF 文档"
                Case ContainerKind.Psd : Return "PSD 文档"
                Case ContainerKind.Zip : Return "ZIP 归档"
                Case ContainerKind.SevenZip : Return "7Z 归档"
                Case ContainerKind.Rtf : Return "RTF 文档"
                Case ContainerKind.StampBin : Return "SymbolPrint 章文件"
                Case ContainerKind.PlainText : Return "纯文本"
                Case Else : Return "未知类型"
            End Select
        End Function

        ''' <summary>说明判定依据：内容 vs 扩展名，供日志里写清楚为什么这么选。</summary>
        Public Function Describe(filePath As String) As String
            Dim kind = KindOf(filePath)
            Dim byHead = kind <> ContainerKind.Unknown
            Dim ext = CarrierExt.ExtOf(filePath)
            If byHead Then
                Return $"{KindName(kind)}（按文件头识别，扩展名 {If(ext = "", "无", ext)}）"
            End If
            Return $"扩展名 {If(ext = "", "（无）", ext)}（文件头未识别，按扩展名判断）"
        End Function

        ''' <summary>头部是否像普通文本：没有 NUL，且能按 UTF-8 解出、没有替换字符。</summary>
        Private Function LooksLikeText(head As Byte()) As Boolean
            For i = 0 To head.Length - 1
                If head(i) = 0 Then Return False
            Next
            ' 控制字符过多（除常见空白）就当作二进制
            Dim ctrl = 0
            For i = 0 To head.Length - 1
                Dim b = head(i)
                If b < &H20 AndAlso b <> 9 AndAlso b <> 10 AndAlso b <> 13 Then ctrl += 1
            Next
            If ctrl > 2 Then Return False
            Try
                Dim s = Encoding.UTF8.GetString(head)
                Return Not s.Contains(ChrW(&HFFFD))
            Catch
                Return False
            End Try
        End Function

        Private Function Starts(h As Byte(), ParamArray sig As Integer()) As Boolean
            Return StartsAt(h, 0, sig)
        End Function

        Private Function StartsAt(h As Byte(), offset As Integer, ParamArray sig As Integer()) As Boolean
            If h Is Nothing OrElse h.Length < offset + sig.Length Then Return False
            For i = 0 To sig.Length - 1
                If h(offset + i) <> CByte(sig(i)) Then Return False
            Next
            Return True
        End Function

    End Module

End Namespace
