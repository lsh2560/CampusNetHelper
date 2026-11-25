param(
    [ValidateSet('local')][string]$Target='local',
    [Parameter(Mandatory)][string]$WinAppPath,
    [Parameter(Mandatory)][string]$BuildDirectory,
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [int]$AppPid=0,
    [ValidateSet('local')][string]$AppScope='local',
    [string]$WorkflowId=[guid]::NewGuid().ToString('N')
)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$ArtifactDirectory=[IO.Path]::GetFullPath($ArtifactDirectory)
[IO.Directory]::CreateDirectory($ArtifactDirectory) | Out-Null
$oldWorkflow=$env:WINAPP_UI_WORKFLOW_ID
$oldTelemetry=$env:WINAPP_CLI_TELEMETRY_OPTOUT
$env:WINAPP_UI_WORKFLOW_ID=$WorkflowId
$env:WINAPP_CLI_TELEMETRY_OPTOUT='1'
$results=New-Object 'Collections.Generic.List[object]'
$screenshots=New-Object 'Collections.Generic.List[string]'
$uiStarted=$false
$ownedProcess=$null
function Invoke-WinAppChecked([string[]]$Arguments) {
    $global:LASTEXITCODE=$null
    $output=@(& $WinAppPath @Arguments 2>&1)
    if($null -eq $global:LASTEXITCODE -or $global:LASTEXITCODE -ne 0){throw "winapp failed: $($Arguments -join ' '): $($output -join ' ')"}
    $output
}
function Invoke-Ui([string[]]$Arguments) { Invoke-WinAppChecked (@('ui')+$Arguments+@('--on',$Target,'-a',"$AppPid")) }
function Test-UI([string]$Name,[scriptblock]$Script) {
    try{ & $Script | Out-Null; $results.Add(@{name=$Name;status='PASS'}) }
    catch{ $results.Add(@{name=$Name;status='FAIL';detail="$_"}) }
}
function Read-State {
    for($i=0;$i -lt 20;$i++) {
        try{ $s=Get-Content -LiteralPath (Join-Path $ArtifactDirectory 'window-state.json') -Raw | ConvertFrom-Json; if($s.bounds){return $s} }catch{}
        Start-Sleep -Milliseconds 100
    }
    throw 'Test host did not report window state.'
}
function Wait-State([string]$State) {
    for($i=0;$i -lt 30;$i++) { $s=Read-State; if($s.state -eq $State){return $s}; Start-Sleep -Milliseconds 100 }
    throw "Expected $State, got $($s.state)"
}
function Save-Screenshot([string]$Name) {
    $path=Join-Path $ArtifactDirectory $Name
    if(Test-Path -LiteralPath $path){throw "Evidence already exists: $path"}
    Invoke-WinAppChecked @('ui','screenshot','--on',$Target,'-w',"$hwnd",'--output',$path) | Out-Null
    if(-not(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -eq 0){throw 'Screenshot missing'}
    $screenshots.Add($path)
}
function Drag-Border([string]$Edge,[int]$Delta) {
    $s=Wait-State 'Normal'; $b=$s.bounds
    $x=[int]($b.x+$b.width/2); $y=[int]($b.y+$b.height/2); $dx=0; $dy=0
    if($Edge.Contains('left')){$x=$b.x+2; $dx=-$Delta}
    if($Edge.Contains('right')){$x=$b.x+$b.width-3; $dx=$Delta}
    if($Edge.Contains('top')){$y=$b.y+2; $dy=-$Delta}
    if($Edge.Contains('bottom')){$y=$b.y+$b.height-3; $dy=$Delta}
    Invoke-Ui @('drag',"$x,$y",("{0},{1}" -f ($x+$dx),($y+$dy))) | Out-Null
    Start-Sleep -Milliseconds 250
    $a=(Read-State).bounds
    $widthDelta=if($dx -ne 0){$Delta}else{0}
    $heightDelta=if($dy -ne 0){$Delta}else{0}
    if([Math]::Abs($a.width-$b.width-$widthDelta) -gt 2 -or [Math]::Abs($a.height-$b.height-$heightDelta) -gt 2){
        throw "Drag $Edge did not resize: $($b|ConvertTo-Json -Compress) -> $($a|ConvertTo-Json -Compress)"
    }
}
try {
    if(-not(Test-Path -LiteralPath $WinAppPath)){throw 'Run Setup-UiTestTools.ps1 first.'}
    if($AppPid -lt 0){throw 'AppPid must be zero or positive'}
    if($AppPid -eq 0){
        $exe=Join-Path ([IO.Path]::GetFullPath($BuildDirectory)) 'DesktopInteractionUiHost.exe'
        $ownedProcess=Start-Process -FilePath $exe -ArgumentList @(('"'+$ArtifactDirectory+'"'),'--ui-host') -WindowStyle Normal -PassThru
        $AppPid=$ownedProcess.Id
    }elseif($AppScope -ne $Target){throw 'Captured PID belongs to a different target'}
    Read-State | Out-Null
    $uiStarted=$true
    $windows=@(Invoke-Ui @('list-windows','--json') | ConvertFrom-Json)
    $main=@($windows | Where-Object {$_.processId -eq $AppPid -and $_.title -eq '校园网助手 UI 自动测试（示例数据）'})
    if($main.Count -ne 1){throw 'Expected exactly one isolated test window'}
    $hwnd=[string]$main[0].hwnd
    Test-UI 'Real UI tree and stable selectors' {
        $tree=Invoke-Ui @('inspect','--json','--depth','7') | ConvertFrom-Json
        $tree | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'ui-tree.json') -Encoding utf8
        function Visit($nodes) {foreach($n in $nodes){$n; Visit $n.children}}
        $all=@(Visit $tree.windows[0].elements)
        foreach($id in @('HeroDownloadValue','HeroUploadValue','RealtimeDownloadValue','RealtimeUploadValue','WindowTitleBar','WindowMaximize','WindowMinimize')){
            if(-not($all | Where-Object {$_.automationId -eq $id})){throw "Missing real UI element: $id"}
        }
    }
    Test-UI 'Upload and download values are complete in UIA' {
        Invoke-Ui @('wait-for','HeroUploadValue','--value','↑  17.0 KB/s','-t','3000')
        Invoke-Ui @('wait-for','HeroDownloadValue','--value','↓  240.1 KB/s','-t','3000')
        Save-Screenshot '01-normal.png'
    }
    Test-UI 'Double-click blank title bar maximizes' {
        Wait-State 'Normal' | Out-Null
        Invoke-Ui @('click','WindowTitleBar','--double')
        $s=Wait-State 'Maximized'; if($s.padding -ne 0){throw 'Maximized window has a resize gutter'}
        Save-Screenshot '02-maximized.png'
    }
    Test-UI 'Double-click blank title bar restores' {
        Wait-State 'Maximized' | Out-Null
        Invoke-Ui @('click','WindowTitleBar','--double')
        $s=Wait-State 'Normal'; if($s.padding -ne 8){throw 'Restored resize gutter missing'}
    }
    foreach($edge in @('left','right','top','bottom','top-left','top-right','bottom-left','bottom-right')) {
        Test-UI "Mouse drag $edge expands and shrinks" {Drag-Border $edge 24; Drag-Border $edge -24}
    }
    Test-UI 'Blank title bar still drags the window' {
        $before=(Read-State).bounds
        $bar=(Read-State).titlebar
        $x=[int]($bar.x+$bar.width/2); $y=[int]($bar.y+$bar.height/2)
        Invoke-Ui @('drag','WindowTitleBar',("{0},{1}" -f ($x+30),($y+20)))
        Start-Sleep -Milliseconds 300
        $after=(Read-State).bounds
        if($after.x -le $before.x -or $after.y -le $before.y -or $after.width -ne $before.width){throw 'Title bar did not move the window'}
        Invoke-Ui @('drag','WindowTitleBar',"$x,$y")
    }
    Test-UI 'Caption maximize and restore still work with real mouse capture' {
        Invoke-Ui @('click','WindowMaximize'); Wait-State 'Maximized' | Out-Null
        Invoke-Ui @('click','WindowMaximize'); Wait-State 'Normal' | Out-Null
        Save-Screenshot '03-restored-after-resize.png'
    }
    Test-UI 'Caption minimize still works with real mouse capture' {
        Invoke-Ui @('click','WindowMinimize'); Wait-State 'Minimized' | Out-Null
    }
}catch{ $results.Add(@{name='Target setup / test execution';status='FAIL';detail="$_"}) }
finally {
    try{if($uiStarted){Invoke-WinAppChecked @('ui','yield','--on',$Target) | Out-Null}}
    catch{$results.Add(@{name='Release UI workflow';status='FAIL';detail="$_"})}
    if($ownedProcess -and -not $ownedProcess.HasExited){$ownedProcess.Kill()}
    $env:WINAPP_UI_WORKFLOW_ID=$oldWorkflow; $env:WINAPP_CLI_TELEMETRY_OPTOUT=$oldTelemetry
}
$failed=@($results | Where-Object {$_.status -eq 'FAIL'}).Count
$report=[ordered]@{target=$Target;appPid=$AppPid;workflowId=$WorkflowId;passed=$results.Count-$failed;failed=$failed;results=$results.ToArray();screenshots=$screenshots.ToArray();visualReview='Pending: view every screenshot before declaring completion'}
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'test-results.json') -Encoding utf8
$results | Format-Table -AutoSize
if($failed){exit 1}
