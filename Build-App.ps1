param([string]$OutputDirectory, [switch]$NoShortcuts)

$ErrorActionPreference = 'Stop'
$base = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = $base }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $compiler)) { throw '没有找到 Windows 自带的 C# 编译器。' }
$iconMaker = Join-Path $OutputDirectory 'IconMaker.exe'
$icon = Join-Path $OutputDirectory 'CampusNetHelper.ico'
$preview = Join-Path $OutputDirectory 'CampusNetHelper-icon-preview.png'
& $compiler /nologo /target:exe /optimize+ /codepage:65001 /out:$iconMaker /reference:System.Drawing.dll (Join-Path $base 'IconMaker.cs')
if ($LASTEXITCODE -ne 0) { throw '图标工具编译失败。' }
& $iconMaker (Join-Path $base 'icon-source.png') $icon $preview
if ($LASTEXITCODE -ne 0) { throw '图标生成失败。' }
$output = Join-Path $OutputDirectory 'CampusNetHelper.exe'
$extraSources = @('TransferEngine.cs','SpeedupView.cs','WindowBehavior.cs','AntdUiControls.cs','UiMotion.cs','DesignControls.cs','BandwidthTestEngine.cs','NetworkSpeedTestView.cs','NetworkSpeedTestView.Designer.cs','CampusStability.cs','CampusStabilityView.cs','CampusDiagnostics.cs','DashboardWindow.Designer.cs','DashboardWindow.Surfaces.Designer.cs') | ForEach-Object { Join-Path $base $_ }
$antdui = Join-Path $base 'lib\AntdUI.2.3.0\AntdUI.dll'
if ((Get-FileHash -LiteralPath $antdui -Algorithm SHA256).Hash -ne '407B39BDE1EF89AB4AA9B37B9E1786E0229E2FB58F1A246EB146C08C90F611BD') { throw 'AntdUI 依赖校验失败。' }
$uiReference = '/reference:' + $antdui
$manifest = '/win32manifest:' + (Join-Path $base 'app.manifest')
& $compiler /nologo /target:winexe /optimize+ /codepage:65001 /win32icon:$icon /out:$output $manifest $uiReference /reference:System.Data.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Security.dll /reference:System.Web.Extensions.dll (Join-Path $base 'CampusNetHelper.cs') (Join-Path $base 'NetworkSpeed.cs') (Join-Path $base 'AppSettings.cs') (Join-Path $base 'PortalProbe.cs') (Join-Path $base 'PortalClient.cs') (Join-Path $base 'CampusLogin.cs') (Join-Path $base 'ConnectionMonitor.cs') (Join-Path $base 'DashboardWindow.cs') (Join-Path $base 'EnvironmentCheck.cs') (Join-Path $base 'RuntimeSafety.cs') (Join-Path $base 'VersionInfo.cs') $extraSources
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
if ([IO.Path]::GetFullPath($antdui) -ne [IO.Path]::GetFullPath((Join-Path $OutputDirectory 'AntdUI.dll'))) { Copy-Item -LiteralPath $antdui -Destination (Join-Path $OutputDirectory 'AntdUI.dll') -Force }
Copy-Item -LiteralPath (Join-Path $base 'lib\AntdUI.2.3.0\LICENSE') -Destination (Join-Path $OutputDirectory 'LICENSE.AntdUI.txt') -Force
if (-not $NoShortcuts) {
    $shell = New-Object -ComObject WScript.Shell
    $links = @((Join-Path ([Environment]::GetFolderPath('Desktop')) '稳连.lnk'), (Join-Path ([Environment]::GetFolderPath('Programs')) '稳连.lnk'))
    foreach ($linkPath in $links) {
        $link = $shell.CreateShortcut($linkPath)
        $link.TargetPath = $output
        $link.WorkingDirectory = $OutputDirectory
        $link.IconLocation = "$icon,0"
        $link.Description = '稳连 · 校园网助手（非官方）'
        $link.Save()
    }
}
Write-Output "软件已生成：$output"
if (-not $NoShortcuts) { Write-Output '桌面和开始菜单快捷方式已创建。' }
