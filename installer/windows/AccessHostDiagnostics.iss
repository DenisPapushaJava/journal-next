{ Общие для установщика и нативной CI-проверки сообщения. Никаких изменений защиты. }
const
  WindowsSecuritySettingsUri = 'ms-settings:windowsdefender';
  WindowsSecurityManualPath = 'Откройте через «Пуск»: «Безопасность Windows» → «Управление приложениями/браузером». На рабочем компьютере обратитесь к ИТ.';

function IsAccessHostSecurityFailure(Started: Boolean; Code: Integer): Boolean;
begin
  if Started then
    { STATUS_SYSTEM_INTEGRITY_POLICY_VIOLATION / STATUS_INVALID_IMAGE_HASH }
    Result := (Code = -1058471934) or (Code = -1073740760)
  else
    Result := (Code = 4551) or (Code = 1260) or (Code = 577);
end;

function AccessHostCodeText(Code: Integer): String;
begin
  if Code = -1058471934 then Result := '0xC0E90002'
  else if Code = -1073740760 then Result := '0xC0000428'
  else Result := IntToStr(Code);
end;

function AccessHostFailureText(Started: Boolean; Code: Integer): String;
begin
  if IsAccessHostSecurityFailure(Started, Code) then begin
    if (Code = 577) or (Code = -1073740760) then
      Result := 'Windows не смогла проверить подпись или целостность модуля работы с MDB.'
    else
      Result := 'Windows заблокировала модуль работы с MDB политикой контроля приложений. Это может быть Smart App Control или правило организации.';
    Result := Result + #13#10#13#10 + WindowsSecurityManualPath;
  end
  else if not Started then
    Result := 'Не удалось запустить модуль работы с MDB. Проверьте установочный файл и доступ к временной папке. Точная причина записана в журнал установки.'
  else if Code = 3 then
    Result := 'Модуль работы с MDB запущен, но встроенный провайдер Jet недоступен. Обратитесь к ИТ для проверки компонентов Windows.'
  else
    Result := 'Проверка модуля работы с MDB завершилась с ошибкой. Передайте журнал установки в ИТ.';
  Result := Result + #13#10#13#10 + 'Код: ' + AccessHostCodeText(Code) +
    '. Установка остановлена до замены приложения. Существующая база и Office не изменены.';
end;
