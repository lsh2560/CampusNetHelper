param(
    [ValidateSet('Apply','Restore')][string]$Action = 'Apply',
    [int]$InterfaceIndex,
    [string]$InterfaceGuid,
    [ValidateSet('tencent','aliyun','huawei','campus','automatic')][string]$Provider = 'automatic',
    [Parameter(Mandatory)][string]$ResultPath
)
$ErrorActionPreference = 'Stop'
$stateDir = Join-Path $env:LOCALAPPDATA 'CampusEthernetAutoLogin\dns-backups'
$utf8 = New-Object Text.UTF8Encoding($false)
$guard = $null; $taken = $false
function Save-Json([string]$Path, $Value) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    $temp = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temp, ($Value | ConvertTo-Json -Depth 8 -Compress), $utf8)
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temp, $Path, [NullString]::Value) } else { [IO.File]::Move($temp, $Path) }
    } finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
}
function Restore-Dns($Snapshot, [int]$Index) {
    $ipv4 = Get-DnsClientServerAddress -InterfaceIndex $Index -AddressFamily IPv4
    if ($Snapshot.automatic) { $ipv4 | Set-DnsClientServerAddress -ResetServerAddresses }
    else { $ipv4 | Set-DnsClientServerAddress -ServerAddresses @($Snapshot.addresses) }
}
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '需要管理员权限才能修改 DNS。' }
    $guard = New-Object Threading.Mutex($false, ('Local\CampusDnsConfig-' + $identity.User.Value))
    try { $taken = $guard.WaitOne(0) } catch [Threading.AbandonedMutexException] { $taken = $true }
    if (-not $taken) { throw '另一个 DNS 配置任务正在运行。' }
    $adapter = Get-NetAdapter -InterfaceIndex $InterfaceIndex -ErrorAction Stop
    $guid = ([guid]$adapter.InterfaceGuid).ToString('D')
    if ($guid -ne ([guid]$InterfaceGuid).ToString('D')) { throw '网卡发生变化，请刷新网卡列表后重试。' }
    $backup = Join-Path $stateDir ($guid + '.json')
    $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{' + $guid + '}'
    $staticDns = (Get-ItemProperty -LiteralPath $key -Name NameServer -ErrorAction SilentlyContinue).NameServer
    $before = [pscustomobject]@{ guid=$guid; automatic=[string]::IsNullOrWhiteSpace($staticDns); addresses=@((Get-DnsClientServerAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4).ServerAddresses); rules=@() }
    $snapshot = if (Test-Path -LiteralPath $backup) { Get-Content -LiteralPath $backup -Raw -Encoding UTF8 | ConvertFrom-Json } else { $before }
    if ($Action -eq 'Restore') {
        if (-not (Test-Path -LiteralPath $backup)) { throw '此网卡没有由助手创建的 DNS 备份。' }
        Restore-Dns $snapshot $InterfaceIndex
        foreach ($rule in @($snapshot.rules)) { Get-DnsClientNrptRule -Name $rule -ErrorAction SilentlyContinue | Remove-DnsClientNrptRule -Force }
        [IO.File]::Delete($backup)
        Clear-DnsClientCache
        Save-Json $ResultPath @{ok=$true; message='已恢复原 DNS 模式和地址，并删除助手创建的校内分流规则。'}
        exit 0
    }
    $servers = switch ($Provider) {
        'tencent' { @('119.29.29.29','182.254.116.116') }
        'aliyun' { @('223.5.5.5','223.6.6.6') }
        'huawei' { @('122.112.208.1','139.9.23.90') }
        'campus' { @('210.43.112.1') }
        'automatic' { @() }
    }
    if (-not (Test-Path -LiteralPath $backup)) { Save-Json $backup $snapshot }
    $added = @()
    try {
        if ($Provider -in @('tencent','aliyun','huawei')) {
            $existing = @(Get-DnsClientNrptRule)
            foreach ($namespace in @('usc.edu.cn','.usc.edu.cn')) {
                $conflict = @($existing | Where-Object { $namespace -in $_.Namespace -and $_.Name -notin @($snapshot.rules) })
                if ($conflict.Count -gt 0) {
                    foreach ($existingRule in $conflict) {
                        $existingServers = @((@($existingRule.NameServers) -join ',') -split '[,;\s]+' | Where-Object { $_ })
                        if ($existingServers.Count -ne 1 -or $existingServers[0] -ne '210.43.112.1') { throw '已有不同的校内域名分流规则，助手保留原规则。请由管理员确认是否兼容。' }
                    }
                    continue # Compatible pre-existing rules remain owned by their original creator.
                }
                if (-not @($existing | Where-Object { $namespace -in $_.Namespace -and $_.Name -in @($snapshot.rules) }).Count) {
                    $rule = Add-DnsClientNrptRule -Namespace $namespace -NameServers '210.43.112.1' -DisplayName ('CampusNetHelper-' + $guid) -PassThru
                    $added += $rule.Name
                    $snapshot.rules = @($snapshot.rules) + @($rule.Name)
                    Save-Json $backup $snapshot
                }
            }
        }
        if ($servers.Count -eq 0) {
            Get-DnsClientServerAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 | Set-DnsClientServerAddress -ResetServerAddresses
        } else {
            Get-DnsClientServerAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 | Set-DnsClientServerAddress -ServerAddresses $servers
        }
        Clear-DnsClientCache
        Save-Json $ResultPath @{ok=$true; message='DNS 已更新（仅 IPv4）。外部 DNS 保留南华校内域名解析；IPv6 DNS 保持原设置。请按需测试校内网站。'}
    } catch {
        try { Restore-Dns $before $InterfaceIndex } catch { }
        foreach ($rule in $added) { Get-DnsClientNrptRule -Name $rule -ErrorAction SilentlyContinue | Remove-DnsClientNrptRule -Force }
        $snapshot.rules = @($snapshot.rules | Where-Object { $_ -notin $added })
        Save-Json $backup $snapshot
        throw
    }
} catch { Save-Json $ResultPath @{ok=$false; message=$_.Exception.Message}; exit 1 }
finally { if ($taken) { $guard.ReleaseMutex() }; if ($guard) { $guard.Dispose() } }
