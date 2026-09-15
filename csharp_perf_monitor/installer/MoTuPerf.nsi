Unicode true

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

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

!define APP_ID "D8C3D239-3D4E-4EC8-901D-1D9D75B593E0"
!define APP_EXE "CSharpIosPerfMonitor.exe"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}"

Name "MoTuPerf ${APP_VERSION}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\MoTuPerf"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
Icon "${SETUP_ICON}"
UninstallIcon "${SETUP_ICON}"
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "${APP_FILE_VERSION}"
VIAddVersionKey /LANG=2052 "ProductName" "MoTuPerf"
VIAddVersionKey /LANG=2052 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "FileVersion" "${APP_FILE_VERSION}"
VIAddVersionKey /LANG=2052 "CompanyName" "Motutest"
VIAddVersionKey /LANG=2052 "FileDescription" "MoTuPerf iOS and Android performance monitor installer"
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
  ${If} ${RunningX64}
  ${Else}
    MessageBox MB_ICONSTOP|MB_OK "MoTuPerf 仅支持 64 位 Windows。"
    Abort
  ${EndIf}
FunctionEnd

Function StopInstalledProcesses
  InitPluginsDir
  File /oname=$PLUGINSDIR\StopInstalledProcesses.ps1 "${PROCESS_HELPER}"

  nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\StopInstalledProcesses.ps1" -InstallDirectory "$INSTDIR" -DetectOnly'
  Pop $0
  Pop $1
  StrCmp $0 "0" stop_processes_done
  StrCmp $0 "10" stop_processes_confirm stop_processes_check_failed

stop_processes_confirm:
  MessageBox MB_ICONQUESTION|MB_YESNO "检测到旧版 MoTuPerf 或其采集进程仍在运行。$\r$\n$\r$\n继续安装会自动关闭这些进程，请先确认当前采集数据已经保存。是否继续？" /SD IDYES IDYES stop_processes_run
  SetErrorLevel 20
  Quit

stop_processes_run:
  nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\StopInstalledProcesses.ps1" -InstallDirectory "$INSTDIR"'
  Pop $0
  Pop $1
  StrCmp $0 "0" stop_processes_done
  MessageBox MB_ICONSTOP|MB_OK "无法关闭旧版 MoTuPerf 的后台进程。$\r$\n$\r$\n请关闭 MoTuPerf 后在任务管理器中结束安装目录内的 adb.exe 或 python.exe，然后重新安装。" /SD IDOK
  SetErrorLevel 21
  Quit

stop_processes_check_failed:
  MessageBox MB_ICONSTOP|MB_OK "无法检查旧版 MoTuPerf 的后台进程，安装已停止，避免覆盖失败。$\r$\n$\r$\n请关闭 MoTuPerf 后重新运行安装包。" /SD IDOK
  SetErrorLevel 22
  Quit

stop_processes_done:
FunctionEnd

Section "MoTuPerf" SEC_MAIN
  SectionIn RO
  Call StopInstalledProcesses
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  File /r "${SOURCE_DIR}\*.*"

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\MoTuPerf"
  CreateShortcut "$SMPROGRAMS\MoTuPerf\MoTuPerf.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\motu-shortcut-icon.ico" 0
  CreateShortcut "$DESKTOP\MoTuPerf.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\motu-shortcut-icon.ico" 0

  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "MoTuPerf ${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "Motutest"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1

  IfSilent driver_check_done
  Call AppleMobileDeviceSupportInstalled
  StrCmp $0 "1" driver_check_done
  MessageBox MB_ICONINFORMATION|MB_YESNO \
    "未检测到 Apple 移动设备驱动。$\r$\n$\r$\nMoTuPerf 已安装完成，但 Windows 还需要 Apple 官方 USB 驱动才能识别 iPhone/iPad。$\r$\n$\r$\n是否现在打开 Microsoft Store 安装“Apple 设备”？" \
    IDNO driver_check_done
  ExecShell "open" "ms-windows-store://pdp/?ProductId=9NP83LWLPZ9K"

driver_check_done:
SectionEnd

Function AppleMobileDeviceSupportInstalled
  StrCpy $0 "0"
  SetRegView 64
  ClearErrors
  ReadRegStr $1 HKLM "SYSTEM\CurrentControlSet\Services\Apple Mobile Device Service" "ImagePath"
  IfErrors check_driver_files driver_found

check_driver_files:
  ${If} ${RunningX64}
    IfFileExists "$PROGRAMFILES64\Apple\Mobile Device Support\AppleMobileDeviceService.exe" driver_found
  ${EndIf}
  IfFileExists "$PROGRAMFILES32\Apple\Mobile Device Support\AppleMobileDeviceService.exe" driver_found driver_check_end

driver_found:
  StrCpy $0 "1"

driver_check_end:
  SetRegView 32
FunctionEnd

Section "Uninstall"
  SetShellVarContext current
  Delete "$DESKTOP\MoTuPerf.lnk"
  Delete "$SMPROGRAMS\MoTuPerf\MoTuPerf.lnk"
  RMDir "$SMPROGRAMS\MoTuPerf"

  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\README.txt"
  Delete "$INSTDIR\motu-shortcut-icon.ico"
  Delete "$INSTDIR\motuperf-package.json"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir /r "$INSTDIR\tools"
  RMDir /r "$INSTDIR\runtime"
  RMDir "$INSTDIR"

  DeleteRegKey HKCU "${UNINSTALL_KEY}"
SectionEnd
