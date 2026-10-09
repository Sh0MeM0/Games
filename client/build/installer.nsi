; Custom supported NSIS installer. WriteUninstaller executes on Windows at install time.
; This avoids executing an intermediate 32-bit Windows installer on the Linux build machine.
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

Name "${PRODUCT_NAME}"
InstallDir "$LOCALAPPDATA\Programs\SharedArchive"
RequestExecutionLevel user
ShowInstDetails show
ShowUninstDetails show
!define REGKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\SharedArchive"
!define MUI_ABORTWARNING
!define MUI_DIRECTORYPAGE_TEXT_TOP "Выберите пустую папку или папку ранее установленного архива. Перед обновлением закройте приложение."
!define MUI_FINISHPAGE_RUN "$INSTDIR\${PRODUCT_FILENAME}.exe"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"

Function .onInit
  SetShellVarContext current
  SetRegView 64
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "Приложение предназначено для Windows 10/11 x64."
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "Требуется Windows 10 или Windows 11."
    Abort
  ${EndIf}
  ReadRegStr $0 HKCU "${REGKEY}" "InstallLocation"
  ${If} $0 != ""
    StrCpy $INSTDIR $0
  ${EndIf}
FunctionEnd

Function .onVerifyInstDir
  ; Prevent installation/uninstallation over an unrelated directory with existing files.
  IfFileExists "$INSTDIR\*.*" 0 valid_directory
  IfFileExists "$INSTDIR\.shared-archive-install" valid_directory 0
  Abort
  valid_directory:
FunctionEnd

Section "Установка"
  SetShellVarContext current
  SetRegView 64
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=application.zip "${APP_64}"
  SetOutPath "$INSTDIR"
  nsisunz::Unzip "$PLUGINSDIR\application.zip" "$INSTDIR"
  Pop $0
  ${If} $0 != "success"
    MessageBox MB_OK|MB_ICONSTOP "Не удалось распаковать приложение. Закройте архив и проверьте свободное место и права на папку."
    Abort
  ${EndIf}
  FileOpen $0 "$INSTDIR\.shared-archive-install" w
  FileWrite $0 "SharedArchive ${VERSION}"
  FileClose $0
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
  CreateShortcut "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk" "$INSTDIR\${PRODUCT_FILENAME}.exe"
  CreateShortcut "$DESKTOP\${PRODUCT_NAME}.lnk" "$INSTDIR\${PRODUCT_FILENAME}.exe"
  WriteRegStr HKCU "${REGKEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${REGKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${REGKEY}" "Publisher" "Shared Archive"
  WriteRegStr HKCU "${REGKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${REGKEY}" "DisplayIcon" "$INSTDIR\${PRODUCT_FILENAME}.exe"
  WriteRegStr HKCU "${REGKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${REGKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${REGKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${REGKEY}" "NoRepair" 1
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  IfFileExists "$INSTDIR\.shared-archive-install" valid_uninstall 0
  MessageBox MB_OK|MB_ICONSTOP "Не найдена установленная программа. Удаление отменено."
  Abort
  valid_uninstall:
FunctionEnd

Section "Uninstall"
  SetShellVarContext current
  SetRegView 64
  Delete "$DESKTOP\${PRODUCT_NAME}.lnk"
  RMDir /r "$SMPROGRAMS\${PRODUCT_NAME}"
  RMDir /r "$INSTDIR"
  IfFileExists "$INSTDIR\${PRODUCT_FILENAME}.exe" 0 uninstall_complete
  MessageBox MB_OK|MB_ICONEXCLAMATION "Программа ещё работает. Закройте её и повторите удаление."
  Abort
  uninstall_complete:
  DeleteRegKey HKCU "${REGKEY}"
  ; Deliberately preserve AppData, including unsent working copies and protected credentials.
SectionEnd
