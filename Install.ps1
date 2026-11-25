param(
    [Parameter(Mandatory=$true)][string]$Username,
    [Parameter(Mandatory=$true)][Security.SecureString]$Password
)

$ErrorActionPreference = 'Stop'
$base = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath = Join-Path $base 'CampusNetHelper.exe'
if (-not (Test-Path -LiteralPath $exePath)) { throw '缺少 CampusNetHelper.exe，请先运行 Build-App.ps1。' }
Add-Type -AssemblyName System.Security
function Write-Atomic([string]$Path, [string]$Content, [Text.Encoding]$Encoding) {
    $taskTemp = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    $taskBackup = $taskTemp + '.previous'
    try {
        [IO.File]::WriteAllText($taskTemp, $Content, $Encoding)
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($taskTemp, $Path, $taskBackup) }
        else { [IO.File]::Move($taskTemp, $Path) }
    } finally {
        if (Test-Path -LiteralPath $taskTemp) { Remove-Item -LiteralPath $taskTemp }
        if (Test-Path -LiteralPath $taskBackup) { Remove-Item -LiteralPath $taskBackup }
    }
}
$dataDir = Join-Path $env:LOCALAPPDATA 'CampusEthernetAutoLogin'
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
$secretPath = Join-Path $dataDir 'credential.json'
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
try {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    $bytes = [Text.Encoding]::UTF8.GetBytes($plain)
    $encrypted = [Security.Cryptography.ProtectedData]::Protect(
        $bytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $passwordBlob = [Convert]::ToBase64String($encrypted)
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    if ($bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
}
Write-Atomic $secretPath (@{ username = $Username; password = $passwordBlob } | ConvertTo-Json -Compress) ([Text.Encoding]::UTF8)

$startup = [Environment]::GetFolderPath('Startup')
$launcher = Join-Path $startup 'CampusEthernetAutoLogin.vbs'
$settingsPath = Join-Path $dataDir 'settings.json'
if (Test-Path -LiteralPath $settingsPath) {
    $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
} else {
    $settings = [pscustomobject]@{ autoLogin = $true; priorityMode = 'Off'; quickRecheck = $true }
}
$settings.autoLogin = $true
Write-Atomic $settingsPath ($settings | ConvertTo-Json -Compress) ([Text.Encoding]::UTF8)
$escapedExePath = $exePath.Replace('"', '""')
$vbs = 'CreateObject("WScript.Shell").Run """{0}"" --background --startup", 0, False' -f $escapedExePath
Write-Atomic $launcher $vbs ([Text.Encoding]::Unicode)

Write-Output "已安装开机自动登录：$launcher"
Write-Output "加密账号配置：$secretPath"
Write-Output "日志：$(Join-Path $dataDir 'run.log')"
