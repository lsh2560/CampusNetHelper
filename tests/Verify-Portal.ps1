param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'portal-output'))
# Builds and runs tests\PortalClientVerify.cs: the campus portal protocol and the
# login safeguards, against loopback fixtures only (no school server, no Node.js).
$ErrorActionPreference = 'Stop'
$base = Split-Path -Parent $PSScriptRoot
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @('CampusNetHelper.cs','NetworkSpeed.cs','AppSettings.cs','PortalProbe.cs','PortalClient.cs','CampusLogin.cs','ConnectionMonitor.cs','DashboardWindow.cs','DashboardWindow.Designer.cs','DashboardWindow.Surfaces.Designer.cs','DesignControls.cs','EnvironmentCheck.cs','RuntimeSafety.cs','TransferEngine.cs','SpeedupView.cs','WindowBehavior.cs','AntdUiControls.cs','UiMotion.cs','BandwidthTestEngine.cs','NetworkSpeedTestView.cs','NetworkSpeedTestView.Designer.cs','CampusStability.cs','CampusStabilityView.cs','CampusDiagnostics.cs','VersionInfo.cs') | ForEach-Object { Join-Path $base $_ }
$antdui = Join-Path $base 'lib\AntdUI.2.3.0\AntdUI.dll'
Copy-Item -LiteralPath $antdui -Destination (Join-Path $OutputDirectory 'AntdUI.dll') -Force
$executable = Join-Path $OutputDirectory 'PortalClientVerify.exe'
& $compiler /nologo /target:exe /main:PortalClientVerify /codepage:65001 /out:$executable ('/reference:' + $antdui) /reference:System.Data.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Security.dll /reference:System.Web.Extensions.dll $sources (Join-Path $PSScriptRoot 'PortalClientVerify.cs')
if ($LASTEXITCODE -ne 0) { throw 'Portal verification build failed' }
& $executable $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Portal verification failed' }
