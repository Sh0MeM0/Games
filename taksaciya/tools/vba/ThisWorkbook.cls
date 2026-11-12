Option Explicit
' Таксация Лайт — события книги для защиты доступа (код — в модуле TK).

Private Sub Workbook_Open()
    TK_OnOpen
End Sub

Private Sub Workbook_BeforeSave(ByVal SaveAsUI As Boolean, Cancel As Boolean)
    TK_BeforeSave
End Sub

Private Sub Workbook_AfterSave(ByVal Success As Boolean)
    TK_AfterSave
End Sub
