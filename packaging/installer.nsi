Unicode True
Target amd64-unicode
!include "MUI2.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

!ifndef PAYLOAD
  !error "Pass /DPAYLOAD=<portable directory>"
!endif
!ifndef OUTPUT
  !define OUTPUT "PDFMaster-Setup-x64.exe"
!endif
Name "PDF Мастер"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\PDFMaster"
InstallDirRegKey HKCU "Software\PDFMaster" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 32
VIProductVersion "0.1.0.0"
VIAddVersionKey /LANG=1049 "ProductName" "PDF Мастер"
VIAddVersionKey /LANG=1049 "FileDescription" "Установка PDF Мастер"
VIAddVersionKey /LANG=1049 "FileVersion" "0.1.0"
VIAddVersionKey /LANG=1049 "LegalCopyright" "PDF Master contributors"

!define MUI_ABORTWARNING
!define MUI_ICON "${PAYLOAD}/assets/app.ico"
!define MUI_UNICON "${PAYLOAD}/assets/app.ico"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${PAYLOAD}/LICENSE"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\PDFMaster.exe"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "PDF Мастер требует Windows 10/11, 64 бита."
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP "PDF Мастер требует Windows 10 или новее."
    Abort
  ${EndIf}
FunctionEnd

Section "PDF Мастер" SEC_MAIN
  SetOutPath "$INSTDIR"
  File /r "${PAYLOAD}/*"
  Delete "$INSTDIR\portable.flag"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\PDF Мастер"
  CreateShortcut "$SMPROGRAMS\PDF Мастер\PDF Мастер.lnk" "$INSTDIR\PDFMaster.exe"
  CreateShortcut "$SMPROGRAMS\PDF Мастер\Удалить.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\PDF Мастер.lnk" "$INSTDIR\PDFMaster.exe"
  WriteRegStr HKCU "Software\PDFMaster" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PDFMaster" "DisplayName" "PDF Мастер"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PDFMaster" "DisplayVersion" "0.1.0"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PDFMaster" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PDFMaster" "DisplayIcon" "$INSTDIR\PDFMaster.exe"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PDFMaster" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PDFMaster" "NoRepair" 1
SectionEnd

Section "Uninstall"
  Delete "$DESKTOP\PDF Мастер.lnk"
  Delete "$SMPROGRAMS\PDF Мастер\PDF Мастер.lnk"
  Delete "$SMPROGRAMS\PDF Мастер\Удалить.lnk"
  RMDir "$SMPROGRAMS\PDF Мастер"
  ; Generated exact manifest removes only packaged files, preserving user documents.
  !include "${PAYLOAD}/uninstall-files.nsh"
  Delete "$INSTDIR\uninstall-files.nsh"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKCU "Software\PDFMaster"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PDFMaster"
SectionEnd
