param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$base=Split-Path -Parent $PSScriptRoot
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$config=Join-Path $OutputDirectory 'configs'
$data=Join-Path $OutputDirectory 'data'
[IO.Directory]::CreateDirectory($config) | Out-Null
$utf8=New-Object Text.UTF8Encoding($false)
function Assert($condition, [string]$message) { if(-not $condition) { throw $message }; Write-Output "PASS $message" }
foreach($tool in @('pip','npm','go','cargo')) {
    $path=Join-Path $config ($tool+'.config')
    $original=switch($tool) {
        'pip' { "[global]`r`ntimeout=47`r`nindex-url=https://old.example/simple`r`n[install]`r`nno-cache-dir=true`r`n" }
        'npm' { "//old.example/:_authToken=FAKE_LOCAL_TEST_TOKEN`r`nregistry=https://old.example/`r`nstrict-ssl=true`r`n" }
        'go' { "GOPROXY=https://old.example/`nGOSUMDB=sum.golang.org`n" }
        'cargo' { "[build]`n jobs=3`n[source.crates-io]`nreplace-with=`"old-mirror`"`n[source.old-mirror]`nregistry=`"https://old.example/index`"`n" }
    }
    [IO.File]::WriteAllText($path,$original,$utf8)
    $before=[Convert]::ToBase64String([IO.File]::ReadAllBytes($path))
    $result= & (Join-Path $base 'MirrorConfig.ps1') -Action Apply -Tool $tool -Provider tencent -ConfigRoot $config -DataRoot $data
    $parsed=($result -join '') | ConvertFrom-Json
    Assert $parsed.ok "$tool apply"
    $after=[IO.File]::ReadAllText($path)
    Assert ($after -match 'cloud.tencent.com') "$tool changed intended source"
    $preserved=switch($tool) {'pip' {$after -match 'timeout=47' -and $after -match 'no-cache-dir=true'} 'npm' {$after -match 'FAKE_LOCAL_TEST_TOKEN' -and $after -match 'strict-ssl=true'} 'go' {$after -match 'GOSUMDB=sum.golang.org'} 'cargo' {$after -match 'jobs=3' -and $after -match 'source.old-mirror'}}
    Assert $preserved "$tool unrelated settings preserved"
    $null= & (Join-Path $base 'MirrorConfig.ps1') -Action Apply -Tool $tool -Provider aliyun -ConfigRoot $config -DataRoot $data
    if($tool -eq 'cargo') {
        $null= & (Join-Path $base 'MirrorConfig.ps1') -Action Apply -Tool $tool -Provider official -ConfigRoot $config -DataRoot $data
        Assert ([IO.File]::ReadAllText($path) -notmatch 'replace-with=') 'cargo official removes replacement'
    }
    $result= & (Join-Path $base 'MirrorConfig.ps1') -Action Restore -Tool $tool -ConfigRoot $config -DataRoot $data
    Assert ((($result -join '') | ConvertFrom-Json).ok) "$tool restore"
    Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -eq $before) "$tool byte-exact original restored after multiple switches"
}
$newPath=Join-Path $config 'pip.config'; [IO.File]::Delete($newPath)
$result= & (Join-Path $base 'MirrorConfig.ps1') -Action Apply -Tool pip -Provider tencent -ConfigRoot $config -DataRoot $data
Assert ((($result -join '') | ConvertFrom-Json).ok) 'new configuration created'
$null= & (Join-Path $base 'MirrorConfig.ps1') -Action Restore -Tool pip -ConfigRoot $config -DataRoot $data
Assert (-not (Test-Path -LiteralPath $newPath)) 'original absent configuration restored'
[IO.File]::WriteAllText($newPath,'',$utf8)
$result= & (Join-Path $base 'MirrorConfig.ps1') -Action Apply -Tool pip -Provider tencent -ConfigRoot $config -DataRoot $data
Assert ((($result -join '') | ConvertFrom-Json).ok) 'empty original configuration can be changed'
$null= & (Join-Path $base 'MirrorConfig.ps1') -Action Restore -Tool pip -ConfigRoot $config -DataRoot $data
Assert ((Get-Item -LiteralPath $newPath).Length -eq 0) 'empty original configuration restored exactly'

# Test the production DNS workflow against isolated cmdlet doubles. No real DNS writes.
$scriptText=[IO.File]::ReadAllText((Join-Path $base 'DnsConfig.ps1'))
$scriptText=$scriptText.Replace('if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))', 'if ($false)')
$mockScript=Join-Path $OutputDirectory 'DnsConfig-isolated.ps1'
[IO.File]::WriteAllText($mockScript,$scriptText,(New-Object Text.UTF8Encoding($true)))
$env:LOCALAPPDATA=Join-Path $OutputDirectory 'local-app-data'
$global:FakeDns=@('10.1.2.3'); $global:FakeStatic='10.1.2.3'; $global:FakeRules=@([pscustomobject]@{Name='unrelated'; Namespace=@('.example.org')})
$global:FakeFail=$false; $global:FakeIpv6Touches=0
$fakeGuid='11111111-2222-3333-4444-555555555555'
function Get-NetAdapter { [CmdletBinding()] param($InterfaceIndex); [pscustomobject]@{InterfaceGuid=$fakeGuid} }
function Get-ItemProperty { [CmdletBinding()] param($LiteralPath,$Name); [pscustomobject]@{NameServer=$global:FakeStatic} }
function Get-DnsClientServerAddress { [CmdletBinding()] param($InterfaceIndex,$AddressFamily); [pscustomobject]@{ServerAddresses=@($global:FakeDns); AddressFamily=$AddressFamily} }
function Set-DnsClientServerAddress {
    [CmdletBinding()] param([Parameter(ValueFromPipeline=$true)]$InputObject,$ServerAddresses,[switch]$ResetServerAddresses)
    process { if($InputObject.AddressFamily -ne 'IPv4'){ $global:FakeIpv6Touches++ }; if($global:FakeFail){ $global:FakeFail=$false; throw 'injected failure' }
        if($ResetServerAddresses){$global:FakeDns=@('DHCP')}else{$global:FakeDns=@($ServerAddresses)} }
}
function Get-DnsClientNrptRule { [CmdletBinding()] param($Name); if($Name){$global:FakeRules | Where-Object Name -eq $Name}else{$global:FakeRules} }
function Add-DnsClientNrptRule { [CmdletBinding()] param($Namespace,$NameServers,$DisplayName,[switch]$PassThru)
    $item=[pscustomobject]@{Name=[guid]::NewGuid().ToString(); Namespace=@($Namespace)}; $global:FakeRules+=@($item); $item }
function Remove-DnsClientNrptRule { [CmdletBinding()] param([Parameter(ValueFromPipeline=$true)]$InputObject,[switch]$Force)
    process{$global:FakeRules=@($global:FakeRules | Where-Object Name -ne $InputObject.Name)} }
function Clear-DnsClientCache { }
$report=Join-Path $OutputDirectory 'dns-result.json'
& $mockScript -Action Apply -InterfaceIndex 9 -InterfaceGuid $fakeGuid -Provider tencent -ResultPath $report
Assert ((Get-Content $report -Raw -Encoding UTF8 | ConvertFrom-Json).ok) 'DNS apply using isolated cmdlets'
Assert ($global:FakeDns[0] -eq '119.29.29.29' -and $global:FakeRules.Count -eq 3) 'DNS addresses and owned split rules'
& $mockScript -Action Restore -InterfaceIndex 9 -InterfaceGuid $fakeGuid -ResultPath $report
Assert ((Get-Content $report -Raw -Encoding UTF8 | ConvertFrom-Json).ok -and $global:FakeDns[0] -eq '10.1.2.3' -and $global:FakeRules.Count -eq 1 -and $global:FakeRules[0].Name -eq 'unrelated') 'DNS original static mode restored, unrelated rules retained'
$global:FakeStatic=''; $global:FakeDns=@('DHCP');
& $mockScript -Action Apply -InterfaceIndex 9 -InterfaceGuid $fakeGuid -Provider aliyun -ResultPath $report
& $mockScript -Action Restore -InterfaceIndex 9 -InterfaceGuid $fakeGuid -ResultPath $report
Assert ($global:FakeDns[0] -eq 'DHCP') 'DNS original automatic mode restored'
$global:FakeStatic='10.1.2.3'; $global:FakeDns=@('10.1.2.3'); $global:FakeFail=$true
& $mockScript -Action Apply -InterfaceIndex 9 -InterfaceGuid $fakeGuid -Provider huawei -ResultPath $report
Assert (-not (Get-Content $report -Raw -Encoding UTF8 | ConvertFrom-Json).ok -and $global:FakeDns[0] -eq '10.1.2.3' -and $global:FakeRules.Count -eq 1) 'DNS failure rolls back addresses and owned rules'
Assert ($global:FakeIpv6Touches -eq 0) 'IPv6 DNS never modified'
& $mockScript -Action Restore -InterfaceIndex 9 -InterfaceGuid $fakeGuid -ResultPath $report
$global:FakeRules+=@([pscustomobject]@{Name='prior-school'; Namespace=@('.usc.edu.cn'); NameServers=@('210.43.112.1')},[pscustomobject]@{Name='prior-root'; Namespace=@('usc.edu.cn'); NameServers=@('210.43.112.1')})
& $mockScript -Action Apply -InterfaceIndex 9 -InterfaceGuid $fakeGuid -Provider tencent -ResultPath $report
Assert ((Get-Content $report -Raw -Encoding UTF8 | ConvertFrom-Json).ok -and $global:FakeRules.Count -eq 3) 'compatible pre-existing split rules reused'
& $mockScript -Action Restore -InterfaceIndex 9 -InterfaceGuid $fakeGuid -ResultPath $report
Assert ($global:FakeRules.Count -eq 3) 'pre-existing split rules survive restore'
$global:FakeRules[1].NameServers=@('10.8.8.8')
& $mockScript -Action Apply -InterfaceIndex 9 -InterfaceGuid $fakeGuid -Provider tencent -ResultPath $report
Assert (-not (Get-Content $report -Raw -Encoding UTF8 | ConvertFrom-Json).ok -and $global:FakeDns[0] -eq '10.1.2.3') 'conflicting split rules refuse change'
