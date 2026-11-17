[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$SkipInstall,
    [switch]$Deploy,
    [string]$DeployDir = "D:\Storageredirect\Program"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force -Path logs | Out-Null

function Invoke-Native {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$OutLog,
        [string]$ErrLog
    )
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments `
        -Wait -PassThru -NoNewWindow `
        -RedirectStandardOutput $OutLog -RedirectStandardError $ErrLog
    return $process.ExitCode
}

if ($Clean) {
    Remove-Item -Recurse -Force build, dist -ErrorAction SilentlyContinue
}

if (-not $SkipInstall) {
    Write-Host "Installing GUI/build dependencies..."
    $code = Invoke-Native "python" @("-m", "pip", "install", "-e", ".[gui,build]") `
        "logs\build-install.out.log" "logs\build-install.err.log"
    if ($code -ne 0) {
        Get-Content "logs\build-install.out.log" -Tail 80
        Get-Content "logs\build-install.err.log" -Tail 80
        exit $code
    }
}

if (-not (Test-Path "packaging\icon.ico")) {
    $code = Invoke-Native "python" @("packaging\make_icon.py") `
        "logs\build-icon.out.log" "logs\build-icon.err.log"
    if ($code -ne 0) {
        Get-Content "logs\build-icon.err.log" -Tail 40
        exit $code
    }
}

Write-Host "Building single-file exe..."
$code = Invoke-Native "python" `
    @("-m", "PyInstaller", "--noconfirm", "--clean", "packaging\RuijieCampusAuth.spec") `
    "logs\build.out.log" "logs\build.err.log"
if ($code -ne 0) {
    Get-Content "logs\build.out.log" -Tail 100
    Get-Content "logs\build.err.log" -Tail 100
    exit $code
}

$exe = Join-Path $PSScriptRoot "dist\RuijieCampusAuth.exe"
if (-not (Test-Path $exe)) {
    Get-Content "logs\build.out.log" -Tail 80
    Get-Content "logs\build.err.log" -Tail 80
    throw "exe not found: $exe"
}

$size = (Get-Item $exe).Length / 1MB
Write-Host ("Built: {0} ({1:N1} MB)" -f $exe, $size)

if ($Deploy) {
    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator
    )
    if (-not $isAdmin) {
        throw "Deploy requires an elevated PowerShell session"
    }
    New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null
    $target = Join-Path $DeployDir "RuijieCampusAuth.exe"
    $running = Get-Process RuijieCampusAuth -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "Stopping running RuijieCampusAuth..."
        $running | Stop-Process -Force
        Start-Sleep -Seconds 2
        if (Test-Path $target) {
            & $target --restore-client | Out-Null
        }
    }
    Copy-Item $exe $target -Force
    Write-Host "Deployed exe: $target"

    $vbsSource = Join-Path $PSScriptRoot "packaging\restart_ruijie.vbs"
    $vbsTarget = "C:\Program Files\restart_ruijie.vbs"
    if (Test-Path $vbsTarget) {
        $backup = "$vbsTarget.bak-" + (Get-Date -Format "yyyyMMdd-HHmmss")
        Copy-Item $vbsTarget $backup -Force
        Write-Host "Backed up launcher: $backup"
    }
    Copy-Item $vbsSource $vbsTarget -Force
    Write-Host "Deployed launcher: $vbsTarget"

    $ws = New-Object -ComObject WScript.Shell
    $shortcutPath = Join-Path $DeployDir "HXXY-CampusAuth.lnk"
    $shortcut = $ws.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $target
    $shortcut.WorkingDirectory = $DeployDir
    $shortcut.IconLocation = "$target,0"
    $shortcut.Description = "HXXY Ruijie Campus Auth"
    $shortcut.Save()
    Write-Host "Shortcut: $shortcutPath"

    $desktop = [Environment]::GetFolderPath("Desktop")
    if ($desktop) {
        $desktopShortcut = Join-Path $desktop "HXXY-CampusAuth.lnk"
        $shortcut = $ws.CreateShortcut($desktopShortcut)
        $shortcut.TargetPath = $target
        $shortcut.WorkingDirectory = $DeployDir
        $shortcut.IconLocation = "$target,0"
        $shortcut.Description = "HXXY Ruijie Campus Auth"
        $shortcut.Save()
        Write-Host "Desktop shortcut: $desktopShortcut"
    }
}

Write-Host "Requires Npcap; the exe requests administrator rights."
Write-Host "Run with: RuijieCampusAuth.exe --tray --auto"
exit 0
