; Inno Setup 6 Script for WinGet GUI
; Independent Desktop Setup with Embedded Offline Dependency Bundles (.NET 8.0 & VC++ Redistributable)

#define MyAppName "WinGet GUI"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "NSP-MO"
#define MyAppURL "https://github.com/NSP-MO/WinGet-GUI"
#define MyAppExeName "WingetGui.exe"

[Setup]
AppId={{9B6F35C8-37A4-4ED7-A6E5-9F7C208E2061}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes

SetupIconFile=..\assets\app.ico
OutputDir=output
OutputBaseFilename=WinGet_GUI_Setup_v1.0.0_x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin

UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Main Application Payload
Source: "..\publish_fdd\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Embedded Offline Dependencies (Extracted to {tmp} and deleted automatically after installation)
Source: "redist\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: not IsVCRedistInstalled
Source: "redist\windowsdesktop-runtime-8.0-win-x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: not IsDotNet8DesktopRuntimeInstalled

[Icons]
Name: "{autoprograms}\{#MyAppName}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autoprograms}\{#MyAppName}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Silent installation of Microsoft Visual C++ Redistributable (if not already installed)
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; Check: not IsVCRedistInstalled; StatusMsg: "Installing Microsoft Visual C++ 2015-2022 Redistributable..."; Flags: waituntilterminated

; Silent installation of Microsoft .NET 8.0 Desktop Runtime (if not already installed)
Filename: "{tmp}\windowsdesktop-runtime-8.0-win-x64.exe"; Parameters: "/install /quiet /norestart"; Check: not IsDotNet8DesktopRuntimeInstalled; StatusMsg: "Installing Microsoft .NET 8.0 Desktop Runtime..."; Flags: waituntilterminated

; Launch WinGet GUI after setup completes (runascurrentuser ensures Setup's elevated credentials are used)
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// Function to check if Microsoft Visual C++ 2015-2022 Redistributable (x64) is installed
function IsVCRedistInstalled(): Boolean;
var
  InstalledVal: Cardinal;
  MajorVal: Cardinal;
begin
  Result := False;

  // 1. Check 64-bit Registry Key
  if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', InstalledVal) then
  begin
    if InstalledVal = 1 then
    begin
      if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Major', MajorVal) then
      begin
        if MajorVal >= 14 then
        begin
          Result := True;
          Exit;
        end;
      end
      else
      begin
        Result := True;
        Exit;
      end;
    end;
  end;

  // 2. Fallback check for runtime DLL presence in System32
  if FileExists(ExpandConstant('{sys}\vcruntime140.dll')) and FileExists(ExpandConstant('{sys}\msvcp140.dll')) then
  begin
    Result := True;
    Exit;
  end;
end;

// Function to check if Microsoft .NET 8.0 Desktop Runtime is installed
function IsDotNet8DesktopRuntimeInstalled(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  FindRec: TFindRec;
  SharedPath: string;
begin
  Result := False;

  // 1. Check 64-bit Registry Key
  if RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if (Pos('8.0.', Names[I]) = 1) or (Names[I] = '8.0') or (Pos('8.', Names[I]) = 1) then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;

  // 2. Check 32-bit Registry Key
  if RegGetValueNames(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if (Pos('8.0.', Names[I]) = 1) or (Names[I] = '8.0') or (Pos('8.', Names[I]) = 1) then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;

  // 3. Check 64-bit Filesystem Directory: Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.*
  SharedPath := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if DirExists(SharedPath) then
  begin
    if FindFirst(SharedPath + '\8.*', FindRec) then
    begin
      try
        Result := True;
        Exit;
      finally
        FindClose(FindRec);
      end;
    end;
  end;

  // 4. Check 32-bit Filesystem Directory: Program Files (x86)\dotnet\shared\Microsoft.WindowsDesktop.App\8.*
  SharedPath := ExpandConstant('{commonpf32}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if DirExists(SharedPath) then
  begin
    if FindFirst(SharedPath + '\8.*', FindRec) then
    begin
      try
        Result := True;
        Exit;
      finally
        FindClose(FindRec);
      end;
    end;
  end;
end;
