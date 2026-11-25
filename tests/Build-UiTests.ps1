param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'ui-build'))
$ErrorActionPreference='Stop'
$base=Split-Path -Parent $PSScriptRoot
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$compiler='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources=@('CampusNetHelper.cs','NetworkSpeed.cs','AppSettings.cs','PortalProbe.cs','PortalClient.cs','CampusLogin.cs','ConnectionMonitor.cs','DashboardWindow.cs','DashboardWindow.Designer.cs','DashboardWindow.Surfaces.Designer.cs','EnvironmentCheck.cs','RuntimeSafety.cs','TransferEngine.cs','SpeedupView.cs','WindowBehavior.cs','AntdUiControls.cs','UiMotion.cs','DesignControls.cs','BandwidthTestEngine.cs','NetworkSpeedTestView.cs','NetworkSpeedTestView.Designer.cs','CampusStability.cs','CampusStabilityView.cs','CampusDiagnostics.cs') | ForEach-Object {Join-Path $base $_}
$antdui=Join-Path $base 'lib\AntdUI.2.3.0\AntdUI.dll'
Copy-Item -LiteralPath $antdui -Destination $OutputDirectory -Force
foreach($test in @('DesktopInteractionVerify','DashboardWindowBehaviorVerify','CaptionCaptureVerify','AntdComponentsVerify','DiagnosisUiVerify','BandwidthViewVerify','UiExperienceVerify','ResizeAndPaintVerify','NavigationIndicatorVerify','RestoreSurfaceVerify','DesignSurfaceVerify','PresentationVerify','CampusStabilityVerify','StatusHeaderVerify')) {
    & $compiler /nologo /target:exe /codepage:65001 ("/main:$test") ("/out:"+(Join-Path $OutputDirectory ($test+'.exe'))) ('/win32manifest:'+(Join-Path $base 'app.manifest')) ('/reference:'+$antdui) /reference:System.Data.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Security.dll /reference:System.Web.Extensions.dll $sources (Join-Path $PSScriptRoot ($test+'.cs'))
    if($LASTEXITCODE -ne 0){throw "Test build failed: $test"}
}
# Keep the interactive host windowed so an attached console cannot steal input.
& $compiler /nologo /target:winexe /codepage:65001 /main:DiagnosisUiVerify ("/out:"+(Join-Path $OutputDirectory 'DiagnosisUiHost.exe')) ('/win32manifest:'+(Join-Path $base 'app.manifest')) ('/reference:'+$antdui) /reference:System.Data.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Security.dll /reference:System.Web.Extensions.dll $sources (Join-Path $PSScriptRoot 'DiagnosisUiVerify.cs')
if($LASTEXITCODE -ne 0){throw 'Windowed UI host build failed'}
& $compiler /nologo /target:winexe /codepage:65001 /main:DesktopInteractionVerify ("/out:"+(Join-Path $OutputDirectory 'DesktopInteractionUiHost.exe')) ('/win32manifest:'+(Join-Path $base 'app.manifest')) ('/reference:'+$antdui) /reference:System.Data.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Security.dll /reference:System.Web.Extensions.dll $sources (Join-Path $PSScriptRoot 'DesktopInteractionVerify.cs')
if($LASTEXITCODE -ne 0){throw 'Window behavior UI host build failed'}
& $compiler /nologo /target:winexe /codepage:65001 /main:UiExperienceVerify ("/out:"+(Join-Path $OutputDirectory 'UiExperienceHost.exe')) ('/win32manifest:'+(Join-Path $base 'app.manifest')) ('/reference:'+$antdui) /reference:System.Data.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Security.dll /reference:System.Web.Extensions.dll $sources (Join-Path $PSScriptRoot 'UiExperienceVerify.cs')
if($LASTEXITCODE -ne 0){throw 'UI experience host build failed'}

