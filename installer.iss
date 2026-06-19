; ============================================================
;  Inno Setup Script -- Kartogramma Plugin v{#AppVersion}
;  Build:  .\build-installer.ps1 [-Version "1.1.1"]
;  Output: dist\Setup_KartogrammaPlugin_v*.exe
; ============================================================

#ifndef AppVersion
  #define AppVersion "1.1.1"
#endif

#define AppName      "Kartogramma zemlyanyh rabot"
#define AppNameRu    "Картограмма земляных работ"
#define AppPublisher "OPG Zaycsev"
#define AppURL       "https://github.com/zaycsev/openkartogramma"
#define BundleName   "KartogrammaPlugin.bundle"
#define BundleSrc    "dist\bundle"
#define InstDir      "{userappdata}\Autodesk\ApplicationPlugins\KartogrammaPlugin.bundle"

; ============================================================
[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#AppNameRu}
AppVersion={#AppVersion}
AppVerName={#AppNameRu} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}

DefaultDirName={#InstDir}
DisableDirPage=yes
DisableProgramGroupPage=yes
CreateUninstallRegKey=yes

PrivilegesRequired=lowest

OutputDir=dist
OutputBaseFilename=Setup_openkartogramma_v{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes

SetupIconFile=dist\assets\setup.ico
WizardImageFile=dist\assets\wizard_side.png
WizardSmallImageFile=dist\assets\wizard_small.png

WizardStyle=modern
WizardResizable=no
DisableWelcomePage=no

; ============================================================
[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

; ============================================================
[CustomMessages]
russian.WelcomeLabel2=Этот мастер установит плагин {#AppNameRu} v{#AppVersion}.%n%nПлагин добавляет команду OpenKartogramma в AutoCAD Civil 3D.%n%nПоддерживаемые версии:%n  • Civil 3D 2015–2024  (.NET Framework 4.8)%n  • Civil 3D 2025–2027  (.NET 8)%n%nПуть установки:%n  %APPDATA%\Autodesk\ApplicationPlugins\{#BundleName}%n%n— Закройте AutoCAD Civil 3D перед продолжением. —
russian.FinishedLabel=Установка {#AppNameRu} v{#AppVersion} завершена!%n%nПлагин будет загружен автоматически при следующем запуске Civil 3D.%n%nКоманда для запуска:%n%n    OpenKartogramma

; ============================================================
[Dirs]
Name: "{#InstDir}"
Name: "{#InstDir}\Contents"
Name: "{#InstDir}\Contents\net48"
Name: "{#InstDir}\Contents\net8"

; ============================================================
[Files]
Source: "{#BundleSrc}\PackageContents.xml"; DestDir: "{#InstDir}"; Flags: ignoreversion
Source: "{#BundleSrc}\Contents\net48\openkartogramma.dll"; DestDir: "{#InstDir}\Contents\net48"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#BundleSrc}\Contents\net8\openkartogramma.dll"; DestDir: "{#InstDir}\Contents\net8"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#BundleSrc}\Contents\net8\openkartogramma.deps.json"; DestDir: "{#InstDir}\Contents\net8"; Flags: ignoreversion skipifsourcedoesntexist

; ============================================================
[UninstallDelete]
; Рекурсивно пытаемся удалить всю папку плагина (вместе с логами и любыми
; побочными файлами, которые могли появиться после установки).
Type: filesandordirs; Name: "{app}"

; ============================================================
[Code]

function InitializeSetup(): Boolean;
var
  msg: string;
begin
  Result := True;
  if not (
    DirExists('C:\Program Files\Autodesk\AutoCAD 2027') or
    DirExists('C:\Program Files\Autodesk\AutoCAD 2026') or
    DirExists('C:\Program Files\Autodesk\AutoCAD 2025') or
    DirExists('C:\Program Files\Autodesk\AutoCAD 2024') or
    DirExists('C:\Program Files\Autodesk\AutoCAD 2023') or
    DirExists('C:\Program Files\Autodesk\AutoCAD 2022') or
    DirExists('C:\Program Files\Autodesk\AutoCAD 2021') or
    DirExists('C:\Program Files\Autodesk\AutoCAD 2020') or
    DirExists('C:\Program Files\Autodesk\AutoCAD 2019') or
    DirExists('C:\Program Files\Autodesk\AutoCAD 2018') or
    DirExists('C:\Program Files\Autodesk\AutoCAD 2017') or
    DirExists('C:\Program Files\Autodesk\AutoCAD 2016') or
    DirExists('C:\Program Files\Autodesk\AutoCAD 2015')
  ) then
  begin
    msg := 'AutoCAD Civil 3D не обнаружен на этом компьютере.' + #13#10 +
           'Плагин предназначен для AutoCAD Civil 3D 2015–2027.' + #13#10#13#10 +
           'Продолжить установку несмотря на это?';
    Result := (MsgBox(msg, mbConfirmation, MB_YESNO) = IDYES);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  wbem, procs: Variant;
  i: Integer;
  found: Boolean;
begin
  Result := '';
  found  := False;
  try
    wbem  := CreateOleObject('WbemScripting.SWbemLocator');
    procs := wbem.ConnectServer('', 'root\cimv2')
                 .ExecQuery('SELECT * FROM Win32_Process WHERE Name="acad.exe"');
    for i := 0 to procs.Count - 1 do
      found := True;
  except
  end;
  if found then
    Result := 'AutoCAD Civil 3D сейчас запущен.' + #13#10 +
              'Закройте программу и нажмите "Повторить".';
end;

// ------------------------------------------------------------
// Пост-обработка удаления: если после деинсталляции в папке плагина
// остались файлы (например, потому что AutoCAD держал DLL открытым),
// показываем пользователю полные пути и предлагаем открыть папку.

// Файлы самого деинсталлятора (unins000.exe и т.п.) во время usPostUninstall
// ещё присутствуют в {app} — они удаляют себя сами, их не считаем.
function IsUninstallerFile(const Name: string): Boolean;
var
  L, Ext: string;
begin
  L := Lowercase(Name);
  if Copy(L, 1, 5) <> 'unins' then
  begin
    Result := False;
    Exit;
  end;
  Ext := Lowercase(ExtractFileExt(Name));
  Result := (Ext = '.exe') or (Ext = '.dat') or
            (Ext = '.msg') or (Ext = '.lst');
end;

procedure CollectRemainingFiles(const Dir: string; List: TStringList);
var
  FindRec: TFindRec;
  FullPath: string;
begin
  if FindFirst(Dir + '\*', FindRec) then
  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') and
         (not IsUninstallerFile(FindRec.Name)) then
      begin
        FullPath := Dir + '\' + FindRec.Name;
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          CollectRemainingFiles(FullPath, List)
        else
          List.Add(FullPath);
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  InstallPath: string;
  Remaining: TStringList;
  Msg: string;
  i, Shown, Total, ErrCode: Integer;
begin
  if CurUninstallStep <> usPostUninstall then Exit;

  InstallPath := ExpandConstant('{app}');
  if not DirExists(InstallPath) then Exit;

  Remaining := TStringList.Create;
  try
    CollectRemainingFiles(InstallPath, Remaining);
    Total := Remaining.Count;

    if Total = 0 then
    begin
      // Папка пустая — удаляем и выходим молча.
      RemoveDir(InstallPath);
      Exit;
    end;

    Msg := 'Не все файлы плагина удалось удалить автоматически.' + #13#10 +
           '(обычно это происходит, если AutoCAD Civil 3D был запущен' + #13#10 +
           ' во время удаления и удерживал файлы плагина).' + #13#10#13#10 +
           'Оставшиеся файлы (' + IntToStr(Total) + ' шт.):' + #13#10;

    Shown := Total;
    if Shown > 20 then Shown := 20;
    for i := 0 to Shown - 1 do
      Msg := Msg + '  ' + Remaining[i] + #13#10;
    if Total > Shown then
      Msg := Msg + '  ... и ещё ' + IntToStr(Total - Shown) + ' файл(ов)' + #13#10;

    Msg := Msg + #13#10 + 'Папка плагина:' + #13#10 +
           '  ' + InstallPath + #13#10#13#10 +
           'Закройте AutoCAD Civil 3D и удалите оставшиеся файлы вручную.' + #13#10#13#10 +
           'Открыть эту папку в проводнике сейчас?';

    if MsgBox(Msg, mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('', 'explorer.exe', '"' + InstallPath + '"', '',
                SW_SHOW, ewNoWait, ErrCode);
  finally
    Remaining.Free;
  end;
end;
