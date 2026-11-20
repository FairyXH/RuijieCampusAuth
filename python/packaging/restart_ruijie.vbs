' Restart Ruijie Supplicant - windowless and takeover-aware.
'
' Healthy takeover (all four checks) -> do nothing:
'   1. takeover.ini enabled=1
'   2. takeover.ini modified within HEARTBEAT_MAX_AGE seconds
'   3. the PID recorded in takeover.ini is running as RuijieCampusAuth.exe
'      or RuijieCampusAuth.Service.exe
'   4. IFEO marker RuijieTakeover=RuijieCampusAuth (or the legacy
'      HXXYTakeover value) exists in the 64- or 32-bit view
'
' Otherwise the launcher restores any stale policy, kills 8021x.exe and starts
' the original RuijieSupplicant.exe (the previous behavior).

Option Explicit

Const TAKEOVER_DIR = "D:\Storageredirect\Program\RuijieCampusAuth"
Const TAKEOVER_EXE = "D:\Storageredirect\Program\RuijieCampusAuth\RuijieCampusAuth.exe"
Const ORIGINAL_EXE = "C:\Program Files\Ruijie Networks\Ruijie Supplicant\RuijieSupplicant.exe"
Const ORIGINAL_DIR = "C:\Program Files\Ruijie Networks\Ruijie Supplicant"
Const IFEO_KEY = "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\8021x.exe"
Const MARKER_NAME = "RuijieTakeover"
Const LEGACY_MARKER_NAME = "HXXYTakeover"
Const MARKER_VALUE = "RuijieCampusAuth"
Const TAKEOVER_PROCESS = "RuijieCampusAuth.exe"
Const SERVICE_PROCESS = "RuijieCampusAuth.Service.exe"
Const HEARTBEAT_MAX_AGE = 12

Dim shell, fso, wmi, regSvc
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
Set wmi = Nothing
On Error Resume Next
Set wmi = GetObject("winmgmts:\\.\root\cimv2")
Err.Clear

' StdRegProv does registry reads/writes in-process: no console windows
' at all (reg.exe children flicker badly under wscript). The hDefKey
' flags select the 64/32-bit view, replacing reg.exe /reg:64|32.
Set regSvc = Nothing
Set regSvc = GetObject("winmgmts:{impersonationLevel=impersonate}!\\.\root\default:StdRegProv")
Err.Clear

Const HKLM = &H80000002
Const KEY_WOW64_32KEY = &H200

Function StateDir()
    Dim dir
    dir = TAKEOVER_DIR
    On Error Resume Next
    If Not fso.FolderExists(dir) Then fso.CreateFolder(dir)
    Err.Clear
    StateDir = dir
End Function

Function StatePath()
    StatePath = StateDir() & "\takeover.ini"
End Function

Sub Log(msg)
    On Error Resume Next
    Dim path, file
    path = StateDir() & "\launcher.log"
    If fso.FileExists(path) Then
        If fso.GetFile(path).Size > 1048576 Then
            If fso.FileExists(path & ".1") Then fso.DeleteFile path & ".1", True
            fso.MoveFile path, path & ".1"
            Err.Clear
        End If
    End If
    Set file = fso.OpenTextFile(path, 8, True)
    If Err.Number <> 0 Then
        Err.Clear
        path = shell.ExpandEnvironmentStrings("%TEMP%") & "\RuijieCampusAuth-launcher.log"
        Set file = fso.OpenTextFile(path, 8, True)
    End If
    If Err.Number = 0 Then
        file.WriteLine Now & " " & msg
        file.Close
    End If
    Err.Clear
End Sub

Function RegExe()
    Dim sysnative, system32
    sysnative = shell.ExpandEnvironmentStrings("%SystemRoot%") & "\Sysnative\reg.exe"
    system32 = shell.ExpandEnvironmentStrings("%SystemRoot%") & "\System32\reg.exe"
    If fso.FileExists(sysnative) Then
        RegExe = sysnative
    Else
        RegExe = system32
    End If
End Function

Function IfeoValueHas(view32, name, valueName, needle)
    Dim subKey, value
    IfeoValueHas = False
    On Error Resume Next
    subKey = "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\" & name
    If view32 Then subKey = "SOFTWARE\WOW6432Node\" & subKey
    If Not regSvc Is Nothing Then
        value = ""
        regSvc.GetStringValue HKLM, subKey, valueName, value
        If Err.Number <> 0 Then
            Err.Clear
            Exit Function
        End If
        If InStr(1, CStr(value), needle, vbTextCompare) > 0 Then
            IfeoValueHas = True
        End If
        Exit Function
    End If
    ' StdRegProv unavailable: hidden reg.exe fallback (shell.Exec would
    ' flash a console window, so capture through a hidden cmd redirect).
    If InStr(1, RegQueryHidden("reg query """ & IfeoKeyFor(name) & """ /v " & valueName & " /reg:64"), needle, vbTextCompare) > 0 _
       Or InStr(1, RegQueryHidden("reg query """ & IfeoKeyFor(name) & """ /v " & valueName & " /reg:32"), needle, vbTextCompare) > 0 Then
        IfeoValueHas = True
    End If
End Function

Sub IfeoValueDelete(view32, name, valueName)
    Dim subKey
    On Error Resume Next
    subKey = "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\" & name
    If view32 Then subKey = "SOFTWARE\WOW6432Node\" & subKey
    If Not regSvc Is Nothing Then
        regSvc.DeleteValue HKLM, subKey, valueName
        Err.Clear
        Exit Sub
    End If
    shell.Run "reg delete """ & IfeoKeyFor(name) & """ /v " & valueName & " /f /reg:64", 0, True
    shell.Run "reg delete """ & IfeoKeyFor(name) & """ /v " & valueName & " /f /reg:32", 0, True
End Sub

Function RegQueryHidden(cmd)
    Dim out, text, file
    RegQueryHidden = ""
    On Error Resume Next
    out = shell.ExpandEnvironmentStrings("%TEMP%") & "\ruijie_reg_query.tmp"
    shell.Run "cmd.exe /c " & cmd & " > """ & out & """ 2>nul", 0, True
    If Err.Number <> 0 Then
        Err.Clear
        Exit Function
    End If
    If fso.FileExists(out) Then
        Set file = fso.OpenTextFile(out, 1, False)
        If Err.Number = 0 Then
            text = file.ReadAll
            file.Close
            RegQueryHidden = text
        End If
        fso.DeleteFile out, True
    End If
    Err.Clear
End Function

Function ReadIni(path)
    Dim dict, file, line, pos, key, value
    Set dict = CreateObject("Scripting.Dictionary")
    On Error Resume Next
    If fso.FileExists(path) Then
        Set file = fso.OpenTextFile(path, 1, False)
        Do Until file.AtEndOfStream
            line = Trim(file.ReadLine)
            pos = InStr(line, "=")
            If pos > 0 Then
                key = LCase(Trim(Left(line, pos - 1)))
                value = Trim(Mid(line, pos + 1))
                If key <> "" Then
                    If dict.Exists(key) Then dict(key) = value Else dict.Add key, value
                End If
            End If
        Loop
        file.Close
    End If
    Err.Clear
    Set ReadIni = dict
End Function

Function StateEnabled(state)
    StateEnabled = False
    If state.Exists("enabled") Then
        StateEnabled = (state("enabled") = "1")
    End If
End Function

Function FileFresh(path, seconds)
    Dim file, age
    FileFresh = False
    On Error Resume Next
    If Not fso.FileExists(path) Then Exit Function
    Set file = fso.GetFile(path)
    If Err.Number <> 0 Then Exit Function
    age = DateDiff("s", file.DateLastModified, Now())
    If age < 0 Then age = -age
    FileFresh = (age <= seconds)
    Err.Clear
End Function

Function ProcessPidRunning(name, expectedPid)
    Dim items, item
    ProcessPidRunning = False
    On Error Resume Next
    If wmi Is Nothing Then Exit Function
    Set items = wmi.ExecQuery("SELECT ProcessId FROM Win32_Process WHERE Name='" & name & "'")
    If Err.Number <> 0 Then Exit Function
    For Each item In items
        If expectedPid = "" Or expectedPid = "0" Then
            ProcessPidRunning = True
        ElseIf CStr(item.ProcessId) = CStr(expectedPid) Then
            ProcessPidRunning = True
        End If
    Next
    Err.Clear
End Function

Function IfeoKeyFor(name)
    IfeoKeyFor = "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\" & name
End Function

Function MarkerForName(name)
    ' Check current and legacy markers in both registry views. The service
    ' writes RuijieTakeover; older builds wrote HXXYTakeover.
    MarkerForName = IfeoValueHas(True, name, MARKER_NAME, MARKER_VALUE) _
        Or IfeoValueHas(False, name, MARKER_NAME, MARKER_VALUE) _
        Or IfeoValueHas(True, name, LEGACY_MARKER_NAME, MARKER_VALUE) _
        Or IfeoValueHas(False, name, LEGACY_MARKER_NAME, MARKER_VALUE)
End Function

Function MarkerPresent()
    MarkerPresent = MarkerForName("8021x.exe")
End Function

' Older builds wrote only the Debugger value, without a takeover marker.
' Treat that as a stale policy so the transition cannot leave 8021x blocked.
Function LegacyDebuggerForName(name)
    LegacyDebuggerForName = IfeoValueHas(True, name, "Debugger", "cmd.exe") _
        And IfeoValueHas(True, name, "Debugger", "/c exit")
    If Not LegacyDebuggerForName Then
        LegacyDebuggerForName = IfeoValueHas(False, name, "Debugger", "cmd.exe") _
            And IfeoValueHas(False, name, "Debugger", "/c exit")
    End If
End Function

Function LegacyDebuggerPresent()
    LegacyDebuggerPresent = LegacyDebuggerForName("8021x.exe")
End Function

Function OfficialExeNames()
    ' Same set the C# TakeoverGuard enumerates, excluding 7z/uninstaller.
    OfficialExeNames = Array( _
        "8021x.exe", _
        "RuijieSupplicant.exe", _
        "NetworkDetect.exe", _
        "snetcfg.exe", _
        "SoftwareManager.exe", _
        "SuInstaller.exe", _
        "suservice.exe")
End Function

Function AnyIfeoPolicyPresent()
    Dim names, name
    AnyIfeoPolicyPresent = False
    names = OfficialExeNames()
    For Each name In names
        If MarkerForName(name) Or LegacyDebuggerForName(name) Then
            AnyIfeoPolicyPresent = True
            Exit Function
        End If
    Next
End Function

Sub DeleteIfeoValue(name, valueName)
    IfeoValueDelete True, name, valueName
    IfeoValueDelete False, name, valueName
End Sub

' Last-resort recovery if --restore-client is missing, hung, or D: is offline.
' Only removes values written by this tool; a real third-party Debugger is kept.
Sub ClearIfeoPolicy()
    Dim names, name
    On Error Resume Next
    names = OfficialExeNames()
    For Each name In names
        If MarkerForName(name) Then
            DeleteIfeoValue name, MARKER_NAME
            DeleteIfeoValue name, LEGACY_MARKER_NAME
            DeleteIfeoValue name, "Debugger"
        ElseIf LegacyDebuggerForName(name) Then
            DeleteIfeoValue name, "Debugger"
        End If
    Next
    Err.Clear
End Sub

Function IsHealthy()
    Dim state, pid
    IsHealthy = False
    If Not fso.FileExists(StatePath()) Then Exit Function
    If Not FileFresh(StatePath(), HEARTBEAT_MAX_AGE) Then Exit Function
    Set state = ReadIni(StatePath())
    If Not StateEnabled(state) Then Exit Function
    pid = ""
    If state.Exists("pid") Then pid = state("pid")
    If Not (ProcessPidRunning(TAKEOVER_PROCESS, pid) Or ProcessPidRunning(SERVICE_PROCESS, pid)) Then Exit Function
    If Not MarkerPresent() Then Exit Function
    IsHealthy = True
End Function

Dim healthy, attempt, stateOn, state, markerOn, legacyOn, processOn
healthy = False
For attempt = 1 To 2
    If IsHealthy() Then
        healthy = True
        Exit For
    End If
    If attempt < 2 Then WScript.Sleep 1500
Next

If healthy Then
    Log "healthy takeover; skip original"
    WScript.Quit 0
End If

stateOn = False
Set state = ReadIni(StatePath())
stateOn = StateEnabled(state)
markerOn = AnyIfeoPolicyPresent()
legacyOn = LegacyDebuggerPresent()
processOn = ProcessPidRunning(TAKEOVER_PROCESS, "") Or ProcessPidRunning(SERVICE_PROCESS, "")

If stateOn Or markerOn Or legacyOn Then
    If processOn Then
        Log "stale/hung takeover; terminating GUI/service"
        shell.Run "taskkill /f /im " & TAKEOVER_PROCESS & " /t", 0, True
        shell.Run "taskkill /f /im " & SERVICE_PROCESS & " /t", 0, True
        WScript.Sleep 500
    End If
    If fso.FileExists(TAKEOVER_EXE) Then
        Log "restoring stale policy"
        On Error Resume Next
        shell.Run """" & TAKEOVER_EXE & """ --restore-client", 0, True
        If Err.Number <> 0 Then
            Log "restore command failed: " & Err.Description
            Err.Clear
        End If
        On Error GoTo 0
    Else
        Log "takeover exe missing; using direct IFEO cleanup"
    End If
End If

' Do not leave the official client blocked when the exe/restore path failed.
If AnyIfeoPolicyPresent() Then
    Log "restore incomplete; clearing IFEO directly"
    ClearIfeoPolicy()
End If

If IsHealthy() Then
    Log "takeover healthy after recovery; skip original"
    WScript.Quit 0
End If

Log "launching original supplicant"
shell.Run "taskkill /f /im 8021x.exe /t", 0, True
shell.CurrentDirectory = ORIGINAL_DIR
shell.Run """" & ORIGINAL_EXE & """", 0, False
