param([Parameter(Mandatory)][string]$ToolDirectory)
$ErrorActionPreference='Stop'
$ToolDirectory=[IO.Path]::GetFullPath($ToolDirectory)
[IO.Directory]::CreateDirectory($ToolDirectory) | Out-Null
$archive=Join-Path $ToolDirectory 'winappcli-x64.zip'
if(-not(Test-Path -LiteralPath $archive)){
    Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/microsoft/winappCli/releases/download/v0.7.1/winappcli-x64.zip' -OutFile $archive
}
if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne 'D925D1E32CDC320B6D271F653FD2CD3C05B5D7558DEB20D1B548BEAD77900FCF'){throw 'Official WinApp CLI archive checksum mismatch'}
Expand-Archive -LiteralPath $archive -DestinationPath $ToolDirectory -Force
Write-Output (Join-Path $ToolDirectory 'winapp.exe')
