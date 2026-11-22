Imports System
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Drawing.Imaging
Imports System.IO
Imports System.Text
Imports SymbolPrint.Core.Util

Namespace Visual

    ''' <summary>
    ''' 视觉章的位置与外观。坐标全部用"相对比例"(0–1)，原点在左上角，
    ''' 这样同一份设置可以直接套到不同尺寸的图片/PDF 页面上。
    ''' </summary>
    Public NotInheritable Class VisualMark
        ''' <summary>章图案（SVG 源码）。留空则必须提供 PngBytes。</summary>
        Public Property SvgMarkup As String = ""
        ''' <summary>已光栅化的章图案（PNG）。优先使用它，避免不同渲染器出差异。</summary>
        Public Property PngBytes As Byte()

        ''' <summary>左边距占宽度比例（0–1）。</summary>
        Public Property XRatio As Double = 0.72
        ''' <summary>上边距占高度比例（0–1）。</summary>
        Public Property YRatio As Double = 0.78
        ''' <summary>章宽占宽度比例（0–1）。</summary>
        Public Property WidthRatio As Double = 0.22
        ''' <summary>不透明度（0–1）。</summary>
        Public Property Opacity As Double = 0.9
        ''' <summary>顺时针旋转角度。</summary>
        Public Property RotationDegrees As Double = 0
        ''' <summary>PDF 从 1 开始的页码（图片忽略）。0 表示全部页面。</summary>
        Public Property PdfPage As Integer = 1

        ' ---- 以下来自「高级设置 → 图案 / PDF」。锚点为 Custom 时仍按 XRatio/YRatio 定位，
        '      否则按「锚点 + 边距 + 偏移」在真实尺寸上算绝对坐标（设置才会真的生效）。----
        Public Property Anchor As Model.ImageAnchor = Model.ImageAnchor.Custom
        Public Property OffsetXPx As Integer = 0
        Public Property OffsetYPx As Integer = 0
        Public Property MarginPx As Integer = 0
        ''' <summary>章图案宽度上限（px）。0 表示不限制。</summary>
        Public Property MaxWidthPx As Integer = 0
        ''' <summary>章图案高度上限（px）。0 表示不限制。</summary>
        Public Property MaxHeightPx As Integer = 0
        ''' <summary>是否抗锯齿；关掉后边缘会有锯齿，但老机器更快。</summary>
        Public Property AntiAlias As Boolean = True
        ''' <summary>JPEG 重编码质量（1–100）。</summary>
        Public Property JpegQuality As Integer = 92
        ''' <summary>渲染 DPI（PDF 用）。</summary>
        Public Property Dpi As Integer = 300
        ''' <summary>SVG 光栅化时是否保留透明背景。</summary>
        Public Property TransparentBackground As Boolean = True

        Public Function HasImage() As Boolean
            Return (PngBytes IsNot Nothing AndAlso PngBytes.Length > 0) OrElse Not String.IsNullOrWhiteSpace(SvgMarkup)
        End Function
    End Class

    Public NotInheritable Class VisualMarkResult
        Public Property Applied As Boolean = False
        Public Property Reason As String = ""
        Public Property PageCount As Integer = 0
        Public Property MarkedWidthPx As Integer = 0
    End Class

    ''' <summary>
    ''' 视觉章合成：把章图案画到图片或 PDF 的指定位置。
    ''' 重要：这一步会**改写文件本身**（图片要重新编码、PDF 要重新保存），
    ''' 所以盖章流程里必须"先打图案、再算原文件哈希、最后追加记录"，
    ''' 这样记录里的"原文件哈希"指的是"已经带图案的那份文件"，语义才自洽。
    ''' </summary>
    Public Module VisualMarkApplier

        ''' <summary>
        ''' PDFsharp 需要 1252 等传统代码页编码；.NET Core 默认只带 Unicode 编码。
        ''' 用模块的静态构造函数保证"谁先用到视觉模块谁就把 provider 注册好"，调用方不用记这件事。
        ''' </summary>
        Sub New()
            Text.Encoding.RegisterProvider(Text.CodePagesEncodingProvider.Instance)
        End Sub

        ' GDI+ 在多线程下不稳定，贴图统一串行化
        Private ReadOnly _gdiLock As New Object()

        Private ReadOnly ImageExts As String() = {".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".dib", ".tif", ".tiff"}

        Public Function CanApply(filePath As String) As Boolean
            Dim ext = Path.GetExtension(filePath).ToLowerInvariant()
            If ext = ".pdf" Then Return True
            Return Array.IndexOf(ImageExts, ext) >= 0
        End Function

        Public Function Apply(filePath As String, mark As VisualMark,
                              Optional sink As IProgressSink = Nothing,
                              Optional log As Action(Of String) = Nothing) As VisualMarkResult
            Dim res As New VisualMarkResult()
            If mark Is Nothing OrElse Not mark.HasImage() Then
                res.Reason = "没有提供章图案（SVG 或 PNG）。"
                Return res
            End If
            If Not CanApply(filePath) Then
                res.Reason = $"该格式不支持视觉贴图（{Path.GetExtension(filePath)}），只写入盖章数据。"
                Return res
            End If

            Dim ext = Path.GetExtension(filePath).ToLowerInvariant()
            Dim png = GetMarkPng(mark)

            If ext = ".pdf" Then
                Return ApplyPdf(filePath, png, mark, sink, log)
            End If
            Return ApplyImage(filePath, png, mark, sink, log)
        End Function

        ''' <summary>取得章图案位图：优先用预渲染的 PNG，否则把 SVG 光栅化。</summary>
        Public Function GetMarkPng(mark As VisualMark, Optional targetWidthPx As Integer = 512) As Byte()
            If mark.PngBytes IsNot Nothing AndAlso mark.PngBytes.Length > 0 Then Return mark.PngBytes
            Return RenderSvgToPng(mark.SvgMarkup, targetWidthPx, mark.TransparentBackground)
        End Function

        ''' <summary>SVG → PNG（透明背景）。文字类特性依赖字体与渲染器，复杂滤镜可能被简化。</summary>
        Public Function RenderSvgToPng(svgMarkup As String, widthPx As Integer,
                                       Optional transparent As Boolean = True) As Byte()
            If String.IsNullOrWhiteSpace(svgMarkup) Then Throw New ArgumentException("SVG 内容为空。", NameOf(svgMarkup))
            If widthPx < 16 Then widthPx = 16

            Dim doc As Svg.SvgDocument
            Try
                doc = Svg.SvgDocument.FromSvg(Of Svg.SvgDocument)(svgMarkup)
            Catch ex As Exception
                Throw New InvalidDataException("SVG 解析失败：" & ex.Message, ex)
            End Try

            Dim w = If(doc.Width.Value > 0, doc.Width.Value, widthPx)
            Dim h = If(doc.Height.Value > 0, doc.Height.Value, widthPx)
            Dim heightPx = Math.Max(16, CInt(Math.Round(widthPx * h / w)))
            doc.Width = widthPx
            doc.Height = heightPx

            Using bmp As Bitmap = doc.Draw(widthPx, heightPx)
                If transparent Then
                    Using ms As New MemoryStream()
                        bmp.Save(ms, ImageFormat.Png)
                        Return ms.ToArray()
                    End Using
                End If

                ' 背景透明关掉：先铺白底再叠图案，这样盖到深色底图上不会有"半透明黑边"
                Using flat As New Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb)
                    Using g = Graphics.FromImage(flat)
                        g.Clear(Color.White)
                        g.DrawImage(bmp, New Rectangle(0, 0, bmp.Width, bmp.Height))
                    End Using
                    Using ms As New MemoryStream()
                        flat.Save(ms, ImageFormat.Png)
                        Return ms.ToArray()
                    End Using
                End Using
            End Using
        End Function

        ' ==================== 图片 ====================

        Private Function ApplyImage(filePath As String, png As Byte(), mark As VisualMark,
                                    sink As IProgressSink, log As Action(Of String)) As VisualMarkResult
            Dim res As New VisualMarkResult()
            Dim ext = Path.GetExtension(filePath).ToLowerInvariant()

            SyncLock _gdiLock
                ' 先读进内存再释放文件句柄，避免 GDI+ 锁文件
                Dim raw = File.ReadAllBytes(filePath)
                Using srcMs As New MemoryStream(raw)
                    Using src As Image = Image.FromStream(srcMs, True, True)
                        Dim w = src.Width
                        Dim h = src.Height
                        Dim markW = Math.Max(8, CInt(Math.Round(w * Clamp01(mark.WidthRatio))))
                        ' 「最大宽度/高度」是硬上限：超过就等比缩小，避免章盖满整张图
                        If mark.MaxWidthPx > 0 AndAlso markW > mark.MaxWidthPx Then markW = mark.MaxWidthPx
                        Using markBmp = MakeMarkBitmap(png, markW, mark.Opacity)
                            Dim markH = markBmp.Height
                            If mark.MaxHeightPx > 0 AndAlso markH > mark.MaxHeightPx Then
                                markH = mark.MaxHeightPx
                                markW = Math.Max(8, CInt(Math.Round(markH * (markBmp.Width / CDbl(markBmp.Height)))))
                            End If

                            Dim x, y As Integer
                            If mark.Anchor = Model.ImageAnchor.Custom Then
                                x = CInt(Math.Round(w * Clamp01(mark.XRatio)))
                                y = CInt(Math.Round(h * Clamp01(mark.YRatio)))
                            Else
                                Dim pos = AnchorPoint(mark.Anchor, w, h, markW, markH, mark.MarginPx, mark.OffsetXPx, mark.OffsetYPx)
                                x = pos.X
                                y = pos.Y
                            End If
                            ' 位置越界时贴边，不裁掉章
                            If x + markW > w Then x = Math.Max(0, w - markW)
                            If y + markH > h Then y = Math.Max(0, h - markH)

                            Using dst As New Bitmap(w, h, PixelFormat.Format32bppArgb)
                                Using g = Graphics.FromImage(dst)
                                    g.CompositingQuality = CompositingQuality.HighQuality
                                    g.InterpolationMode = InterpolationMode.HighQualityBicubic
                                    g.PixelOffsetMode = PixelOffsetMode.HighQuality
                                    g.SmoothingMode = If(mark.AntiAlias, SmoothingMode.AntiAlias, SmoothingMode.None)
                                    g.DrawImage(src, New Rectangle(0, 0, w, h))

                                    If Math.Abs(mark.RotationDegrees) > 0.01 Then
                                        Dim state = g.Save()
                                        ' 以章中心为旋转中心
                                        g.TranslateTransform(x + markW / 2.0F, y + markH / 2.0F)
                                        g.RotateTransform(CSng(mark.RotationDegrees))
                                        g.TranslateTransform(-(x + markW / 2.0F), -(y + markH / 2.0F))
                                        g.DrawImage(markBmp, New Rectangle(x, y, markW, markH))
                                        g.Restore(state)
                                    Else
                                        g.DrawImage(markBmp, New Rectangle(x, y, markW, markH))
                                    End If
                                End Using

                                Dim fmt = ImageFormatFor(ext)
                                If fmt Is Nothing Then
                                    res.Reason = $"GDI+ 不支持写回 {ext} 格式，只写入盖章数据。"
                                    Return res
                                End If

                                ' 先写临时文件再替换，避免写一半把原图弄坏
                                Dim tmp = filePath & ".sp-tmp"
                                If fmt Is ImageFormat.Jpeg Then
                                    Dim enc = Imaging.ImageCodecInfo.GetImageEncoders().
                                        First(Function(c) c.FormatID = ImageFormat.Jpeg.Guid)
                                    Using cp As New Imaging.EncoderParameters(1)
                                        cp.Param(0) = New Imaging.EncoderParameter(Imaging.Encoder.Quality,
                                                                                   CLng(Math.Max(1, Math.Min(100, mark.JpegQuality))))
                                        dst.Save(tmp, enc, cp)
                                    End Using
                                Else
                                    dst.Save(tmp, fmt)
                                End If
                                File.Copy(tmp, filePath, True)
                                File.Delete(tmp)
                            End Using
                            res.Applied = True
                            res.MarkedWidthPx = markW
                            log?.Invoke($"已在图片上绘制章图案：{markW}×{markH} px，位置 ({x},{y})，透明度 {mark.Opacity:0.##}。")
                        End Using
                    End Using
                End Using
            End SyncLock
            Return res
        End Function

        ''' <summary>
        ''' 按锚点算出图案左上角坐标：边距是"离边缘固定距离"，偏移是在此基础上再挪。
        ''' 锚点来自「高级设置 → 图案 → 默认锚点」，改了立刻影响贴图位置。
        ''' </summary>
        Private Function AnchorPoint(anchor As Model.ImageAnchor, w As Integer, h As Integer,
                                     markW As Integer, markH As Integer,
                                     margin As Integer, offX As Integer, offY As Integer) As Drawing.Point
            Dim m = Math.Max(0, margin)
            Dim x, y As Integer
            Select Case anchor
                Case Model.ImageAnchor.TopLeft, Model.ImageAnchor.MiddleLeft, Model.ImageAnchor.BottomLeft
                    x = m + offX
                Case Model.ImageAnchor.TopRight, Model.ImageAnchor.MiddleRight, Model.ImageAnchor.BottomRight
                    x = w - markW - m - offX
                Case Else
                    x = (w - markW) \ 2 + offX
            End Select

            Select Case anchor
                Case Model.ImageAnchor.TopLeft, Model.ImageAnchor.TopCenter, Model.ImageAnchor.TopRight
                    y = m + offY
                Case Model.ImageAnchor.BottomLeft, Model.ImageAnchor.BottomCenter, Model.ImageAnchor.BottomRight
                    y = h - markH - m - offY
                Case Else
                    y = (h - markH) \ 2 + offY
            End Select
            Return New Drawing.Point(x, y)
        End Function

        Private Function MakeMarkBitmap(png As Byte(), widthPx As Integer, opacity As Double) As Bitmap
            Dim o = Clamp01(opacity)
            Using ms As New MemoryStream(png)
                Using raw As Image = Image.FromStream(ms, True, True)
                    Dim ratio = raw.Height / CDbl(raw.Width)
                    Dim h = Math.Max(8, CInt(Math.Round(widthPx * ratio)))
                    Dim dst As New Bitmap(widthPx, h, PixelFormat.Format32bppArgb)
                    Using g = Graphics.FromImage(dst)
                        g.CompositingQuality = CompositingQuality.HighQuality
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality
                        g.SmoothingMode = SmoothingMode.AntiAlias
                        g.Clear(Color.Transparent)

                        Dim cm As New ColorMatrix()
                        cm.Matrix33 = CSng(o) ' 整体 alpha 缩放
                        Using ia As New ImageAttributes()
                            ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap)
                            g.DrawImage(raw, New Rectangle(0, 0, widthPx, h), 0, 0, raw.Width, raw.Height,
                                        GraphicsUnit.Pixel, ia)
                        End Using
                    End Using
                    Return dst
                End Using
            End Using
        End Function

        Private Function ImageFormatFor(ext As String) As ImageFormat
            Select Case ext
                Case ".png" : Return ImageFormat.Png
                Case ".jpg", ".jpeg", ".jpe", ".jfif" : Return ImageFormat.Jpeg
                Case ".gif" : Return ImageFormat.Gif
                Case ".bmp", ".dib" : Return ImageFormat.Bmp
                Case ".tif", ".tiff" : Return ImageFormat.Tiff
                Case Else : Return Nothing
            End Select
        End Function

        ' ==================== PDF ====================

        Private Function ApplyPdf(filePath As String, png As Byte(), mark As VisualMark,
                                  sink As IProgressSink, log As Action(Of String)) As VisualMarkResult
            Dim res As New VisualMarkResult()
            Dim tmp = filePath & ".sp-tmp"

            Using doc = PdfSharp.Pdf.IO.PdfReader.Open(filePath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify)
                Dim pages = doc.Pages
                res.PageCount = pages.Count
                Dim first As Integer = If(mark.PdfPage <= 0, 1, mark.PdfPage)
                Dim last As Integer = If(mark.PdfPage <= 0, pages.Count, Math.Min(mark.PdfPage, pages.Count))
                If first > pages.Count Then first = pages.Count

                Dim pngWithAlpha = ApplyOpacityToPng(png, mark.Opacity)
                Using ms As New MemoryStream(pngWithAlpha)
                    Using img = PdfSharp.Drawing.XImage.FromStream(ms)
                        For p = first To last
                            ProgressExt.ThrowIfCancelled(sink)
                            Dim page = pages(p - 1)
                            Dim pw = page.Width.Point
                            Dim ph = page.Height.Point
                            Dim mw = pw * Clamp01(mark.WidthRatio)
                            Dim mh = mw * img.PixelHeight / img.PixelWidth
                            Dim mx = pw * Clamp01(mark.XRatio)
                            Dim my = ph * Clamp01(mark.YRatio)
                            If mx + mw > pw Then mx = Math.Max(0, pw - mw)
                            If my + mh > ph Then my = Math.Max(0, ph - mh)

                            Using gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page, PdfSharp.Drawing.XGraphicsPdfPageOptions.Append)
                                Dim st = gfx.Save()
                                gfx.TranslateTransform(mx + mw / 2.0, my + mh / 2.0)
                                gfx.RotateTransform(mark.RotationDegrees)
                                gfx.TranslateTransform(-(mx + mw / 2.0), -(my + mh / 2.0))
                                gfx.DrawImage(img, mx, my, mw, mh)
                                gfx.Restore(st)
                            End Using
                            res.MarkedWidthPx = CInt(Math.Round(mw))
                        Next
                    End Using
                End Using

                doc.Save(tmp)
            End Using

            File.Copy(tmp, filePath, True)
            File.Delete(tmp)
            res.Applied = True
            log?.Invoke($"已在 PDF 第 {If(mark.PdfPage <= 0, "全部", mark.PdfPage.ToString())} 页绘制章图案（{res.MarkedWidthPx} pt 宽）。")
            Return res
        End Function

        ''' <summary>把不透明度烘进 PNG 的 alpha 通道（PDF 绘制路径无法直接指定透明度）。</summary>
        Private Function ApplyOpacityToPng(png As Byte(), opacity As Double) As Byte()
            Dim o = Clamp01(opacity)
            If o >= 0.999 Then Return png
            Using ms As New MemoryStream(png)
                Using src As Image = Image.FromStream(ms, True, True)
                    Using dst As New Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb)
                        Using g = Graphics.FromImage(dst)
                            g.Clear(Color.Transparent)
                            Dim cm As New ColorMatrix()
                            cm.Matrix33 = CSng(o)
                            Using ia As New ImageAttributes()
                                ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap)
                                g.DrawImage(src, New Rectangle(0, 0, src.Width, src.Height),
                                            0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia)
                            End Using
                        End Using
                        Using outMs As New MemoryStream()
                            dst.Save(outMs, ImageFormat.Png)
                            Return outMs.ToArray()
                        End Using
                    End Using
                End Using
            End Using
        End Function

        Private Function Clamp01(v As Double) As Double
            If v < 0 Then Return 0
            If v > 1 Then Return 1
            Return v
        End Function

    End Module

End Namespace
