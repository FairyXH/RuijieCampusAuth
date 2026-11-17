' Restart Ruijie Supplicant - windowless and takeover-aware.
'
' Healthy takeover (all four checks) -> do nothing:
'   1. takeover.ini enabled=1
'   2. takeover.ini modified within HEARTBEAT_MAX_AGE seconds
'   3. the PID recorded in takeover.ini is running as RuijieCampusAuth.exe
'   4. IFEO marker HXXYTakeover=RuijieCampusAuth exists in the 64- or 32-bit view
'
' Otherwise the launcher restores any stale policy, kills 8021x.exe and starts
' the original RuijieSupplicant.exe (the previous behavior).

Option Explicit

Const TAKEOVER_EXE = "D:\Storageredirect\Program\RuijieCampusAuth.exe"
Const ORIGINAL_EXE = "C:\Program Files\Ruijie Networks\Ruijie Supplicant\RuijieSupplicant.exe"
Const ORIGINAL_DIR = "C:\Program Files\Ruijie Networks\Ruijie Supplicant"
Const IFEO_KEY = "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\8021x.exe"
Const MARKER_NAME = "HXXYTakeover"
Const MARKER_VALUE = "RuijieCampusAuth"
Const TAKEOVER_PROCESS = "RuijieCampusAuth.exe"
Const HEARTBEAT_MAX_AGE = 12

Dim shell, fso, wmi
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
Set wmi = Nothing
On Error Resume Next
Set wmi = GetObject("winmgmts:\\.\root\cimv2")
Err.Clear

Function StateDir()
    Dim dir
    dir = shell.ExpandEnvironmentStrings("%ProgramData%") & "\RuijieCampusAuth"
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

Function MarkerPresent()
    Dim views, view, cmd, exec, output
    MarkerPresent = False
    On Error Resume Next
    views = Array("64", "32")
    For Each view In views
        cmd = """" & RegExe() & """ query """ & IFEO_KEY & """ /v " & MARKER_NAME & " /reg:" & view
        Set exec = shell.Exec(cmd)
        If Err.Number = 0 Then
            output = exec.StdOut.ReadAll
            If exec.ExitCode = 0 And InStr(1, output, MARKER_VALUE, vbTextCompare) > 0 Then
                MarkerPresent = True
                Exit Function
            End If
        End If
        Err.Clear
    Next
End Function

' Older builds wrote only the Debugger value, without HXXYTakeover.
' Treat that as a stale policy so the transition cannot leave 8021x blocked.
Function LegacyDebuggerPresent()
    Dim views, view, cmd, exec, output, text
    LegacyDebuggerPresent = False
    On Error Resume Next
    views = Array("64", "32")
    For Each view In views
        cmd = """" & RegExe() & """ query """ & IFEO_KEY & """ /v Debugger /reg:" & view
        Set exec = shell.Exec(cmd)
        If Err.Number = 0 Then
            output = exec.StdOut.ReadAll
            text = LCase(output)
            If exec.ExitCode = 0 And InStr(1, text, "cmd.exe", vbTextCompare) > 0 And InStr(1, text, "/c exit", vbTextCompare) > 0 Then
                LegacyDebuggerPresent = True
                Exit Function
            End If
        End If
        Err.Clear
    Next
End Function

Function IsHealthy()
    Dim state, pid
    IsHealthy = False
    If Not fso.FileExists(StatePath()) Then Exit Function
    If Not FileFresh(StatePath(), HEARTBEAT_MAX_AGE) Then Exit Function
    Set state = ReadIni(StatePath())
    If Not StateEnabled(state) Then Exit Function
    pid = ""
    If state.Exists("pid") Then pid = state("pid")
    If Not ProcessPidRunning(TAKEOVER_PROCESS, pid) Then Exit Function
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
markerOn = MarkerPresent()
legacyOn = LegacyDebuggerPresent()
processOn = ProcessPidRunning(TAKEOVER_PROCESS, "")

If stateOn Or markerOn Or legacyOn Then
    If processOn Then
        Log "stale/hung takeover; terminating GUI"
        shell.Run "taskkill /f /im " & TAKEOVER_PROCESS & " /t", 0, True
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
    End If
End If

If IsHealthy() Then
    Log "takeover healthy after recovery; skip original"
    WScript.Quit 0
End If

Log "launching original supplicant"
shell.Run "taskkill /f /im 8021x.exe /t", 0, True
shell.CurrentDirectory = ORIGINAL_DIR
shell.Run """" & ORIGINAL_EXE & """", 0, False
