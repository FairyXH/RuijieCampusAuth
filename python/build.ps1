[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$SkipInstall,
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

if ($Clean) {
    Remove-Item -Recurse -Force build, dist -ErrorAction SilentlyContinue
}

if (-not $SkipInstall) {
    Write-Host "Installing service build dependencies..."
    $code = Invoke-Native "python" @("-m", "pip", "install", "-e", ".[build]") `
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

Write-Host "Building service host (onedir)..."
$code = Invoke-Native "python" `
    @("-m", "PyInstaller", "--noconfirm", "--clean", "packaging\RuijieService.spec") `
    "logs\build-service.out.log" "logs\build-service.err.log"
if ($code -ne 0) {
    Get-Content "logs\build-service.out.log" -Tail 100
    Get-Content "logs\build-service.err.log" -Tail 100
    exit $code
}

$serviceDir = Join-Path $PSScriptRoot "dist\RuijieService"
$serviceExe = Join-Path $serviceDir "RuijieService.exe"
if (-not (Test-Path $serviceExe)) {
    Get-Content "logs\build-service.out.log" -Tail 80
    Get-Content "logs\build-service.err.log" -Tail 80
    throw "service exe not found: $serviceExe"
}

$serviceSize = (
    Get-ChildItem $serviceDir -Recurse -File |
        Measure-Object -Property Length -Sum
).Sum / 1MB
Write-Host ("Built service: {0} ({1:N1} MB)" -f $serviceExe, $serviceSize)

if ($Deploy) {
    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator
    )
    if (-not $isAdmin) {
        throw "Deploy requires an elevated PowerShell session"
    }
    New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null
    $service = Get-Service -Name "RuijieCampusAuth" -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne "Stopped") {
        Write-Host "Stopping system service..."
        Stop-Service -Name "RuijieCampusAuth" -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    Copy-Item (Join-Path $serviceDir "*") $DeployDir -Recurse -Force
    Write-Host "Deployed service host: $DeployDir\RuijieService.exe"
    Write-Host "Next: run 'ruijie-auth' -> 7) 系统服务 -> 1) 安装并启动"
}
