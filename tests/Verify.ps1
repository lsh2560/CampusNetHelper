param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'output'))
$ErrorActionPreference = 'Stop'
$base = Split-Path -Parent $PSScriptRoot
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @('CampusNetHelper.cs', 'NetworkSpeed.cs', 'AppSettings.cs', 'PortalProbe.cs', 'PortalClient.cs', 'CampusLogin.cs', 'ConnectionMonitor.cs', 'DashboardWindow.cs', 'DashboardWindow.Designer.cs','DashboardWindow.Surfaces.Designer.cs','DesignControls.cs', 'EnvironmentCheck.cs', 'RuntimeSafety.cs', 'TransferEngine.cs', 'SpeedupView.cs', 'WindowBehavior.cs', 'AntdUiControls.cs','UiMotion.cs','BandwidthTestEngine.cs','NetworkSpeedTestView.cs','NetworkSpeedTestView.Designer.cs','CampusStability.cs','CampusStabilityView.cs','CampusDiagnostics.cs') | ForEach-Object { Join-Path $base $_ }
$executable = Join-Path $OutputDirectory 'Verify.exe'
$antdui=Join-Path $base 'lib\AntdUI.2.3.0\AntdUI.dll'
Copy-Item -LiteralPath $antdui -Destination (Join-Path $OutputDirectory 'AntdUI.dll') -Force
& $compiler /nologo /target:exe /main:Verify /codepage:65001 /out:$executable ('/reference:'+$antdui) /reference:System.Data.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Security.dll /reference:System.Web.Extensions.dll $sources (Join-Path $PSScriptRoot 'Verify.cs')
if ($LASTEXITCODE -ne 0) { throw 'Verification build failed' }
& $executable $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Verification failed' }
Get-Content (Join-Path $OutputDirectory 'verification.txt')
