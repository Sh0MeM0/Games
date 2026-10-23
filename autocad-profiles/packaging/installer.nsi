Unicode True
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "nsDialogs.nsh"

!ifndef PAYLOAD
  !error "Pass /DPAYLOAD=absolute-bundle-directory"
!endif
!ifndef OUTPUT
  !error "Pass /DOUTPUT=installer-path"
!endif
!define PRODUCT "Профиля для AutoCAD 2020–2024"
!define VERSION "0.6.0"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Sh0MeM0.Profiles"

Name "${PRODUCT}"
OutFile "${OUTPUT}"
InstallDir "$PROGRAMFILES64\Autodesk\ApplicationPlugins\Sh0MeM0.Profiles.bundle"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
SetCompressorDictSize 32
BrandingText "Профиля ${VERSION}"
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "0.6.0.0"
VIAddVersionKey /LANG=1049 "ProductName" "${PRODUCT}"
VIAddVersionKey /LANG=1049 "FileDescription" "Установка модуля Профиля"
VIAddVersionKey /LANG=1049 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1049 "LegalCopyright" "Sh0MeM0, 2026"

Var ExistingInstallation
Var InstalledVersion
Var MaintenanceDialog
Var RepairOption
Var RemoveOption
Var RemoveSelected

!define MUI_ABORTWARNING
!define MUI_CUSTOMFUNCTION_UNABORT un.MarkUninstallCanceled
!define MUI_WELCOMEPAGE_TITLE "Профиля для AutoCAD 2020–2024"
!define MUI_WELCOMEPAGE_TEXT "Модуль строит продольные и поперечные профили каналов и прудов.$\r$\n$\r$\nПеред установкой закройте все окна AutoCAD. После установки запустите AutoCAD заново: появится верхнее меню «Профиля»; при включённой ленте доступна и вкладка.$\r$\n$\r$\nВерсия ${VERSION}: расчётное ядро проверено автоматическими тестами; проверку загрузки в вашей версии AutoCAD выполните по инструкции."
!insertmacro MUI_PAGE_WELCOME
Page custom MaintenancePage MaintenanceLeave
!insertmacro MUI_PAGE_LICENSE "${PAYLOAD}\LICENSE.txt"
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TITLE "Модуль установлен"
!define MUI_FINISHPAGE_TEXT "Запустите AutoCAD заново. Откройте верхнее меню «Профиля» или введите команду PROFILI.$\r$\n$\r$\nЕсли меню не видно, выполните PROFILI_MENU для его восстановления. Данные профилей можно сохранить отдельно в JSON.$\r$\n$\r$\nИнструкция находится в папке установленного модуля (README.md)."
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Нужна 64-разрядная Windows и AutoCAD 2020–2024 x64."
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP "Этот установщик предназначен для Windows 10/11 x64."
    Abort
  ${EndIf}
  SetRegView 64
  StrCpy $ExistingInstallation 0
  ReadRegStr $0 HKLM "${UNINSTALL_KEY}" "InstallLocation"
  ReadRegStr $InstalledVersion HKLM "${UNINSTALL_KEY}" "DisplayVersion"
  ${If} $InstalledVersion == ""
    StrCpy $InstalledVersion "не указана"
  ${EndIf}
  ${If} $0 != ""
    GetFullPathName $0 $0
    GetFullPathName $1 $INSTDIR
    ; The installer has always used this fixed directory. Never execute an
    ; arbitrary command or executable supplied by a changed registry value.
    ${If} $0 != $1
      MessageBox MB_ICONSTOP "В записи установленного модуля указан другой каталог. Удалите прежнюю установку через «Установленные приложения» Windows, затем повторите запуск."
      Abort
    ${EndIf}
    StrCpy $ExistingInstallation 1
  ${Else}
    IfFileExists "$INSTDIR\PackageContents.xml" 0 +2
      StrCpy $ExistingInstallation 1
  ${EndIf}
FunctionEnd

Function MaintenancePage
  ${If} $ExistingInstallation != 1
    Abort
  ${EndIf}
  !insertmacro MUI_HEADER_TEXT "Обслуживание модуля «Профиля»" "Выберите исправление или удаление установленного модуля."
  nsDialogs::Create 1018
  Pop $MaintenanceDialog
  ${If} $MaintenanceDialog == error
    Abort
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 24u "Модуль «Профиля» уже установлен (версия $InstalledVersion). Перед продолжением закройте все окна AutoCAD."
  Pop $0
  ${NSD_CreateRadioButton} 0 30u 100% 12u "Исправить / обновить до версии ${VERSION}"
  Pop $RepairOption
  ${NSD_CreateLabel} 12u 45u 95% 22u "Восстановить файлы модуля и обновить его. Ваши DWG и файлы проектов сохранятся."
  Pop $0
  ${NSD_CreateRadioButton} 0 77u 100% 12u "Удалить программу"
  Pop $RemoveOption
  ${NSD_CreateLabel} 12u 92u 95% 24u "Удалить установленный модуль. Чертежи DWG и сохранённые отдельно проекты JSON останутся на месте."
  Pop $0
  ${NSD_Check} $RepairOption
  IfFileExists "$INSTDIR\Uninstall.exe" maintenance_ready
    EnableWindow $RemoveOption 0
    ${NSD_CreateLabel} 0 120u 100% 18u "Файл удаления отсутствует. Сначала выберите «Исправить», затем запустите установщик повторно для удаления."
    Pop $0
  maintenance_ready:
  nsDialogs::Show
FunctionEnd

Function MaintenanceLeave
  ${NSD_GetState} $RemoveOption $RemoveSelected
  ${If} $RemoveSelected != ${BST_CHECKED}
    Return
  ${EndIf}
  Call CheckBundleUnlocked
  ; NSIS uninstallers normally spawn a temporary child and return immediately.
  ; Run a temporary copy with _?= so ExecWait waits for the actual uninstaller,
  ; while the original Uninstall.exe can be removed from the bundle normally.
  InitPluginsDir
  ClearErrors
  CopyFiles /SILENT "$INSTDIR\Uninstall.exe" "$PLUGINSDIR\Profiles-Uninstall.exe"
  ${If} ${Errors}
    MessageBox MB_ICONSTOP "Не удалось подготовить удаление. Закройте AutoCAD и повторите попытку. Для восстановления файла удаления выберите «Исправить»."
    Abort
  ${EndIf}
  ClearErrors
  ExecWait '"$PLUGINSDIR\Profiles-Uninstall.exe" _?=$INSTDIR' $0
  ${If} ${Errors}
    MessageBox MB_ICONSTOP "Не удалось запустить удаление. Выберите «Исправить» для восстановления модуля и повторите попытку."
    Abort
  ${EndIf}
  ReadRegStr $1 HKLM "${UNINSTALL_KEY}" "InstallLocation"
  ${If} $0 == 0
  ${AndIf} $1 == ""
    IfFileExists "$INSTDIR\PackageContents.xml" removal_incomplete
    IfFileExists "$INSTDIR\Contents\2020\Profiles.AutoCAD.dll" removal_incomplete
    IfFileExists "$INSTDIR\Contents\2021\Profiles.AutoCAD.dll" removal_incomplete
    IfFileExists "$INSTDIR\Contents\2022\Profiles.AutoCAD.dll" removal_incomplete
    IfFileExists "$INSTDIR\Contents\2023\Profiles.AutoCAD.dll" removal_incomplete
    IfFileExists "$INSTDIR\Contents\2024\Profiles.AutoCAD.dll" removal_incomplete
    MessageBox MB_ICONINFORMATION "Модуль «Профиля» удалён. Ваши DWG и файлы проектов сохранены."
    ; Removal must never fall through into the installation section.
    SetErrorLevel 0
    Quit
  ${EndIf}
  MessageBox MB_ICONINFORMATION "Удаление не завершено или отменено. Установщик остаётся в режиме обслуживания."
  Abort
  removal_incomplete:
    MessageBox MB_ICONEXCLAMATION "Часть файлов модуля осталась в каталоге установки. Закройте AutoCAD. Для восстановления программы и её удаления выберите «Исправить», затем запустите установщик ещё раз."
    Abort
FunctionEnd

Function CheckRuntime
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  ${If} $0 < 461808
    MessageBox MB_ICONSTOP "Нужен Microsoft .NET Framework 4.7.2 или новее. Установите его штатным установщиком Microsoft, затем повторите установку."
    Abort
  ${EndIf}
FunctionEnd

Section "Профиля" SEC_MAIN
  SetRegView 64
  ; .NET is needed to install or repair; removing the module never requires it.
  Call CheckRuntime
  Call CheckBundleUnlocked
  SetShellVarContext all
  SetOutPath "$INSTDIR"
  SetOverwrite on
  !include "${INSTALL_FILES}"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "Publisher" "Sh0MeM0"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoRepair" 1
  SetErrorLevel 0
SectionEnd

Section "Uninstall"
  SetRegView 64
  Call un.CheckBundleUnlocked
  SetShellVarContext all
  !include "${UNINSTALL_FILES}"
  ; Keep the registration and uninstaller if runtime files could not be
  ; removed. Do not claim success or remove user files to empty the directory.
  IfFileExists "$INSTDIR\PackageContents.xml" uninstall_incomplete
  IfFileExists "$INSTDIR\Contents\2020\Profiles.AutoCAD.dll" uninstall_incomplete
  IfFileExists "$INSTDIR\Contents\2021\Profiles.AutoCAD.dll" uninstall_incomplete
  IfFileExists "$INSTDIR\Contents\2022\Profiles.AutoCAD.dll" uninstall_incomplete
  IfFileExists "$INSTDIR\Contents\2023\Profiles.AutoCAD.dll" uninstall_incomplete
  IfFileExists "$INSTDIR\Contents\2024\Profiles.AutoCAD.dll" uninstall_incomplete
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKLM "${UNINSTALL_KEY}"
  SetErrorLevel 0
  Goto uninstall_done
  uninstall_incomplete:
    MessageBox MB_ICONEXCLAMATION "Не удалось удалить часть файлов модуля. Закройте AutoCAD и повторите удаление. Ваши DWG и файлы проектов не удалялись."
    SetErrorLevel 1
    Abort
  uninstall_done:
SectionEnd


; A mapped AutoCAD assembly cannot be opened for writing. Offer retry without
; terminating AutoCAD or deleting any user's work. Share this preflight between
; repair, maintenance removal and direct Windows uninstall.
!macro BundleLockCheck Prefix
Function ${Prefix}CheckFileUnlocked
  Exch $0
  Push $1
  check_again:
    IfFileExists "$0" 0 check_done
    ClearErrors
    FileOpen $1 "$0" a
    ${If} ${Errors}
      MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "Файл модуля занят или недоступен:$\r$\n$0$\r$\n$\r$\nЗакройте все окна AutoCAD и нажмите «Повтор»." IDRETRY check_again
      Pop $1
      Pop $0
      SetErrorLevel 1
      Abort
    ${EndIf}
    FileClose $1
  check_done:
  Pop $1
  Pop $0
FunctionEnd

Function ${Prefix}CheckBundleUnlocked
  !insertmacro CheckYearFiles "${Prefix}" "2020"
  !insertmacro CheckYearFiles "${Prefix}" "2021"
  !insertmacro CheckYearFiles "${Prefix}" "2022"
  !insertmacro CheckYearFiles "${Prefix}" "2023"
  !insertmacro CheckYearFiles "${Prefix}" "2024"
FunctionEnd
!macroend

!macro CheckYearFiles Prefix Year
  Push "$INSTDIR\Contents\${Year}\Profiles.AutoCAD.dll"
  Call ${Prefix}CheckFileUnlocked
  Push "$INSTDIR\Contents\${Year}\Profiles.Core.dll"
  Call ${Prefix}CheckFileUnlocked
  Push "$INSTDIR\Contents\${Year}\Newtonsoft.Json.dll"
  Call ${Prefix}CheckFileUnlocked
!macroend

!insertmacro BundleLockCheck ""
!insertmacro BundleLockCheck "un."

Function un.MarkUninstallCanceled
  SetErrorLevel 1
FunctionEnd
