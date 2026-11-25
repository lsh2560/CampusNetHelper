param(
    [Parameter(Mandatory)][string]$WinAppPath,
    [Parameter(Mandatory)][string]$BuildDirectory,
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$FixtureServer,
    [ValidateSet('mouse','uia')][string]$InteractionMode='uia'
)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$ArtifactDirectory=[IO.Path]::GetFullPath($ArtifactDirectory)
[IO.Directory]::CreateDirectory($ArtifactDirectory)|Out-Null
$oldWorkflow=$env:WINAPP_UI_WORKFLOW_ID; $oldTelemetry=$env:WINAPP_CLI_TELEMETRY_OPTOUT
$env:WINAPP_UI_WORKFLOW_ID=[guid]::NewGuid().ToString('N'); $env:WINAPP_CLI_TELEMETRY_OPTOUT='1'
$workflow=$env:WINAPP_UI_WORKFLOW_ID
$results=New-Object 'Collections.Generic.List[object]'
$screenshots=New-Object 'Collections.Generic.List[string]'
$testProcess=$null
$hwnd=$null
function WinApp([string[]]$Arguments) {
    $global:LASTEXITCODE=$null; $output=@(& $WinAppPath @Arguments 2>&1)
    if($null -eq $global:LASTEXITCODE -or $global:LASTEXITCODE -ne 0){throw "winapp failed: $($Arguments -join ' '): $($output -join ' ')"}
    $output
}
function Ui([string[]]$Arguments) {
    $selector=if($hwnd){@('-w',"$hwnd")}else{@('-a',"$appPid")}
    WinApp (@('ui')+$Arguments+@('--on','local')+$selector)
}
function Test-UI([string]$Name,[scriptblock]$Script) {
    try{& $Script | Out-Null; $results.Add(@{name=$Name;status='PASS'})}
    catch{$results.Add(@{name=$Name;status='FAIL';detail="$_"})}
}
function State {
    for($i=0;$i -lt 30;$i++){
        try{$s=Get-Content -LiteralPath (Join-Path $ArtifactDirectory 'state.json') -Raw|ConvertFrom-Json; if($s.page){return $s}}catch{}
        Start-Sleep -Milliseconds 100
    }
    throw 'Isolated UI state was not delivered'
}
function Until([scriptblock]$Condition,[string]$Message,[int]$Milliseconds=12000) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    do{$state=State; if(& $Condition $state){return $state}; Start-Sleep -Milliseconds 100}while($clock.ElapsedMilliseconds -lt $Milliseconds)
    throw "$Message. Last state: $($state|ConvertTo-Json -Depth 4 -Compress)"
}
function Key([string]$Key) {Ui @('send-keys',$Key,'--via','post-message')|Out-Null}
function Activate-Button([string]$Name) {
    if($InteractionMode -eq 'mouse'){Ui @('click',$Name)}else{Ui @('invoke',$Name,'--action','invoke')}
}
function Screenshot([string]$Name) {
    $path=Join-Path $ArtifactDirectory $Name
    if(Test-Path -LiteralPath $path){throw 'Use a fresh evidence directory'}
    WinApp @('ui','screenshot','--on','local','-w',"$hwnd",'--output',$path)|Out-Null
    if(-not(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -eq 0){throw 'Screenshot not delivered'}
    $screenshots.Add($path)
}
try {
    $exe=Join-Path ([IO.Path]::GetFullPath($BuildDirectory)) 'DiagnosisUiHost.exe'
    $testProcess=Start-Process -FilePath $exe -ArgumentList @(('"'+$ArtifactDirectory+'"'),$FixtureServer,'--ui-host') -WindowStyle Normal -PassThru
    $appPid=$testProcess.Id; State|Out-Null
    $windows=@(Ui @('list-windows','--json')|ConvertFrom-Json)
    $main=@($windows|Where-Object {$_.processId -eq $appPid -and $_.title -eq '校园网助手 账号与测速 UI 测试（独立数据）'})
    if($main.Count -ne 1){throw 'Expected one isolated test window'}
    $hwnd=$main[0].hwnd
    Test-UI 'Intended window exposes a real UI Automation tree' {
        $tree=Ui @('inspect','--json','--depth','2')|ConvertFrom-Json
        if(-not $tree.windows[0].elements.Count){throw 'No real UI elements were inspected'}
    }
    Test-UI 'Account checkbox is visible and initially off' {
        Ui @('wait-for','AccountAutoStart','--value','Off','-t','3000')
        Screenshot '01-account-unchecked.png'
    }
    Test-UI 'UIA toggle changes complete checkbox on and off' {
        Ui @('invoke','AccountAutoStart','--action','toggle'); Ui @('wait-for','AccountAutoStart','--value','On','-t','3000')
        Screenshot '02-account-checked.png'
        Ui @('invoke','AccountAutoStart','--action','toggle'); Ui @('wait-for','AccountAutoStart','--value','Off','-t','3000')
    }
    Test-UI 'Checkbox remains visible in minimum-width window' {
        Key 'f12'; Ui @('wait-for','AccountAutoStart','--value','Off','-t','3000')
        Screenshot '03-account-narrow.png'; Key 'f5'
    }
    Test-UI 'Diagnosis tests download and acknowledged upload' {
        Key 'f11'; Ui @('wait-for','BandwidthStart','-t','3000'); Activate-Button 'BandwidthStart'
        $s=Until {param($s) -not $s.running -and $s.labels.BandwidthStatus -like '测速完成*'} 'Both directions did not finish'
        foreach($id in @('BandwidthDownloadValue','BandwidthUploadValue')){Ui @('wait-for',$id,'--value','Mbps','--contains','-t','3000')}
        foreach($id in @('BandwidthDownloadDetail','BandwidthUploadDetail')){if($s.labels.$id -notlike '*MB/s*'){throw 'MB/s result is missing'}}
        Screenshot '04-diagnosis-completed.png'
    }
    Test-UI 'Completed result has a clear state and a copyable report' {
        Ui @('wait-for','BandwidthState','--value','已完成','-t','3000')
        Activate-Button 'BandwidthCopy'
        $s=Until {param($s) $s.copiedReport -like '*下载：*Mbps*MB/s*上传：*Mbps*MB/s*'} 'Formatted result was not copied'
        if($s.copiedReport -notlike '*服务器：127.0.0.1*' -or $s.copiedReport -notlike '*时间：*'){throw 'Server or timestamp is missing from the report'}
    }
    Test-UI 'Completed results survive navigation' {
        $before=(State).labels
        if($before.BandwidthDownloadValue -notmatch '^\d' -or $before.BandwidthUploadValue -notmatch '^\d'){throw 'No successful results exist to verify persistence'}
        Key 'f8'; Key 'f7'; $after=(State).labels
        if($before.BandwidthDownloadValue -ne $after.BandwidthDownloadValue -or $before.BandwidthUploadValue -ne $after.BandwidthUploadValue){throw 'Navigation lost measured values'}
    }
    Test-UI 'Server failures show an error rather than zero speed' {
        Key 'f10'; Ui @('wait-for','BandwidthStart','-t','3000'); Activate-Button 'BandwidthStart'
        $s=Until {param($s) -not $s.running -and $s.labels.BandwidthStatus -like '测速结束*'} 'Failure did not reach the UI'
        if($s.labels.BandwidthDownloadValue -ne '未测得' -or $s.labels.BandwidthUploadValue -ne '未测得'){throw 'Failed transfer was presented as measured throughput'}
        Ui @('wait-for','BandwidthState','--value','失败','-t','3000')
        if($s.speedColors.Count -ne 2 -or @($s.speedColors|Where-Object {(($_ -shr 16) -band 255) -le (((($_ -shr 8) -band 255))*2)}).Count -gt 0){throw 'Failed values are not shown in the error color'}
        Screenshot '05-diagnosis-error.png'
    }
    Test-UI 'Independent direction failure is marked as partial completion' {
        Key 'f2'; Ui @('wait-for','BandwidthStart','-t','3000'); Activate-Button 'BandwidthStart'
        $s=Until {param($s) -not $s.running -and $s.labels.BandwidthState -eq '部分完成'} 'Partial result was not identified'
        if($s.labels.BandwidthDownloadValue -notmatch '^\d' -or $s.labels.BandwidthUploadValue -ne '未测得'){throw 'Partial result lost the successful direction'}
        Screenshot '07-diagnosis-partial.png'
    }
    Test-UI 'Stop aborts active measurement and re-enables start' {
        Key 'f9'; Ui @('wait-for','BandwidthStart','-t','3000'); Activate-Button 'BandwidthStart'
        Until {param($s) $s.running} 'Test did not start'|Out-Null
        Key 'f8'; Key 'f7'
        Until {param($s) $s.running} 'Navigation interrupted active measurement'|Out-Null
        Ui @('wait-for','BandwidthStart','-p','IsEnabled','--value','False','-t','3000')
        Activate-Button 'BandwidthStop'
        Until {param($s) -not $s.running -and $s.labels.BandwidthStatus -like '测速已停止*'} 'Stop did not finish'|Out-Null
        Ui @('wait-for','BandwidthStart','-p','IsEnabled','--value','True','-t','3000')
        Ui @('wait-for','BandwidthState','--value','已停止','-t','3000')
        Screenshot '06-diagnosis-stopped.png'
    }
    Test-UI 'Disabled stop action cannot overwrite completed feedback' {
        Ui @('wait-for','BandwidthStop','-p','IsEnabled','--value','False','-t','3000')
        $before=(State).labels.BandwidthStatus
        $rejected=$false
        try { Activate-Button 'BandwidthStop' } catch {
            if("$_" -notmatch 'Arg_COMException|ElementNotEnabled|not enabled|disabled'){throw}
            $rejected=$true
        }
        # A mouse click can be delivered over a disabled control without firing it.
        # UIA Invoke must reject; both modes must leave the actual result unchanged.
        if($InteractionMode -eq 'uia' -and -not $rejected){throw 'The disabled element unexpectedly accepted UIA Invoke'}
        Start-Sleep -Milliseconds 250
        Ui @('wait-for','BandwidthStop','-p','IsEnabled','--value','False','-t','3000')
        if((State).labels.BandwidthStatus -ne $before){throw 'Disabled stop action changed the completed result'}
    }
    Test-UI 'UI interactions leave isolated account/settings unchanged' {
        if(-not(State).settingsUnchanged){throw 'Unsaved checkbox or speed test modified stored settings'}
        if(Test-Path -LiteralPath (Join-Path $ArtifactDirectory 'isolated-data\Secret.json')){throw 'Test unexpectedly created credentials'}
    }
}catch{$results.Add(@{name='Target setup / execution';status='FAIL';detail="$_"})}
finally {
    try{if($testProcess){WinApp @('ui','yield','--on','local')|Out-Null}}catch{$results.Add(@{name='Release UI workflow';status='FAIL';detail="$_"})}
    if($testProcess -and -not $testProcess.HasExited){$testProcess.Kill()}
    $env:WINAPP_UI_WORKFLOW_ID=$oldWorkflow; $env:WINAPP_CLI_TELEMETRY_OPTOUT=$oldTelemetry
}
$failed=@($results|Where-Object {$_.status -eq 'FAIL'}).Count
[ordered]@{target='local';interactionMode=$InteractionMode;workflowId=$workflow;passed=$results.Count-$failed;failed=$failed;results=$results.ToArray();screenshots=$screenshots.ToArray();visualReview='Pending';notes='Synthetic loopback fixture, not campus throughput. Test-only function keys navigate real Dashboard pages. Window-scoped UIA actions must produce observed state changes. Mouse mode requires foreground readiness and is reported separately. Copy uses an injected writer and leaves the system clipboard untouched.'}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'test-results.json') -Encoding utf8
$results|Format-Table -AutoSize
if($failed){exit 1}
