#ifndef MyAppVersion
  #error MyAppVersion is required
#endif
#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef OutputBaseFilename
  #error OutputBaseFilename is required
#endif
#ifndef SetupIcon
  #error SetupIcon is required
#endif

[Setup]
AppId={{D8C3D239-3D4E-4EC8-901D-1D9D75B593E0}
AppName=MoTuPerf
AppVersion={#MyAppVersion}
AppVerName=MoTuPerf {#MyAppVersion}
AppPublisher=Motutest
DefaultDirName={localappdata}\Programs\MoTuPerf
DefaultGroupName=MoTuPerf
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\CSharpIosPerfMonitor.exe
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile={#SetupIcon}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName=MoTuPerf
VersionInfoDescription=MoTuPerf iOS and Android performance monitor installer
VersionInfoCompany=Motutest

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\MoTuPerf"; Filename: "{app}\CSharpIosPerfMonitor.exe"
Name: "{autodesktop}\MoTuPerf"; Filename: "{app}\CSharpIosPerfMonitor.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CSharpIosPerfMonitor.exe"; Description: "启动 MoTuPerf"; Flags: nowait postinstall skipifsilent

[Code]
function AppleMobileDeviceSupportInstalled: Boolean;
begin
  Result :=
    RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\Apple Mobile Device Service') or
    FileExists(ExpandConstant('{commonpf}\Apple\Mobile Device Support\AppleMobileDeviceService.exe')) or
    FileExists(ExpandConstant('{commonpf32}\Apple\Mobile Device Support\AppleMobileDeviceService.exe'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorCode: Integer;
begin
  if (CurStep = ssPostInstall) and (not WizardSilent) and (not AppleMobileDeviceSupportInstalled) then
  begin
    if MsgBox(
      '未检测到 Apple 移动设备驱动。' + #13#10 + #13#10 +
      'MoTuPerf 的程序和 iOS 采集运行环境已经安装完成，但 Windows 还需要 Apple 官方 USB 驱动才能识别 iPhone/iPad。' + #13#10 + #13#10 +
      '是否现在打开 Microsoft Store 安装“Apple 设备”？',
      mbInformation,
      MB_YESNO) = IDYES then
    begin
      ShellExec('', 'ms-windows-store://pdp/?ProductId=9NP83LWLPZ9K', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    end;
  end;
end;
