; Обёртка поставляет зависимости; установкой/удалением/обновлением программы «Заявки» владеет Velopack.
#ifndef JournalVersion
  #error JournalVersion is required
#endif

[Setup]
AppId=DenisPapusha.JournalWorkflow.OfflineBootstrap
AppName=Заявки
AppVersion={#JournalVersion}
AppPublisher=DenisPapusha
AppPublisherURL=https://github.com/DenisPapushaJava/journal-next
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
MinVersion=10.0.19045
SetupMutex=DenisPapusha.JournalWorkflow.OfflineSetup
SetupLogging=yes
AppMutex=Local\DenisPapusha.JournalWorkflow.Running
OutputBaseFilename=JournalNext-{#JournalVersion}-win-x64-OfflineSetup
SetupIconFile=..\..\docs\design\assets\journal-next-icon.ico
WizardStyle=modern
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
Compression=lzma2
SolidCompression=yes
AllowCancelDuringInstall=no
RestartIfNeededByRun=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Messages]
ReadyLabel1=Всё готово к установке программы «Заявки»
ReadyLabel2a=Нужные компоненты уже включены в установщик. Интернет для установки не требуется.%n%nСуществующая база, старая программа и Office сохраняются. При необходимости Windows запросит разрешение установить среду .NET.
FinishedLabel=Программа «Заявки» установлена. Настройте профиль и папку проектов через «Настройки».

[Files]
Source: "{#RuntimeX64}"; DestDir: "{tmp}"; DestName: "runtime-x64.exe"; Flags: dontcopy
Source: "{#RuntimeX86}"; DestDir: "{tmp}"; DestName: "runtime-x86.exe"; Flags: dontcopy
Source: "{#AccessHost}\*"; DestDir: "{tmp}\AccessHost"; Flags: dontcopy recursesubdirs
Source: "{#AppSetup}"; DestDir: "{tmp}"; DestName: "Заявки-AppSetup.exe"; Flags: dontcopy

[Run]
Filename: "{code:LaunchPath}"; Description: "Запустить «Заявки»"; Flags: postinstall nowait skipifsilent

[Code]
#include "AccessHostDiagnostics.iss"

const
  JournalUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\DenisPapusha.JournalWorkflow';

var
  RuntimeRestartRequired: Boolean;
  DirectoryPage: TInputDirWizardPage;
  ExistingRoot: String;
  HostBlockDetected: Boolean;
  SecuritySettingsButton: TNewButton;

procedure OpenSecuritySettings(Sender: TObject);
var
  Code: Integer;
begin
  if not ShellExec('open', WindowsSecuritySettingsUri, '', '', SW_SHOWNORMAL, ewNoWait, Code) then begin
    Log('Не удалось открыть параметры безопасности Windows, код: ' + IntToStr(Code));
    MsgBox('Не удалось открыть параметры Windows.' + #13#10#13#10 + WindowsSecurityManualPath, mbError, MB_OK);
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  SecuritySettingsButton.Visible := (CurPageID = wpPreparing) and HostBlockDetected and not WizardSilent;
end;

function NormalizeRoot(Value: String): String;
begin
  Result := RemoveBackslashUnlessRoot(ExpandFileName(Trim(Value)));
end;

function RegisteredRoot: String;
var
  Candidate: String;
begin
  Result := '';
  if not RegQueryStringValue(HKCU64, JournalUninstallKey, 'InstallLocation', Candidate) then
    RegQueryStringValue(HKCU32, JournalUninstallKey, 'InstallLocation', Candidate);
  if (Candidate <> '') and FileExists(AddBackslash(Candidate) + 'Update.exe') and
    FileExists(AddBackslash(Candidate) + 'Jornal.Workflow.Desktop.exe') then
    Result := NormalizeRoot(Candidate);
end;

procedure InitializeWizard;
var
  DefaultRoot, RequestedRoot, Description: String;
begin
  SecuritySettingsButton := TNewButton.Create(WizardForm);
  SecuritySettingsButton.Parent := WizardForm;
  SecuritySettingsButton.Left := ScaleX(16);
  SecuritySettingsButton.Top := WizardForm.CancelButton.Top;
  SecuritySettingsButton.Width := WizardForm.BackButton.Left - ScaleX(24);
  SecuritySettingsButton.Height := WizardForm.CancelButton.Height;
  SecuritySettingsButton.Caption := 'Параметры безопасности';
  SecuritySettingsButton.OnClick := @OpenSecuritySettings;
  SecuritySettingsButton.Visible := False;
  ExistingRoot := RegisteredRoot;
  DefaultRoot := ExistingRoot;
  if DefaultRoot = '' then DefaultRoot := ExpandConstant('{localappdata}\DenisPapusha.JournalWorkflow');
  Description := 'Установка для текущего пользователя. Выберите пустую папку с правами записи вне Program Files и Windows. OrderDB.mdb выбирается после установки.';
  if ExistingRoot <> '' then
    Description := 'Программа «Заявки» уже установлена. Повторная установка использует прежнюю папку. Для переноса сначала удалите программу через параметры Windows, затем установите заново; база и личные настройки хранятся отдельно.';
  DirectoryPage := CreateInputDirPage(wpWelcome, 'Папка установки',
    'Где установить «Заявки»?', Description, False, 'Заявки');
  DirectoryPage.Add('Папка приложения:');
  RequestedRoot := ExpandConstant('{param:JOURNALNEXTDIR|}');
  if RequestedRoot = '' then RequestedRoot := DefaultRoot;
  DirectoryPage.Values[0] := RequestedRoot;
  DirectoryPage.Edits[0].Enabled := ExistingRoot = '';
  DirectoryPage.Buttons[0].Enabled := ExistingRoot = '';
end;

function InstallRoot: String;
begin
  Result := NormalizeRoot(DirectoryPage.Values[0]);
end;

function IsWithinDirectory(Value, Parent: String): Boolean;
begin
  Parent := NormalizeRoot(Parent);
  Result := (CompareText(Value, Parent) = 0) or
    (CompareText(Copy(Value, 1, Length(Parent) + 1), AddBackslash(Parent)) = 0);
end;

function DirectoryError: String;
var
  Value: String;
  Entry: TFindRec;
begin
  Result := 'Выберите отдельную папку на локальном диске, например D:\Заявки.';
  Value := Trim(DirectoryPage.Values[0]);
  Log('Проверка папки установки: [' + Value + ']');
  if (Length(Value) < 4) or (Copy(Value, 2, 2) <> ':\') then begin
    Log('Ожидается абсолютный путь с буквой диска.');
    Exit;
  end;
  if PathHasInvalidCharacters(Value, True) then begin
    Log('В пути обнаружены недопустимые символы или компоненты.');
    Exit;
  end;
  Value := InstallRoot;
  Log('Нормализованная папка: [' + Value + ']');
  if IsWithinDirectory(Value, ExpandConstant('{pf64}')) or
    IsWithinDirectory(Value, ExpandConstant('{pf32}')) or
    IsWithinDirectory(Value, ExpandConstant('{win}')) then begin
    Result := 'Этот установщик устанавливает программу «Заявки» для текущего пользователя. Program Files и папка Windows требуют другого режима установки. Выберите папку в профиле пользователя или другую отдельную папку с правами записи; так обновления смогут работать без запуска программы от администратора.';
    Exit;
  end;
  if Length(Value) <= 3 then begin Log('Нельзя устанавливать в корень диска.'); Exit; end;
  if FileExists(Value) then begin Log('Вместо папки указан файл.'); Exit; end;
  if (ExistingRoot <> '') and (CompareText(Value, ExistingRoot) <> 0) then begin
    Result := 'Повторная установка должна использовать прежнюю папку: ' + ExistingRoot;
    Exit;
  end;
  Result := '';
  if (ExistingRoot = '') and FindFirst(AddBackslash(Value) + '*', Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then begin
          Result := 'Эта папка уже содержит файлы. Выберите отдельную пустую папку, чтобы сохранить прежнюю программу и данные.';
          Break;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Error: String;
begin
  Result := True;
  if CurPageID <> DirectoryPage.ID then Exit;
  Error := DirectoryError;
  Result := Error = '';
  if not Result then begin
    Log(Error);
    SuppressibleMsgBox(Error, mbError, MB_OK, IDOK);
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := 'Папка приложения:' + NewLine + Space + InstallRoot + NewLine + NewLine +
    'Компоненты:' + NewLine + Space + 'Заявки x64, модуль MDB x86, .NET Runtime x64/x86' + NewLine + NewLine +
    'База данных:' + NewLine + Space + 'Данные программы «Заявки» хранятся отдельно от прежнего Journal; установка не меняет его базу.' + NewLine + NewLine +
    'Сеть:' + NewLine + Space + 'Автопроверка обновлений обращается к GitHub. Её можно отключить в окне колокольчика; скачивание и установка — по вашей команде.';
end;

function LaunchPath(Param: String): String;
begin
  Result := InstallRoot + '\Jornal.Workflow.Desktop.exe';
end;

function CompatibleRuntimeVersion(Value: String): Boolean;
begin
  Result := (Copy(Value, 1, 5) = '10.0.') and
    (StrToIntDef(Copy(Value, 6, Length(Value)), -1) >= {#RuntimePatch});
end;

function HasRuntime(Architecture: String): Boolean;
var
  Versions: TArrayOfString;
  Index: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\' + Architecture + '\sharedfx\Microsoft.NETCore.App', Versions) then
    for Index := 0 to GetArrayLength(Versions) - 1 do
      if CompatibleRuntimeVersion(Versions[Index]) then begin Result := True; Exit; end;
  if RegGetValueNames(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\' + Architecture + '\sharedfx\Microsoft.NETCore.App', Versions) then
    for Index := 0 to GetArrayLength(Versions) - 1 do
      if CompatibleRuntimeVersion(Versions[Index]) then begin Result := True; Exit; end;
end;

function EnsureRuntime(Architecture: String): String;
var
  Code: Integer;
  FileName: String;
begin
  Result := '';
  if HasRuntime(Architecture) then begin Log('Совместимая .NET ' + Architecture + ' уже установлена.'); Exit; end;
  FileName := 'runtime-' + Architecture + '.exe';
  ExtractTemporaryFile(FileName);
  WizardForm.StatusLabel.Caption := 'Устанавливаем необходимые компоненты…';
  if not ShellExec('runas', ExpandConstant('{tmp}\') + FileName, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) then
  begin
    Result := 'Не удалось установить .NET. Разрешите установку компонентов в запросе Windows и повторите попытку. Код: ' + IntToStr(Code);
    Exit;
  end;
  Log('.NET ' + Architecture + ', код завершения: ' + IntToStr(Code));
  if Code = 3010 then RuntimeRestartRequired := True
  else if Code <> 0 then begin
    Result := 'Не удалось установить .NET, код: ' + IntToStr(Code) + '. Программа «Заявки» и база не изменены.';
    Exit;
  end;
  if not HasRuntime(Architecture) then
    Result := '.NET пока недоступна. Перезапустите Windows и повторите установку программы «Заявки».';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  ProbePath: String;
  Started: Boolean;
begin
  HostBlockDetected := False;
  SecuritySettingsButton.Visible := False;
  Result := DirectoryError;
  if Result <> '' then Exit;
  ProbePath := GenerateUniqueName(InstallRoot, '.tmp');
  if not ForceDirectories(InstallRoot) or not SaveStringToFile(ProbePath, '', False) then begin
    Result := 'Нет прав записи в выбранную папку. Выберите доступную вам папку, например в профиле пользователя.';
    Exit;
  end;
  DeleteFile(ProbePath);
  Log('Папка приложения: ' + InstallRoot);
  Result := EnsureRuntime('x64');
  if Result <> '' then Exit;
  Result := EnsureRuntime('x86');
  if Result <> '' then Exit;
  ExtractTemporaryFiles('{tmp}\AccessHost\*');
  Started := Exec(ExpandConstant('{tmp}\AccessHost\Jornal.AccessHost.exe'), '--probe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Log('AccessHost --probe: процесс запущен=' + IntToStr(Ord(Started)) + ', код=' + AccessHostCodeText(Code));
  if not Started then Log('Ошибка запуска AccessHost: ' + SysErrorMessage(Code));
  if not Started or (Code <> 0) then begin
    HostBlockDetected := IsAccessHostSecurityFailure(Started, Code);
    SecuritySettingsButton.Visible := HostBlockDetected and not WizardSilent;
    Result := AccessHostFailureText(Started, Code);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Error: String;
begin
  if CurStep = ssPostInstall then begin
    Error := DirectoryError;
    if Error <> '' then RaiseException(Error);
    ExtractTemporaryFile('Заявки-AppSetup.exe');
    if not Exec(ExpandConstant('{tmp}\Заявки-AppSetup.exe'), '--silent --installto "' + InstallRoot + '"', '', SW_SHOW, ewWaitUntilTerminated, Code) then
      RaiseException('Не удалось запустить установку программы «Заявки». Код: ' + IntToStr(Code));
    if Code <> 0 then RaiseException('Установка программы «Заявки» не завершена. Код: ' + IntToStr(Code));
    if not FileExists(LaunchPath('')) then RaiseException('Не найдена установленная программа «Заявки». Повторите установку.');
  end;
end;

function NeedRestart: Boolean;
begin
  Result := RuntimeRestartRequired;
end;
