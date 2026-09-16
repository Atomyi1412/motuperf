!ifndef APP_VERSION
  !error "APP_VERSION is required"
!endif
!ifndef APP_FILE_VERSION
  !error "APP_FILE_VERSION is required"
!endif
!ifndef SOURCE_DIR
  !error "SOURCE_DIR is required"
!endif
!ifndef OUTPUT_FILE
  !error "OUTPUT_FILE is required"
!endif
!ifndef SETUP_ICON
  !error "SETUP_ICON is required"
!endif
!ifndef PROCESS_HELPER
  !error "PROCESS_HELPER is required"
!endif
!ifndef DIRECTORY_HELPER
  !error "DIRECTORY_HELPER is required"
!endif

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"

!define APP_ID "5B2B9159-531B-4A3B-8F04-55E5D97D54D9"
!define APP_EXE "MoTuPerf.CrossPlatform.exe"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}"
Var TestMode
Name "MoTuPerf ${APP_VERSION}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\MoTuPerf-CrossPlatform"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
Icon "${SETUP_ICON}"
UninstallIcon "${SETUP_ICON}"
VIProductVersion "${APP_FILE_VERSION}"
VIAddVersionKey /LANG=2052 "ProductName" "MoTuPerf"
VIAddVersionKey /LANG=2052 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "FileVersion" "${APP_FILE_VERSION}"
VIAddVersionKey /LANG=2052 "CompanyName" "Motutest"
VIAddVersionKey /LANG=2052 "FileDescription" "MoTuPerf cross-platform performance monitor installer"
VIAddVersionKey /LANG=2052 "LegalCopyright" "Copyright Motutest"
!define MUI_ABORTWARNING
!define MUI_ICON "${SETUP_ICON}"
!define MUI_UNICON "${SETUP_ICON}"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "启动 MoTuPerf"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Function .onInit
  StrCpy $TestMode "0"
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/TESTMODE" $1
  ${IfNot} ${Errors}
    StrCpy $TestMode "1"
  ${EndIf}
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP|MB_OK "MoTuPerf 仅支持 64 位 Windows。"
    Abort
  ${EndIf}
  ClearErrors
  ; NSIS removes /D from $CMDLINE; inspect the original process command line.
  System::Call 'kernel32::GetCommandLineW() w .r3'
  ${GetOptions} $3 "/D=" $1
  ${IfNot} ${Errors}
    Return
  ${EndIf}
  ${If} $TestMode == "1"
    SetErrorLevel 4
    Abort
  ${EndIf}
  InitPluginsDir
  File /oname=$PLUGINSDIR\ResolveInstallDirectory.ps1 "${DIRECTORY_HELPER}"
  System::Call 'kernel32::GetCurrentProcessId() i .r2'
  nsExec::ExecToStack /TIMEOUT=10000 '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\ResolveInstallDirectory.ps1" -InstallerProcessId $2 -OutputPath "$PLUGINSDIR\upgrade-directory.ini"'
  Pop $0
  Pop $1
  ${If} $0 == "0"
    ReadINIStr $1 "$PLUGINSDIR\upgrade-directory.ini" "upgrade" "directory"
    ${If} $1 != ""
      StrCpy $INSTDIR $1
    ${EndIf}
  ${EndIf}
FunctionEnd

Function StopInstalledProcesses
  InitPluginsDir
  File /oname=$PLUGINSDIR\StopInstalledProcesses.ps1 "${PROCESS_HELPER}"
  nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\StopInstalledProcesses.ps1" -InstallDirectory "$INSTDIR"'
  Pop $0
  Pop $1
  StrCmp $0 "0" done
  MessageBox MB_ICONSTOP|MB_OK "无法关闭正在运行的新版 MoTuPerf，请先结束采集后重试。"
  Abort
done:
FunctionEnd

Section "MoTuPerf" SEC_MAIN
  SectionIn RO
  Call StopInstalledProcesses
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  File /r "${SOURCE_DIR}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  ${If} $TestMode == "1"
    FileOpen $0 "$INSTDIR\.installer-test-mode" w
    FileWrite $0 "test"
    FileClose $0
    Goto install_metadata_done
  ${EndIf}
  CreateDirectory "$SMPROGRAMS\MoTuPerf"
  CreateShortcut "$SMPROGRAMS\MoTuPerf\MoTuPerf.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\motu-shortcut-icon.ico" 0
  CreateShortcut "$DESKTOP\MoTuPerf.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\motu-shortcut-icon.ico" 0
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "MoTuPerf ${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "Motutest"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
install_metadata_done:
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  IfFileExists "$INSTDIR\.installer-test-mode" test_uninstall normal_uninstall
normal_uninstall:
  Delete "$DESKTOP\MoTuPerf.lnk"
  Delete "$SMPROGRAMS\MoTuPerf\MoTuPerf.lnk"
  RMDir "$SMPROGRAMS\MoTuPerf"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
test_uninstall:
  Delete "$INSTDIR\Uninstall.exe"
  RMDir /r "$INSTDIR"
SectionEnd
