Imports System
Imports System.IO
Imports SymbolPrint.Core.Model
Imports SymbolPrint.Core.Util

Namespace Carriers

    ''' <summary>
    ''' 尾部追加型载体：PNG / JPEG / GIF / BMP / TIFF / WEBP / ICO / PDF。
    ''' 做法：把 信封(payload+48B footer) 直接写到文件末尾。
    ''' 这些格式的"有效数据"到结束标记（PNG 的 IEND、JPEG 的 EOI、PDF 的 %%EOF）就结束了，
    ''' 解码器/阅读器都会忽略之后的字节，所以原文件仍能正常打开。
    ''' 铁律：只追加，不覆盖；移除时只从尾部截掉我们那一段。
    ''' </summary>
    Public NotInheritable Class TrailerCarrier
        Implements IStampCarrier

        Private Shared ReadOnly Exts As String() = {".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".dib",
                                                    ".tif", ".tiff", ".webp", ".ico", ".pdf", ".psd", ".heic", ".avif"}

        Public ReadOnly Property Id As String Implements IStampCarrier.Id
            Get
                Return "trailer"
            End Get
        End Property

        Public ReadOnly Property DisplayName As String Implements IStampCarrier.DisplayName
            Get
                Return "文件尾部追加"
            End Get
        End Property

        Public Function CanHandle(filePath As String) As Boolean Implements IStampCarrier.CanHandle
            Return Array.IndexOf(Exts, CarrierExt.ExtOf(filePath)) >= 0
        End Function

        Public Function Capacity(filePath As String) As Long Implements IStampCarrier.Capacity
            Return Long.MaxValue
        End Function

        Public Function ReadBlob(filePath As String) As Byte() Implements IStampCarrier.ReadBlob
            Dim len As Long = New FileInfo(filePath).Length
            If len < SealEnvelope.FooterSize Then Return Nothing
            Dim tail = CarrierExt.ReadTail(filePath, SealEnvelope.FooterSize)
            Dim f = SealEnvelope.TryParseFooter(tail, 0)
            If f Is Nothing Then Return Nothing
            If f.TotalLength <= SealEnvelope.FooterSize OrElse f.TotalLength > len Then Return Nothing
            Return CarrierExt.ReadRange(filePath, len - f.TotalLength, f.TotalLength)
        End Function

        ''' <summary>
        ''' 只在末尾追加，**不清除**文件里已有的记录。
        ''' 是否先摘旧记录由调用方决定（<c>SealOptions.ReplaceExisting</c>）：
        ''' 之前这里隐式 RemoveBlob，导致「保留旧记录、追加新章」永远做不到——
        ''' 上层怎么写都变成替换。载体只负责写，替换策略不该藏在载体里。
        ''' </summary>
        Public Sub WriteBlob(filePath As String, blob As Byte(), options As CarrierOptions) Implements IStampCarrier.WriteBlob
            If blob Is Nothing OrElse blob.Length <= SealEnvelope.FooterSize Then Throw New ArgumentException("信封内容为空。", NameOf(blob))
            Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None)
                fs.Position = fs.Length
                fs.Write(blob, 0, blob.Length)
                fs.Flush()
            End Using
            options?.Log?.Invoke($"已在文件末尾追加 {blob.Length} 字节（原内容一个字节都没改）。")
        End Sub

        Public Function RemoveBlob(filePath As String) As Boolean Implements IStampCarrier.RemoveBlob
            Dim len As Long = New FileInfo(filePath).Length
            If len < SealEnvelope.FooterSize Then Return False
            Dim tail = CarrierExt.ReadTail(filePath, SealEnvelope.FooterSize)
            Dim f = SealEnvelope.TryParseFooter(tail, 0)
            If f Is Nothing Then Return False
            If f.TotalLength > len Then Return False
            Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None)
                fs.SetLength(len - f.TotalLength)
                fs.Flush()
            End Using
            Return True
        End Function

    End Class

    ''' <summary>
    ''' 7z 载体。7z 的结构是：[32B StartHeader][数据流][NextHeader]，StartHeader 在文件**开头**，
    ''' NextHeader 在文件**末尾**，且所有偏移都记录在开头那 32 字节里。
    ''' 因此"在文件末尾追加"完全不改变任何偏移字段，7z 仍能正常打开（本机 7-Zip 25.01 实测：
    ''' 追加 5KB/100KB/5MB 后 `7z l` 与 `7z t` 均通过）。
    ''' </summary>
    Public NotInheritable Class SevenZipCarrier
        Implements IStampCarrier

        Private Shared ReadOnly Sig As Byte() = {&H37, &H7A, &HBC, &HAF, &H27, &H1C}

        Public ReadOnly Property Id As String Implements IStampCarrier.Id
            Get
                Return "7z-tail"
            End Get
        End Property

        Public ReadOnly Property DisplayName As String Implements IStampCarrier.DisplayName
            Get
                Return "7z 归档尾部追加"
            End Get
        End Property

        Public Function CanHandle(filePath As String) As Boolean Implements IStampCarrier.CanHandle
            Return String.Equals(CarrierExt.ExtOf(filePath), ".7z", StringComparison.OrdinalIgnoreCase)
        End Function

        Public Function Is7z(filePath As String) As Boolean
            Try
                Dim head = CarrierExt.ReadRange(filePath, 0, Sig.Length)
                If head Is Nothing Then Return False
                For i As Integer = 0 To Sig.Length - 1
                    If head(i) <> Sig(i) Then Return False
                Next
                Return True
            Catch
                Return False
            End Try
        End Function

        Public Function Capacity(filePath As String) As Long Implements IStampCarrier.Capacity
            Return Long.MaxValue
        End Function

        Public Function ReadBlob(filePath As String) As Byte() Implements IStampCarrier.ReadBlob
            If Not Is7z(filePath) Then Return Nothing
            Dim len As Long = New FileInfo(filePath).Length
            If len < SealEnvelope.FooterSize + 32 Then Return Nothing
            Dim tail = CarrierExt.ReadTail(filePath, SealEnvelope.FooterSize)
            Dim f = SealEnvelope.TryParseFooter(tail, 0)
            If f Is Nothing Then Return Nothing
            If f.TotalLength > len Then Return Nothing
            Return CarrierExt.ReadRange(filePath, len - f.TotalLength, f.TotalLength)
        End Function

        Public Sub WriteBlob(filePath As String, blob As Byte(), options As CarrierOptions) Implements IStampCarrier.WriteBlob
            If Not Is7z(filePath) Then Throw New InvalidDataException("这不是有效的 7z 归档（缺少 7z 签名）。")
            ' 同 TrailerCarrier：不清旧记录，替换与否由 SealOptions.ReplaceExisting 决定
            Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None)
                fs.Position = fs.Length
                fs.Write(blob, 0, blob.Length)
                fs.Flush()
            End Using
            options?.Log?.Invoke($"已在 7z 末尾追加 {blob.Length} 字节；StartHeader 与 NextHeader 偏移均未改动，归档仍可正常打开。")
        End Sub

        Public Function RemoveBlob(filePath As String) As Boolean Implements IStampCarrier.RemoveBlob
            Dim len As Long = New FileInfo(filePath).Length
            If len < SealEnvelope.FooterSize + 32 Then Return False
            Dim tail = CarrierExt.ReadTail(filePath, SealEnvelope.FooterSize)
            Dim f = SealEnvelope.TryParseFooter(tail, 0)
            If f Is Nothing Then Return False
            If f.TotalLength > len Then Return False
            Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None)
                fs.SetLength(len - f.TotalLength)
                fs.Flush()
            End Using
            Return True
        End Function

    End Class

End Namespace
