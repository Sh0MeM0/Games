Option Explicit
' Таксация Лайт — доступ по недельному коду.
'
' Владелец при первом открытии задаёт логин и пароль. В книге хранится не пароль, а число, полученное из логина и
' пароля (ячейка A1 служебного листа «TK», кодовое имя SheetTKD), и последний введённый код (ячейка A2). Служебный
' лист всегда полностью скрыт.
' Код = номер дня выдачи (4 цифры, от 01.01.2025) + 6 цифр, вычисленных из этого числа и дня. Код действует 7 дней,
' включая день выдачи, и подходит к любой копии книги с тем же числом владельца.
' Пока код не введён или устарел, листы с расчётом скрыты (видим только лист «Доступ»); перед каждым сохранением
' они тоже скрываются, поэтому файл без включённых макросов показывает только лист «Доступ».
' Это защита от обычного пользователя, а не криптографическая: знающий Excel человек может её обойти.

Private Const P_MOD As Double = 2147483647#
Private Const BASE_DAY As Long = 45658          ' 01.01.2025
Private Const VALID_DAYS As Long = 7
Private Const SPLASH As String = "SheetTK"      ' кодовое имя листа «Доступ»
Private Const STORE As String = "SheetTKD"      ' кодовое имя служебного листа «TK»
Private gLastSheet As String

Private Function T() As String
    T = "Таксация Лайт — доступ"
End Function

' ---------------------------------------------------------------- расчёт числа владельца и кода
Private Function ModD(ByVal x As Double, ByVal m As Double) As Double
    Dim r As Double
    r = x - m * Int(x / m)
    If r < 0 Then r = r + m
    If r >= m Then r = r - m
    ModD = r
End Function

Public Function TK_Secret(ByVal login As String, ByVal pass As String) As Double
    Dim s As String, i As Long, c As Long, h1 As Double, h2 As Double
    s = login & "|" & pass
    h1 = 7
    h2 = 13
    For i = 1 To Len(s)
        c = AscW(Mid$(s, i, 1))
        If c < 0 Then c = c + 65536
        h1 = ModD(h1 * 131 + c + 1, P_MOD)
        h2 = ModD(h2 * 1031 + c * 7 + 3, P_MOD)
    Next i
    TK_Secret = ModD(h1, 100000) * 100000 + ModD(h2, 100000) + 1
End Function

Public Function TK_Code(ByVal secret As Double, ByVal d As Long) As String
    Dim x As Double, y As Double, r As Double
    x = ModD(secret, P_MOD)
    y = Int(secret / P_MOD)
    r = ModD(x * 48271 + CDbl(d) * 16807 + y * 69621 + 12345, P_MOD)
    r = ModD(r * 69621 + 7, P_MOD)
    r = ModD(r * 48271 + d, P_MOD)
    TK_Code = Format$(d, "0000") & "-" & Format$(ModD(r, 1000000), "000000")
End Function

Public Function TK_SelfTest() As String
    TK_SelfTest = Format$(TK_Secret("owner", "Pa55-слово"), "0") & ";" & TK_Code(1234567890#, 680) & ";" & _
        TK_Code(TK_Secret("Иванов", "qwerty"), 1) & ";" & TK_Code(9999999999#, 9999)
End Function

' ---------------------------------------------------------------- хранение: служебный лист «TK» (всегда скрыт)
Private Function StoreSheet() As Object
    Dim sh As Object
    For Each sh In ThisWorkbook.Sheets
        If sh.CodeName = STORE Then
            Set StoreSheet = sh
            Exit Function
        End If
    Next sh
End Function

Private Function StoreCell(ByVal nm As String) As String
    If nm = "TK_S" Then StoreCell = "A1" Else StoreCell = "A2"
End Function

Private Function NameVal(ByVal nm As String) As String
    Dim sh As Object
    Set sh = StoreSheet()
    If sh Is Nothing Then Exit Function
    NameVal = Trim$(CStr(sh.Range(StoreCell(nm)).Value))
End Function

Private Sub SetName(ByVal nm As String, ByVal v As String, ByVal asText As Boolean)
    Dim sh As Object
    Set sh = StoreSheet()
    If sh Is Nothing Then Exit Sub
    sh.Range(StoreCell(nm)).NumberFormat = "@"      ' текст: код хранится с ведущими нулями
    sh.Range(StoreCell(nm)).Value = v
End Sub

Private Function Secret() As Double
    Secret = Val(NameVal("TK_S"))
End Function

Private Function DayNo() As Long
    DayNo = CLng(Date) - BASE_DAY
End Function

Private Function Digits(ByVal v As String) As String
    Dim i As Long, ch As String, r As String
    For i = 1 To Len(v)
        ch = Mid$(v, i, 1)
        If ch >= "0" And ch <= "9" Then r = r & ch
    Next i
    Digits = r
End Function

Private Function LooksLikeCode(ByVal v As String) As Boolean
    Dim s As String
    s = Replace(Replace(Trim$(v), " ", ""), "-", "")
    LooksLikeCode = (Len(s) > 0 And Digits(s) = s)
End Function

Public Function TK_CodeValid(ByVal v As String) As Boolean
    Dim s As String, d As Long, sec As Double
    s = Digits(v)
    If Len(s) <> 10 Then Exit Function
    sec = Secret()
    If sec <= 0 Then Exit Function
    d = CLng(Left$(s, 4))
    If DayNo() < d Or DayNo() - d >= VALID_DAYS Then Exit Function
    TK_CodeValid = (Digits(TK_Code(sec, d)) = s)
End Function

Public Function TK_Unlocked() As Boolean
    If Secret() <= 0 Then Exit Function
    TK_Unlocked = TK_CodeValid(NameVal("TK_K"))
End Function

Private Function DayText(ByVal d As Long) As String
    DayText = Format$(CDate(BASE_DAY + d), "dd.mm.yyyy")
End Function

' ---------------------------------------------------------------- показ и скрытие листов
Private Function IsSplash(ByVal sh As Object) As Boolean
    IsSplash = (sh.CodeName = SPLASH)
End Function

Private Function IsStore(ByVal sh As Object) As Boolean
    IsStore = (sh.CodeName = STORE)
End Function

Public Sub TK_HideData()
    Dim sh As Object
    Application.ScreenUpdating = False
    For Each sh In ThisWorkbook.Sheets
        If IsSplash(sh) Then sh.Visible = -1
    Next sh
    For Each sh In ThisWorkbook.Sheets
        If Not IsSplash(sh) Then sh.Visible = 2
    Next sh
    Application.ScreenUpdating = True
End Sub

Public Sub TK_ShowData()
    Dim sh As Object
    Application.ScreenUpdating = False
    For Each sh In ThisWorkbook.Sheets
        If Not IsSplash(sh) And Not IsStore(sh) Then sh.Visible = -1
    Next sh
    For Each sh In ThisWorkbook.Sheets
        If IsSplash(sh) Then sh.Visible = 2
    Next sh
    If gLastSheet <> "" Then
        For Each sh In ThisWorkbook.Sheets
            If sh.CodeName = gLastSheet Then sh.Activate
        Next sh
    End If
    Application.ScreenUpdating = True
End Sub

Private Sub SaveQuiet()
    On Error Resume Next
    If Not ThisWorkbook.ReadOnly Then ThisWorkbook.Save
    On Error GoTo 0
End Sub

' ---------------------------------------------------------------- события книги
Public Sub TK_OnOpen()
    ' Невидимый Excel (например, его запускает связь данных AutoCAD): окна не показываем — они бы ждали ответа
    ' и программа зависла бы. Листы остаются такими, какими были сохранены.
    If Not Application.Visible Then Exit Sub
    If Secret() <= 0 Then
        TK_Setup
    ElseIf TK_Unlocked() Then
        TK_ShowData
        ThisWorkbook.Saved = True
    Else
        TK_HideData
        ThisWorkbook.Saved = True
        TK_Vhod
    End If
End Sub

Public Sub TK_BeforeSave()
    On Error Resume Next
    If Not IsSplash(ThisWorkbook.ActiveSheet) Then gLastSheet = ThisWorkbook.ActiveSheet.CodeName
    On Error GoTo 0
    TK_HideData
End Sub

Public Sub TK_AfterSave()
    If TK_Unlocked() Then TK_ShowData
    ThisWorkbook.Saved = True
End Sub

' ---------------------------------------------------------------- владелец и коды (Alt+F8)
Private Function OwnerPass(ByVal login As String) As Boolean
    Dim p As String
    p = InputBox("Пароль владельца для логина «" & login & "»:", T())
    If p = "" Then Exit Function
    If TK_Secret(login, p) = Secret() Then
        OwnerPass = True
    Else
        MsgBox "Логин или пароль неверный.", vbExclamation, T()
    End If
End Function

Private Function OwnerCheck() As Boolean
    Dim l As String
    l = Trim$(InputBox("Логин владельца:", T()))
    If l = "" Then Exit Function
    OwnerCheck = OwnerPass(l)
End Function

Private Sub ShowCode(ByVal c As String)
    Dim d As Long
    d = CLng(Left$(Digits(c), 4))
    InputBox "Код доступа на 7 дней: с " & DayText(d) & " по " & DayText(d + VALID_DAYS - 1) & " включительно." & vbCrLf & _
        "Код подходит к любой копии этой книги. Скопируйте его (Ctrl+C) и передайте человеку.", T(), c
End Sub

Public Sub TK_Setup()
    Dim l As String, p1 As String, p2 As String, s As Double, c As String
    MsgBox "Первый запуск книги с защитой." & vbCrLf & vbCrLf & _
        "Задайте логин и пароль владельца. Запишите их: книга хранит не пароль, а число, полученное из него, " & _
        "и напомнить пароль не сможет." & vbCrLf & _
        "С логином и паролем вы открываете доступ на 7 дней и получаете коды для других.", vbInformation, T()
    l = Trim$(InputBox("Логин владельца (новый):", T()))
    If l = "" Then GoTo Cancelled
    p1 = InputBox("Пароль владельца (новый):", T())
    If p1 = "" Then GoTo Cancelled
    p2 = InputBox("Повторите пароль:", T())
    If p1 <> p2 Then
        MsgBox "Пароли не совпали.", vbExclamation, T()
        GoTo Cancelled
    End If
    s = TK_Secret(l, p1)
    SetName "TK_S", Format$(s, "0"), False
    c = TK_Code(s, DayNo())
    SetName "TK_K", Digits(c), True
    TK_ShowData
    SaveQuiet
    MsgBox "Защита включена, книга сохранена. Доступ у вас открыт до " & DayText(DayNo() + VALID_DAYS - 1) & " включительно." & vbCrLf & vbCrLf & _
        "Код на 7 дней для другого человека: Alt+F8 > TK_KodDlyaPeredachi." & vbCrLf & _
        "Копия для передачи (доступ в ней закрыт): Alt+F8 > TK_KopiyaDlyaPeredachi." & vbCrLf & _
        "Через 7 дней книга снова спросит код — введите свой логин, затем пароль.", vbInformation, T()
    Exit Sub
Cancelled:
    TK_HideData
    ThisWorkbook.Saved = True
    MsgBox "Защита не настроена: листы с расчётом скрыты. Откройте книгу заново с включёнными макросами.", vbExclamation, T()
End Sub

Public Sub TK_Vhod()
    Dim v As String, c As String
    If Secret() <= 0 Then
        TK_Setup
        Exit Sub
    End If
    v = Trim$(InputBox("Доступ к расчёту закрыт или срок кода истёк." & vbCrLf & vbCrLf & _
        "Введите код доступа на 7 дней (его выдаёт владелец программы)." & vbCrLf & _
        "Владелец: введите свой логин — затем пароль.", T()))
    If v = "" Then Exit Sub
    If LooksLikeCode(v) Then
        If TK_CodeValid(v) Then
            SetName "TK_K", Digits(v), True
            TK_ShowData
            SaveQuiet
            MsgBox "Доступ открыт до " & DayText(CLng(Left$(Digits(v), 4)) + VALID_DAYS - 1) & " включительно.", vbInformation, T()
        Else
            MsgBox "Код неверный или его срок истёк. Получите новый код у владельца программы.", vbExclamation, T()
        End If
    ElseIf OwnerPass(v) Then
        c = TK_Code(Secret(), DayNo())
        SetName "TK_K", Digits(c), True
        TK_ShowData
        SaveQuiet
        ShowCode c
    End If
End Sub

Public Sub TK_KodDlyaPeredachi()
    If Secret() <= 0 Then
        MsgBox "Защита ещё не настроена.", vbExclamation, T()
        Exit Sub
    End If
    If Not OwnerCheck() Then Exit Sub
    ShowCode TK_Code(Secret(), DayNo())
End Sub

Public Sub TK_KopiyaDlyaPeredachi()
    Dim fn As Variant, k As String, wasSaved As Boolean, bn As String
    If Secret() <= 0 Then
        MsgBox "Защита ещё не настроена.", vbExclamation, T()
        Exit Sub
    End If
    If Not OwnerCheck() Then Exit Sub
    bn = ThisWorkbook.Name
    If LCase$(Right$(bn, 5)) = ".xlsm" Then bn = Left$(bn, Len(bn) - 5)
    fn = Application.GetSaveAsFilename(InitialFileName:=ThisWorkbook.Path & Application.PathSeparator & bn & " - для передачи.xlsm", _
        FileFilter:="Excel (*.xlsm), *.xlsm", Title:=T())
    If VarType(fn) = vbBoolean Then Exit Sub
    wasSaved = ThisWorkbook.Saved
    k = NameVal("TK_K")
    SetName "TK_K", "", True
    TK_HideData
    ThisWorkbook.SaveCopyAs CStr(fn)
    SetName "TK_K", k, True
    TK_ShowData
    ThisWorkbook.Saved = wasSaved
    MsgBox "Копия для передачи сохранена:" & vbCrLf & CStr(fn) & vbCrLf & vbCrLf & _
        "В ней расчёт откроется только по коду. Код на 7 дней: Alt+F8 > TK_KodDlyaPeredachi.", vbInformation, T()
End Sub
