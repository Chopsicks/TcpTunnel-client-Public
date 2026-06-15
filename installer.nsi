!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "nsDialogs.nsh"
Name "TCP Tunnel 3.2.7"
OutFile "TCP-Tunnel-Setup-3.2.7.exe"
InstallDir "$LOCALAPPDATA\TCP Tunnel"
InstallDirRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\TCPTunnel" "InstallLocation"
RequestExecutionLevel user

!define MUI_ICON "ui\icon.ico"
!define MUI_UNICON "ui\icon.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\TCP-Tunnel.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Запустить TCP Tunnel"

Var ExistingInstallPath
Var RadioUpdate
Var RadioRepair
Var RadioRemove

Page custom ExistingInstallPage ExistingInstallPageLeave
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Russian"

Function .onInit
  ReadRegStr $ExistingInstallPath HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\TCPTunnel" "InstallLocation"
  ${If} $ExistingInstallPath != ""
  ${AndIfNot} ${FileExists} "$ExistingInstallPath\TCP-Tunnel.exe"
    StrCpy $ExistingInstallPath ""
  ${EndIf}

  ${If} $ExistingInstallPath != ""
    StrCpy $INSTDIR $ExistingInstallPath
  ${EndIf}
FunctionEnd

Function ExistingInstallPage
  ${If} $ExistingInstallPath == ""
    Abort
  ${EndIf}

  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}

  ${NSD_CreateLabel} 0 0 100% 24u "Найдена установленная копия TCP Tunnel. Выберите действие:"
  Pop $0

  ${NSD_CreateRadioButton} 0 34u 100% 12u "Обновить программу"
  Pop $RadioUpdate
  ${NSD_Check} $RadioUpdate

  ${NSD_CreateRadioButton} 0 52u 100% 12u "Починить установку"
  Pop $RadioRepair

  ${NSD_CreateRadioButton} 0 70u 100% 12u "Удалить программу"
  Pop $RadioRemove

  nsDialogs::Show
FunctionEnd

Function ExistingInstallPageLeave
  ${If} $ExistingInstallPath == ""
    Return
  ${EndIf}

  ${NSD_GetState} $RadioRemove $0
  ${If} $0 == ${BST_CHECKED}
    Call RunExistingUninstaller
    Quit
  ${EndIf}

  ${NSD_GetState} $RadioUpdate $0
  ${If} $0 == ${BST_CHECKED}
    Call RunExistingUninstaller
  ${EndIf}
FunctionEnd

Function RunExistingUninstaller
  Call KillRunningProcesses
  IfFileExists "$ExistingInstallPath\Uninstall.exe" 0 done
    ExecWait '"$ExistingInstallPath\Uninstall.exe" /S _?=$ExistingInstallPath'
    Sleep 500
  done:
FunctionEnd

Function KillRunningProcesses
  ExecWait 'taskkill /IM "TCP-Tunnel.exe" /T'
  ExecWait 'taskkill /IM "frpc.exe" /T'
  Sleep 600
FunctionEnd

Function un.KillRunningProcesses
  ExecWait 'taskkill /IM "TCP-Tunnel.exe" /T'
  ExecWait 'taskkill /IM "frpc.exe" /T'
  Sleep 600
FunctionEnd

Section "Программа" SecApp
  SectionIn RO
  SetOutPath "$INSTDIR"
  SetOverwrite on
  RMDir /r "$TEMP\TCPTunnel_WV2"
  Delete "$INSTDIR\TCP-Tunnel.exe"
  Delete "$INSTDIR\TCP-Tunnel.dll"
  Delete "$INSTDIR\TCP-Tunnel.deps.json"
  Delete "$INSTDIR\TCP-Tunnel.runtimeconfig.json"
  Delete "$INSTDIR\frpc.exe"
  File /r "publish_sf\*.*"

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\TCPTunnel" "DisplayName" "TCP Tunnel 3.2.7"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\TCPTunnel" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\TCPTunnel" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\TCPTunnel" "Publisher" "TCP Tunnel"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\TCPTunnel" "DisplayVersion" "3.2.7"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\TCPTunnel" "NoModify" 0
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\TCPTunnel" "NoRepair" 0
SectionEnd

Section "Ярлык в меню Пуск" SecStartMenu
  CreateDirectory "$SMPROGRAMS\TCP Tunnel"
  CreateShortcut "$SMPROGRAMS\TCP Tunnel\TCP Tunnel.lnk" "$INSTDIR\TCP-Tunnel.exe"
SectionEnd

Section "Ярлык на рабочем столе" SecDesktop
  CreateShortcut "$DESKTOP\TCP Tunnel.lnk" "$INSTDIR\TCP-Tunnel.exe"
SectionEnd

Section "Uninstall"
  Call un.KillRunningProcesses
  Delete "$PROFILE\.tcptunnel_settings.json"
  Delete "$PROFILE\.tcptunnel_account.json"
  RMDir /r "$TEMP\TCPTunnel_WV2"
  RMDir /r "$INSTDIR"

  Delete "$SMPROGRAMS\TCP Tunnel\TCP Tunnel.lnk"
  RMDir "$SMPROGRAMS\TCP Tunnel"
  Delete "$DESKTOP\TCP Tunnel.lnk"

  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\TCPTunnel"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "TCP Tunnel"
SectionEnd

