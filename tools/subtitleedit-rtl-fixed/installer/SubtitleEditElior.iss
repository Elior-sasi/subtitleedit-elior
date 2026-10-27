#ifndef Payload
  #define Payload "..\..\..\..\subtitleedit\publish\compact"
#endif
#ifndef BuildId
  #define BuildId "0"
#endif
[Setup]
AppId={{A2DD9D28-36CF-4277-B6E1-AC25F3999B61}
AppName=Subtitle Edit — הגרסה של אליאור
AppVersion=5.4.0.8
AppVerName=Subtitle Edit 5.4.0-beta8 — Elior build {#BuildId}
AppPublisher=Elior Sasi / Subtitle Edit contributors
DefaultDirName={localappdata}\Programs\SubtitleEditElior
DefaultGroupName=Subtitle Edit - Elior
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=SubtitleEdit-Elior-5.4.0-beta8-Setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=120
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\SubtitleEdit.exe
DisableProgramGroupPage=yes

[Languages]
Name: "hebrew"; MessagesFile: "compiler:Languages\Hebrew.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "Settings*.json,*.srt,*.ass,*.ssa,*.vtt,*.ostproject,*.sework.json,*.dpapi,custom-installed.json,\CloudMfa\*"

[Icons]
Name: "{group}\Subtitle Edit - Elior"; Filename: "{app}\SubtitleEdit.exe"; WorkingDir: "{app}"; AppUserModelID: "Elior.SubtitleEdit.Hebrew"
Name: "{userdesktop}\Subtitle Edit - Elior"; Filename: "{app}\SubtitleEdit.exe"; WorkingDir: "{app}"; AppUserModelID: "Elior.SubtitleEdit.Hebrew"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "צור קיצור דרך בשולחן העבודה"; Flags: unchecked

[Registry]
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "SubtitleEditElior"; ValueData: "Software\SubtitleEditElior\Capabilities"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\SubtitleEditElior\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "Subtitle Edit — אליאור"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\SubtitleEditElior\Capabilities\FileAssociations"; ValueType: string; ValueName: ".srt"; ValueData: "Elior.SubtitleEdit.Subtitle"
Root: HKCU; Subkey: "Software\SubtitleEditElior\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ass"; ValueData: "Elior.SubtitleEdit.Subtitle"
Root: HKCU; Subkey: "Software\SubtitleEditElior\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ssa"; ValueData: "Elior.SubtitleEdit.Subtitle"
Root: HKCU; Subkey: "Software\SubtitleEditElior\Capabilities\FileAssociations"; ValueType: string; ValueName: ".vtt"; ValueData: "Elior.SubtitleEdit.Subtitle"
Root: HKCU; Subkey: "Software\Classes\Elior.SubtitleEdit.Subtitle"; ValueType: string; ValueData: "Subtitle file — Elior"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Elior.SubtitleEdit.Subtitle"; ValueType: string; ValueName: "AppUserModelID"; ValueData: "Elior.SubtitleEdit.Hebrew"
Root: HKCU; Subkey: "Software\Classes\Elior.SubtitleEdit.Subtitle\DefaultIcon"; ValueType: string; ValueData: "{app}\SubtitleEdit.exe,0"
Root: HKCU; Subkey: "Software\Classes\Elior.SubtitleEdit.Subtitle\shell\open\command"; ValueType: string; ValueData: """{app}\SubtitleEdit.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.srt\OpenWithProgids"; ValueType: string; ValueName: "Elior.SubtitleEdit.Subtitle"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.ass\OpenWithProgids"; ValueType: string; ValueName: "Elior.SubtitleEdit.Subtitle"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.ssa\OpenWithProgids"; ValueType: string; ValueName: "Elior.SubtitleEdit.Subtitle"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.vtt\OpenWithProgids"; ValueType: string; ValueName: "Elior.SubtitleEdit.Subtitle"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\Elior.SubtitleEdit.Hebrew"; ValueType: string; ValueName: "DisplayName"; ValueData: "Subtitle Edit — אליאור"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\Elior.SubtitleEdit.Hebrew"; ValueType: string; ValueName: "RelaunchCommand"; ValueData: """{app}\SubtitleEdit.exe"""
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\Elior.SubtitleEdit.Hebrew"; ValueType: string; ValueName: "RelaunchDisplayNameResource"; ValueData: "Subtitle Edit — אליאור"

[Run]
Filename: "{app}\SubtitleEdit.exe"; Description: "פתח את הגרסה שלנו"; Flags: nowait postinstall skipifsilent

[Code]
var
  SourcePage: TInputDirWizardPage;
procedure InitializeWizard;
begin
  SourcePage := CreateInputDirPage(wpSelectDir, 'שמירת ההגדרות הקיימות',
    'בחר את התיקייה שבה ההגדרות שלך נוחות לך',
    'בחר את תיקיית הגרסה הניידת הקודמת, או את תיקיית ההגדרות. המקור לא יימחק. אם כבר הותקנה הגרסה שלנו, ההגדרות שלה יישמרו. אין צורך לחלץ ZIP בעדכונים הבאים.', False, '');
  SourcePage.Add('התיקייה שמכילה Settings.json:');
  SourcePage.Values[0] := ExpandConstant('{param:SOURCESETTINGS|{userappdata}\Subtitle Edit}');
end;
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = SourcePage.ID) and (SourcePage.Values[0] <> '') and
     not FileExists(AddBackslash(SourcePage.Values[0]) + 'Settings.json') then
  begin
    MsgBox('לא נמצא Settings.json בתיקייה שבחרת. בחר תיקייה אחרת, או נקה את השדה כדי להתחיל עם הגדרות חדשות.', mbError, MB_OK);
    Result := False;
  end;
end;
procedure CopyUserFolder(Source, Target: String);
var FindRec: TFindRec;
begin
  if not DirExists(Source) then Exit;
  ForceDirectories(Target);
  if FindFirst(AddBackslash(Source) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') and ((FindRec.Attributes and $400) = 0) then
        begin
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            CopyUserFolder(AddBackslash(Source) + FindRec.Name, AddBackslash(Target) + FindRec.Name)
          else if not FileExists(AddBackslash(Target) + FindRec.Name) then
            if not FileCopy(AddBackslash(Source) + FindRec.Name, AddBackslash(Target) + FindRec.Name, True) then
              RaiseException('לא ניתן להעתיק נתוני משתמש. המקור נשמר.');
        end;
      until not FindNext(FindRec);
    finally FindClose(FindRec); end;
  end;
end;
procedure CurStepChanged(CurStep: TSetupStep);
var Source, Target, Settings: String;
begin
  if CurStep <> ssPostInstall then Exit;
  Source := SourcePage.Values[0];
  Target := ExpandConstant('{userappdata}\Subtitle Edit Elior');
  ForceDirectories(Target);
  Settings := AddBackslash(Target) + 'Settings.json';
  if (Source <> '') and not FileExists(Settings) and FileExists(AddBackslash(Source) + 'Settings.json') then
  begin
    if not FileCopy(AddBackslash(Source) + 'Settings.json', Settings, True) then
      RaiseException('העתקת ההגדרות נכשלה. ההגדרות המקוריות נשמרו.');
    CopyUserFolder(AddBackslash(Source) + 'CustomProjects', AddBackslash(Target) + 'CustomProjects');
    CopyUserFolder(AddBackslash(Source) + 'Dictionaries', AddBackslash(Target) + 'Dictionaries');
    CopyUserFolder(AddBackslash(Source) + 'Themes', AddBackslash(Target) + 'Themes');
    CopyUserFolder(AddBackslash(Source) + 'Fonts', AddBackslash(Target) + 'Fonts');
    CopyUserFolder(AddBackslash(Source) + 'AutoBackup', AddBackslash(Target) + 'AutoBackup');
    CopyUserFolder(AddBackslash(Source) + 'SettingsBackups', AddBackslash(Target) + 'SettingsBackups');
  end;
  if not SaveStringToFile(ExpandConstant('{app}\custom-installed.json'), '{"channel":"Elior-sasi/subtitleedit-elior/main"}', False) then
    RaiseException('לא ניתן להשלים את סימון ההתקנה.');
end;

