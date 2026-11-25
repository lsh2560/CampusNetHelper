# One-click: compile the app and the style preview with Windows' built-in C#
# compiler, then render all pages to tests\style-preview\*.png.
$ErrorActionPreference = 'Stop'
$base = Split-Path -Parent $PSScriptRoot
$out = Join-Path $base 'preview-build'
$shots = Join-Path $PSScriptRoot 'style-preview'
New-Item -ItemType Directory -Force -Path $out, $shots | Out-Null
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $shots '*.png'), (Join-Path $shots 'preview-*.txt'), (Join-Path $out 'build-*.txt')
$log = Join-Path $out 'build-log.txt'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$antdui = Join-Path $base 'lib\AntdUI.2.3.0\AntdUI.dll'
$sources = @('CampusNetHelper.cs','NetworkSpeed.cs','AppSettings.cs','PortalProbe.cs','PortalClient.cs','CampusLogin.cs','ConnectionMonitor.cs','DashboardWindow.cs','DashboardWindow.Designer.cs','DashboardWindow.Surfaces.Designer.cs','EnvironmentCheck.cs','RuntimeSafety.cs','VersionInfo.cs','TransferEngine.cs','SpeedupView.cs','WindowBehavior.cs','AntdUiControls.cs','UiMotion.cs','DesignControls.cs','BandwidthTestEngine.cs','NetworkSpeedTestView.cs','NetworkSpeedTestView.Designer.cs','CampusStability.cs','CampusStabilityView.cs','CampusDiagnostics.cs') | ForEach-Object { Join-Path $base $_ }
$refs = @("/reference:$antdui", '/reference:System.Data.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Security.dll', '/reference:System.Web.Extensions.dll')
Copy-Item -LiteralPath $antdui -Destination $out -Force
foreach ($asset in 'CampusNetHelper.ico','CampusNetHelper-icon-preview.png','MirrorConfig.ps1','DnsConfig.ps1') {
    $p = Join-Path $base $asset; if (Test-Path $p) { Copy-Item -LiteralPath $p -Destination $out -Force }
}
"== app ==" | Out-File $log -Encoding utf8
& $compiler /nologo /target:winexe /optimize+ /codepage:65001 ("/win32icon:" + (Join-Path $base 'CampusNetHelper.ico')) ("/win32manifest:" + (Join-Path $base 'app.manifest')) ("/out:" + (Join-Path $out 'CampusNetHelper.exe')) $refs $sources 2>&1 | Out-File $log -Append -Encoding utf8
$appCode = $LASTEXITCODE
"== preview ==" | Out-File $log -Append -Encoding utf8
& $compiler /nologo /target:winexe /codepage:65001 /main:StylePreview ("/win32manifest:" + (Join-Path $base 'app.manifest')) ("/out:" + (Join-Path $out 'StylePreview.exe')) $refs $sources (Join-Path $PSScriptRoot 'StylePreview.cs') 2>&1 | Out-File $log -Append -Encoding utf8
$previewCode = $LASTEXITCODE
"app=$appCode preview=$previewCode" | Out-File $log -Append -Encoding utf8
if ($previewCode -eq 0) {
    $p = Start-Process -FilePath (Join-Path $out 'StylePreview.exe') -ArgumentList ('"' + $shots + '"') -WorkingDirectory $out -PassThru -Wait
    "preview exit=$($p.ExitCode)" | Out-File $log -Append -Encoding utf8
}
"done" | Out-File (Join-Path $out 'build-done.txt') -Encoding utf8
