Unicode True
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

!ifndef PAYLOAD
  !error "Pass /DPAYLOAD=absolute-bundle-directory"
!endif
!ifndef OUTPUT
  !error "Pass /DOUTPUT=installer-path"
!endif
!define PRODUCT "Профиля для AutoCAD 2020–2024"
!define VERSION "0.1.1"
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
VIProductVersion "0.1.1.0"
VIAddVersionKey /LANG=1049 "ProductName" "${PRODUCT}"
VIAddVersionKey /LANG=1049 "FileDescription" "Установка модуля Профиля"
VIAddVersionKey /LANG=1049 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1049 "LegalCopyright" "Sh0MeM0, 2026"

!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "Профиля для AutoCAD 2020–2024"
!define MUI_WELCOMEPAGE_TEXT "Модуль строит продольные и поперечные профили каналов и прудов.$\r$\n$\r$\nПеред установкой закройте все окна AutoCAD. После установки запустите AutoCAD заново: появится верхнее меню «Профиля»; при включённой ленте доступна и вкладка.$\r$\n$\r$\nВерсия 0.1.1: расчётное ядро проверено автоматическими тестами; проверку загрузки в вашей версии AutoCAD выполните по инструкции."
!insertmacro MUI_PAGE_WELCOME
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
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  ${If} $0 < 461808
    MessageBox MB_ICONSTOP "Нужен Microsoft .NET Framework 4.7.2 или новее. Установите его штатным установщиком Microsoft, затем повторите установку."
    Abort
  ${EndIf}
FunctionEnd

Section "Профиля" SEC_MAIN
  SetRegView 64
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
SectionEnd

Section "Uninstall"
  SetRegView 64
  SetShellVarContext all
  !include "${UNINSTALL_FILES}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKLM "${UNINSTALL_KEY}"
SectionEnd
