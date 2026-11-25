param(
    [ValidateSet('Status','Apply','Restore')][string]$Action = 'Status',
    [ValidateSet('pip','npm','conda','go','cargo')][string]$Tool = 'pip',
    [ValidateSet('tencent','huawei','aliyun','official')][string]$Provider = 'official',
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'CampusEthernetAutoLogin'),
    [string]$ConfigRoot
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Security
function Write-AtomicBytes([string]$Path, [byte[]]$Bytes) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    $temp = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllBytes($temp, $Bytes)
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temp, $Path, [NullString]::Value) }
        else { [IO.File]::Move($temp, $Path) }
    } finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
}
function Set-SectionKey([string]$Text, [string]$Section, [string]$Key, [string]$Value) {
    $lines = New-Object 'Collections.Generic.List[string]'
    $active = $false; $foundSection = $false; $written = $false
    foreach ($line in ($Text -split '\r?\n')) {
        if ($line -match '^\s*\[([^\]]+)\]\s*(?:[#;].*)?$') {
            if ($active -and -not $written) { $lines.Add("$Key=$Value"); $written = $true }
            $active = $Matches[1] -eq $Section
            if ($active) { $foundSection = $true }
        }
        if ($active -and $line -match ('^\s*' + [regex]::Escape($Key) + '\s*=')) {
            if (-not $written) { $lines.Add("$Key=$Value"); $written = $true }
        } else { $lines.Add($line) }
    }
    if (-not $foundSection) { $lines.Add("`r`n[$Section]") }
    if (-not $written) { $lines.Add("$Key=$Value") }
    return $lines -join "`r`n"
}
function Native([string]$Exe, [string[]]$NativeArguments) {
    $result = & $Exe @NativeArguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ('配置工具返回错误：' + ($result -join ' ')) }
}
$guard = $null; $taken = $false
$failureCode = 'configuration_error'; $failureHttpStatus = $null; $failureRetryAfterSeconds = $null
try {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $guard = New-Object Threading.Mutex($false, ('Local\CampusMirrorConfig-' + $sid))
    try { $taken = $guard.WaitOne(0) } catch [Threading.AbandonedMutexException] { $taken = $true }
    if (-not $taken) { throw '另一个换源任务正在运行，请稍后重试。' }
    $paths = @{
        pip = (Join-Path $env:APPDATA 'pip\pip.ini')
        npm = (Join-Path $env:USERPROFILE '.npmrc')
        conda = (Join-Path $env:USERPROFILE '.condarc')
        go = (Join-Path $env:APPDATA 'go\env')
        cargo = (Join-Path $env:USERPROFILE '.cargo\config.toml')
    }
    if ($env:CARGO_HOME) { $paths.cargo = Join-Path $env:CARGO_HOME 'config.toml' }
    if ($env:CONDARC) { $paths.conda = $env:CONDARC }
    if ($env:GOENV -and $env:GOENV -ne 'off') { $paths.go = $env:GOENV }
    if ($env:PIP_CONFIG_FILE -and $env:PIP_CONFIG_FILE -ne 'nul') { $paths.pip = $env:PIP_CONFIG_FILE }
    if ($ConfigRoot) { foreach ($key in @($paths.Keys)) { $paths[$key] = Join-Path $ConfigRoot ($key + '.config') } }
    $stateDir = Join-Path $DataRoot 'mirror-backups'
    $backup = Join-Path $stateDir ($Tool + '.json')
    if ($Action -eq 'Status') {
        $items = foreach ($key in @('pip','npm','conda','go','cargo')) {
            $present = Test-Path -LiteralPath $paths[$key]
            $description = '使用默认配置'
            if ($present) {
                $text = [IO.File]::ReadAllText($paths[$key])
                # Only display relevant source URLs; never return auth tokens from .npmrc etc.
                $urls = [regex]::Matches($text, 'https?://[^\s"''<>]+') | ForEach-Object {
                    $u = $null
                    if ([uri]::TryCreate($_.Value, [UriKind]::Absolute, [ref]$u)) { $u.Scheme + '://' + ($u.Authority -replace '^.*@', '') }
                } | Select-Object -Unique
                $description = if ($urls) { $urls -join ', ' } else { '已有配置（源地址未识别）' }
            }
            [pscustomobject]@{ tool = $key; path = $paths[$key]; description = $description; canRestore = (Test-Path -LiteralPath (Join-Path $stateDir ($key + '.json'))) }
        }
        @{ ok = $true; items = @($items) } | ConvertTo-Json -Depth 6 -Compress
        exit 0
    }
    if ($Action -eq 'Restore') {
        if (-not (Test-Path -LiteralPath $backup)) { throw '此工具没有由助手创建的备份。' }
        $snap = Get-Content -LiteralPath $backup -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($snap.existed) {
            $restored = [Convert]::FromBase64String($snap.bytes)
            if ($snap.protected) { $restored = [Security.Cryptography.ProtectedData]::Unprotect($restored, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser) }
            Write-AtomicBytes $snap.path $restored
        }
        elseif (Test-Path -LiteralPath $snap.path) { [IO.File]::Delete($snap.path) }
        [IO.File]::Delete($backup)
        @{ ok = $true; message = "$Tool 已恢复到本次切换前的配置。" } | ConvertTo-Json -Compress
        exit 0
    }
    $catalog = @{
        tencent = @{ pip='https://mirrors.cloud.tencent.com/pypi/simple/'; npm='https://mirrors.cloud.tencent.com/npm/'; go='https://mirrors.cloud.tencent.com/go/'; cargo='sparse+https://mirrors.cloud.tencent.com/crates.io-index/'; conda='https://mirrors.cloud.tencent.com/anaconda' }
        huawei = @{ pip='https://repo.huaweicloud.com/repository/pypi/simple/'; npm='https://repo.huaweicloud.com/repository/npm/'; go='https://repo.huaweicloud.com/repository/goproxy/'; cargo='sparse+https://repo.huaweicloud.com/repository/crates.io-index/'; conda='https://repo.huaweicloud.com/repository/anaconda' }
        aliyun = @{ pip='https://mirrors.aliyun.com/pypi/simple/'; npm='https://registry.npmmirror.com/'; go='https://mirrors.aliyun.com/goproxy/'; cargo='sparse+https://mirrors.aliyun.com/crates.io-index/'; conda='https://mirrors.aliyun.com/anaconda' }
        official = @{ pip='https://pypi.org/simple/'; npm='https://registry.npmjs.org/'; go='https://proxy.golang.org/'; cargo='sparse+https://index.crates.io/'; conda='https://repo.anaconda.com' }
    }
    $path = $paths[$Tool]; $value = $catalog[$Provider][$Tool]
    $path = [IO.Path]::GetFullPath($path)
    if (Test-Path -LiteralPath $backup) {
        $previous = Get-Content -LiteralPath $backup -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not [string]::Equals([IO.Path]::GetFullPath($previous.path), $path, [StringComparison]::OrdinalIgnoreCase)) { throw '配置路径与备份不同，请先恢复原配置，再切换新的配置路径。' }
    }
    if ($Tool -eq 'go' -and $env:GOENV -eq 'off') { throw 'GOENV=off，Go 禁用了用户配置文件。' }
    if ($Tool -eq 'cargo' -and -not $ConfigRoot -and (Test-Path -LiteralPath (Join-Path (Split-Path $path) 'config'))) { throw '发现旧版 Cargo config，请先整理配置，避免与 config.toml 冲突。' }
    # Verify the specific ecosystem endpoint, rather than assuming an Ubuntu speed test applies to it.
    $checkUrl = switch ($Tool) {
        'pip' { $value.TrimEnd('/') + '/pip/' }
        'cargo' { ($value -replace '^sparse\+', '') + 'config.json' }
        'conda' { $value + '/pkgs/main/win-64/repodata.json' }
        'go' { $value + 'golang.org/x/text/@v/list' }
        default { $value }
    }
    if (-not $ConfigRoot) {
        $request = [Net.HttpWebRequest]::Create($checkUrl); $request.Method = 'GET'
        $request.AddRange(0, 0); $request.Timeout = 12000; $request.ReadWriteTimeout = 12000
        $response = $null
        try {
            $response = $request.GetResponse()
            if ($response.ContentType -like '*text/html*' -and $Tool -in @('cargo','conda','go')) { throw '源地址返回了验证页。' }
        } catch {
            $failureCode = 'availability_unknown'
            $checkException = $_.Exception
            $errorResponse = $null
            while ($checkException) {
                if ($checkException.PSObject.Properties['Response'] -and $checkException.Response) { $errorResponse = $checkException.Response; break }
                $checkException = $checkException.InnerException
            }
            $providerName = @{tencent='腾讯云';huawei='华为云';aliyun='阿里云';official='官方'}[$Provider]
            if ($errorResponse) {
                $failureHttpStatus = [int]$errorResponse.StatusCode
                if ($failureHttpStatus -eq 429) {
                    $failureCode = 'rate_limited'
                    $retryHeader = [string]$errorResponse.Headers['Retry-After']; $retryNumber = 0L
                    if ([long]::TryParse($retryHeader, [ref]$retryNumber) -and $retryNumber -ge 0) { $failureRetryAfterSeconds = [Math]::Min($retryNumber, [int]::MaxValue) }
                    else {
                        $retryDate = [DateTimeOffset]::MinValue
                        if ([DateTimeOffset]::TryParse($retryHeader, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$retryDate)) {
                            $failureRetryAfterSeconds = [Math]::Min([int]::MaxValue, [Math]::Max(0, [Math]::Ceiling(($retryDate - [DateTimeOffset]::UtcNow).TotalSeconds)))
                        }
                    }
                    $waitMessage = if ($null -ne $failureRetryAfterSeconds) { "请 $failureRetryAfterSeconds 秒后再试" } else { '请稍后再试' }
                    $errorResponse.Close()
                    throw "$providerName $Tool 源请求被限流（HTTP 429），$waitMessage；原配置未修改。"
                }
                $errorResponse.Close()
                throw "$providerName $Tool 源检查返回 HTTP $failureHttpStatus，暂未切换；原配置未修改。"
            }
            throw "$providerName $Tool 源检查暂未完成，请稍后重试；原配置未修改。"
        }
        finally { if ($response) { $response.Close() }; $request.Abort() }
    }
    $existed = [IO.File]::Exists($path)
    [byte[]]$old = @()
    if ($existed) { $old = [IO.File]::ReadAllBytes($path) }
    $text = if ($existed) { [IO.File]::ReadAllText($path) } else { '' }
    if (-not (Test-Path -LiteralPath $backup)) {
        [byte[]]$encrypted = @()
        $isProtected = $existed -and $old.Length -gt 0
        if ($isProtected) { $encrypted = [Security.Cryptography.ProtectedData]::Protect($old, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser) }
        $snap = @{ path=$path; existed=$existed; protected=$isProtected; bytes=[Convert]::ToBase64String($encrypted) }
        Write-AtomicBytes $backup ($utf8.GetBytes(($snap | ConvertTo-Json -Compress)))
    }
    try {
        switch ($Tool) {
            'pip' { $text = Set-SectionKey $text 'global' 'index-url' $value; Write-AtomicBytes $path ($utf8.GetBytes($text)) }
            'npm' {
                $lines = @($text -split '\r?\n' | Where-Object { $_ -notmatch '^\s*registry\s*=' })
                $text = ($lines + "registry=$value") -join "`r`n"
                Write-AtomicBytes $path ($utf8.GetBytes($text))
            }
            'go' {
                $lines = @($text -split '\r?\n' | Where-Object { $_ -notmatch '^\s*GOPROXY=' })
                Write-AtomicBytes $path ($utf8.GetBytes((($lines + "GOPROXY=$value") -join "`n")))
            }
            'cargo' {
                if ($text -match '(?m)^\s*\[.*["''].*\]' -or $text -match '(?m)^\s*source\s*=') { throw 'Cargo 使用复杂的 source 写法，请手动配置，避免产生重复表。' }
                if ($Provider -eq 'official') {
                    # Remove only the replacement key in the crates.io section.
                    $text = [regex]::Replace($text, '(?ms)(^\s*\[source\.crates-io\]\s*\r?\n)(.*?)(?=^\s*\[|\z)', {
                        param($m) $m.Groups[1].Value + [regex]::Replace($m.Groups[2].Value, '(?m)^\s*replace-with\s*=.*\r?\n?', '')
                    })
                } else {
                    $text = Set-SectionKey $text 'source.crates-io' 'replace-with' '"campus-helper"'
                    $text = Set-SectionKey $text 'source.campus-helper' 'registry' ('"' + $value + '"')
                }
                Write-AtomicBytes $path ($utf8.GetBytes($text))
            }
            'conda' {
                $conda = Get-Command conda -ErrorAction Stop
                [IO.Directory]::CreateDirectory((Split-Path $path)) | Out-Null
                $null = & $conda.Source config --file $path --remove-key default_channels 2>&1
                Native $conda.Source @('config','--file',$path,'--add','default_channels',($value + '/pkgs/r'))
                Native $conda.Source @('config','--file',$path,'--add','default_channels',($value + '/pkgs/main'))
                $alias = if ($Provider -eq 'official') { 'https://conda.anaconda.org' } else { $value + '/cloud' }
                Native $conda.Source @('config','--file',$path,'--set','channel_alias',$alias)
            }
        }
    } catch {
        if ($existed) { Write-AtomicBytes $path $old } elseif (Test-Path -LiteralPath $path) { [IO.File]::Delete($path) }
        throw
    }
    @{ ok = $true; message = "$Tool 已切换。项目配置或环境变量可能覆盖用户配置；可用恢复按钮还原。" } | ConvertTo-Json -Compress
} catch { @{ ok = $false; message = $_.Exception.Message; code = $failureCode; httpStatus = $failureHttpStatus; retryAfterSeconds = $failureRetryAfterSeconds } | ConvertTo-Json -Compress; exit 1 }
finally { if ($taken) { $guard.ReleaseMutex() }; if ($guard) { $guard.Dispose() } }
