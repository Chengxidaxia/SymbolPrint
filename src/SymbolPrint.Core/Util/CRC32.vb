Imports System

Namespace Util

    ''' <summary>CRC-32（IEEE 802.3，多项式 0xEDB88320），用于内部完整性快检。</summary>
    Public NotInheritable Class CRC32

        Private Shared ReadOnly Table As UInteger() = BuildTable()

        Private Shared Function BuildTable() As UInteger()
            Dim t(255) As UInteger
            For i As Integer = 0 To 255
                Dim c As UInteger = CUInt(i)
                For k As Integer = 0 To 7
                    If (c And 1UI) <> 0UI Then
                        c = (c >> 1) Xor &HEDB88320UI
                    Else
                        c >>= 1
                    End If
                Next
                t(i) = c
            Next
            Return t
        End Function

        Public Shared Function Compute(data As Byte(), Optional offset As Integer = 0, Optional count As Integer = -1) As UInteger
            If data Is Nothing Then Return 0UI
            If count < 0 Then count = data.Length - offset
            Dim c As UInteger = &HFFFFFFFFUI
            For i As Integer = offset To offset + count - 1
                c = Table(CInt((c Xor data(i)) And &HFFUI)) Xor (c >> 8)
            Next
            Return c Xor &HFFFFFFFFUI
        End Function

    End Class

End Namespace
