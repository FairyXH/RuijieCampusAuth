[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [switch]$Deploy,
    [string]$DeployDir = "D:\Storageredirect\Program\RuijieCampusAuth"
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

$publishDir = Join-Path $PSScriptRoot "publish\$Runtime"
Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

$projects = @(
    "RuijieCampusAuth.Service\RuijieCampusAuth.Service.csproj",
    "RuijieCampusAuth.Cli\RuijieCampusAuth.Cli.csproj",
    "RuijieCampusAuth.App\RuijieCampusAuth.App.csproj"
)

foreach ($project in $projects) {
    Write-Host "Publishing $project ..."
    $code = Invoke-Native "dotnet" @(
        "publish", $project,
        "-c", "Release",
        "-r", $Runtime,
        "--self-contained", "false",
        "-o", $publishDir,
        "--nologo"
    ) "logs\publish.out.log" "logs\publish.err.log"
    if ($code -ne 0) {
        Get-Content "logs\publish.out.log" -Tail 100
        Get-Content "logs\publish.err.log" -Tail 100
        exit $code
    }
}

$appExe = if ($Runtime.StartsWith("win")) {
    Join-Path $publishDir "RuijieCampusAuth.exe"
} else {
    Join-Path $publishDir "RuijieCampusAuth"
}
$serviceExe = if ($Runtime.StartsWith("win")) {
    Join-Path $publishDir "RuijieCampusAuth.Service.exe"
} else {
    Join-Path $publishDir "RuijieCampusAuth.Service"
}
foreach ($required in @($appExe, $serviceExe)) {
    if (-not (Test-Path $required)) {
        throw "publish output missing: $required"
    }
}
Write-Host "Published to: $publishDir"

if ($Deploy) {
    if (-not $Runtime.StartsWith("win")) {
        throw "Deploy is only implemented for the Windows RID"
    }
    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator
    )
    if (-not $isAdmin) {
        throw "Deploy requires an elevated PowerShell session"
    }
    New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null

    Get-Service RuijieCampusAuth, HXXY-CampusAuth -ErrorAction SilentlyContinue |
        Stop-Service -Force -ErrorAction SilentlyContinue
    Get-Process RuijieCampusAuth, RuijieCampusAuth.Service -ErrorAction SilentlyContinue |
        Stop-Process -Force
    Start-Sleep -Seconds 1

    Copy-Item (Join-Path $publishDir "*") $DeployDir -Recurse -Force
    $profilesSource = Join-Path $PSScriptRoot "profiles"
    if (Test-Path $profilesSource) {
        Copy-Item $profilesSource (Join-Path $DeployDir "profiles") -Recurse -Force
        Write-Host "Deployed profiles: $(Join-Path $DeployDir 'profiles')"
    }
    $target = Join-Path $DeployDir "RuijieCampusAuth.exe"
    Write-Host "Deployed GUI: $target"

    $vbsSource = Join-Path $PSScriptRoot "python\packaging\restart_ruijie.vbs"
    $vbsTarget = "C:\Program Files\restart_ruijie.vbs"
    if (Test-Path $vbsTarget) {
        $backup = "$vbsTarget.bak-" + (Get-Date -Format "yyyyMMdd-HHmmss")
        Copy-Item $vbsTarget $backup -Force
        Write-Host "Backed up launcher: $backup"
    }
    Copy-Item $vbsSource $vbsTarget -Force
    Write-Host "Deployed launcher: $vbsTarget"

    $ws = New-Object -ComObject WScript.Shell
    $shortcutPath = Join-Path $DeployDir "RuijieAuth.lnk"
    $shortcut = $ws.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $target
    $shortcut.Arguments = "--tray --auto"
    $shortcut.WorkingDirectory = $DeployDir
    $shortcut.IconLocation = "$target,0"
    $shortcut.Description = "Ruijie Campus Auth Client"
    $shortcut.Save()
    Write-Host "Shortcut: $shortcutPath"

    $desktop = [Environment]::GetFolderPath("Desktop")
    if ($desktop) {
        $desktopShortcut = Join-Path $desktop "RuijieAuth.lnk"
        $shortcut = $ws.CreateShortcut($desktopShortcut)
        $shortcut.TargetPath = $target
        $shortcut.Arguments = "--tray --auto"
        $shortcut.WorkingDirectory = $DeployDir
        $shortcut.IconLocation = "$target,0"
        $shortcut.Description = "Ruijie Campus Auth Client"
        $shortcut.Save()
        Write-Host "Desktop shortcut: $desktopShortcut"
    }

    Write-Host "Launch: `"$target`" --tray --auto"
    Write-Host "Install service: `"$DeployDir\RuijieCampusAuth.Cli.exe`" service install"
}

exit 0
