param([string]$Destination)

$ErrorActionPreference = 'Stop'
$base = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path (Split-Path -Parent $base) 'CampusNetHelper-Setup-0.10.0.exe'
}
$Destination = [IO.Path]::GetFullPath($Destination)
$stage = Join-Path $env:TEMP ('campus-release-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $env:TEMP ('campus-payload-' + [Guid]::NewGuid().ToString('N') + '.zip')
$tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
$stageFull = [IO.Path]::GetFullPath($stage)
if (-not $stageFull.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw '发布暂存目录不在临时目录内。' }

try {
    # Authentication is built into CampusNetHelper.exe; no Node.js runtime or login script is packaged.
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    & (Join-Path $base 'Build-App.ps1') -OutputDirectory $stage -NoShortcuts
    if ($LASTEXITCODE -ne 0) { throw '程序编译失败。' }
    Copy-Item -LiteralPath (Join-Path $base 'MirrorConfig.ps1') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $base 'DnsConfig.ps1') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $base 'README.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $base '校内网络稳定性.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $base '优化说明.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $base 'THIRD_PARTY_NOTICES.md') -Destination $stage

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Remove-Item -LiteralPath (Join-Path $stage 'IconMaker.exe') -ErrorAction SilentlyContinue
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $payload -CompressionLevel Optimal -Force -ErrorAction Stop
            break
        } catch {
            if ($attempt -eq 3) { throw }
            if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Force }
            Start-Sleep -Seconds 2
        }
    }

    $compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path $compiler)) { throw '没有找到 C# 编译器。' }
    $icon = Join-Path $stage 'CampusNetHelper.ico'
    $compilerArgs = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001',
        "/out:$Destination", "/win32icon:$icon", ('/win32manifest:' + (Join-Path $base 'app.manifest')), '/reference:System.Windows.Forms.dll',
        '/reference:System.Drawing.dll', '/reference:System.IO.Compression.dll',
        '/reference:System.IO.Compression.FileSystem.dll', '/reference:System.Web.Extensions.dll',
        '/reference:Microsoft.CSharp.dll', "/resource:$payload,CampusPayload", (Join-Path $base 'Installer.cs'), (Join-Path $base 'RuntimeSafety.cs'), (Join-Path $base 'VersionInfo.cs'))
    & $compiler @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw '安装程序编译失败。' }
    $verification = Start-Process -FilePath $Destination -ArgumentList '--verify-payload' -Wait -PassThru -WindowStyle Hidden
    if ($verification.ExitCode -ne 0) { throw "安装程序内置文件校验失败（退出码 $($verification.ExitCode)）。" }
    Write-Output "免配置安装包已生成：$Destination"
    Write-Output "SHA256：$((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash)"
} finally {
    if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload }
    if (Test-Path -LiteralPath $stageFull) { Remove-Item -LiteralPath $stageFull -Recurse -Force }
}
