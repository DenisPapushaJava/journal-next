# -*- coding: utf-8 -*-
Unicode True
Name "Сервер заявок"
OutFile "${SETUP_FILE}"
InstallDir "$LOCALAPPDATA\JournalWorkflowOfficeServer"
RequestExecutionLevel user
SetCompressor /SOLID lzma
ShowInstDetails nevershow
ShowUninstDetails nevershow

!include "MUI2.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\DenisPapusha.JournalOfficeServer"
!define MUI_FINISHPAGE_RUN "$INSTDIR\app\JournalOfficeServer.exe"
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"

VIProductVersion "2026.1.0.${APP_VERSION}"
VIAddVersionKey /LANG=${LANG_RUSSIAN} "FileDescription" "Установка сервера заявок"
VIAddVersionKey /LANG=${LANG_RUSSIAN} "ProductName" "Сервер заявок"
VIAddVersionKey /LANG=${LANG_RUSSIAN} "FileVersion" "2026.1.0.${APP_VERSION}"
VIAddVersionKey /LANG=${LANG_RUSSIAN} "LegalCopyright" "Journal Office Server"

!macro AcquireInstallerMutex
    System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Local\JournalWorkflowOfficeServer-Setup") p .r0 ?e'
    Pop $1
    ${If} $0 == 0
    ${OrIf} $1 == 183
        MessageBox MB_OK|MB_ICONEXCLAMATION "Установка или удаление сервера уже выполняется." /SD IDOK
        Abort
    ${EndIf}
!macroend

Function .onInit
    ${IfNot} ${RunningX64}
    ${OrIfNot} ${AtLeastWin10}
        MessageBox MB_OK|MB_ICONEXCLAMATION "Нужна 64-разрядная Windows 10 или новее." /SD IDOK
        Abort
    ${EndIf}
    SetRegView 64
    StrCpy $INSTDIR "$LOCALAPPDATA\JournalWorkflowOfficeServer"
    !insertmacro AcquireInstallerMutex
FunctionEnd

Function un.onInit
    SetRegView 64
    StrCpy $INSTDIR "$LOCALAPPDATA\JournalWorkflowOfficeServer"
    !insertmacro AcquireInstallerMutex
FunctionEnd

Section
    InitPluginsDir
    SetOutPath "$PLUGINSDIR\app"
    File /r "${PAYLOAD}/*"
    ClearErrors
    ExecWait '"$PLUGINSDIR\app\JournalOfficeServer.exe" --install' $0
    IfErrors install_failed
    StrCmp $0 0 0 install_failed
    SetOutPath "$INSTDIR"
    ClearErrors
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "Сервер заявок"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "v${APP_VERSION}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\app\JournalOfficeServer.exe"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
    CreateShortcut "$SMPROGRAMS\Сервер заявок.lnk" "$INSTDIR\app\JournalOfficeServer.exe"
    IfErrors install_failed
    Goto install_done
install_failed:
    SetErrorLevel 1
    Abort "Установка сервера прервана. Подробности показаны в сообщении «Сервер заявок»."
install_done:
SectionEnd

Section "Uninstall"
    IfFileExists "$INSTDIR\app\JournalOfficeServer.exe" 0 check_settings
    ClearErrors
    ExecWait '"$INSTDIR\app\JournalOfficeServer.exe" --uninstall' $0
    IfErrors remove_failed
    StrCmp $0 0 remove_files remove_failed
check_settings:
    IfFileExists "$INSTDIR\settings.json" remove_failed
remove_files:
    SetOutPath "$TEMP"
    ClearErrors
    !include "${REMOVE_FILES}"
    RMDir "$INSTDIR\app"
    Delete "$SMPROGRAMS\Сервер заявок.lnk"
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"
    Goto remove_done
remove_failed:
    SetErrorLevel 1
    Abort "Удаление прервано. Проверьте сообщение сервера и повторите попытку."
remove_done:
SectionEnd
