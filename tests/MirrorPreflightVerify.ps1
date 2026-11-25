param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($taskRoot)|Out-Null
$taskSource=Join-Path (Split-Path -Parent $PSScriptRoot) 'MirrorConfig.ps1'
$taskText=[IO.File]::ReadAllText($taskSource)
if(-not $taskText.Contains('if (-not $ConfigRoot) {') -or -not $taskText.Contains('[Net.HttpWebRequest]::Create($checkUrl)')){throw 'Preflight injection anchors changed'}
# Run the production preflight/error path, redirecting only the copied test
# script's request to a local fixture. ConfigRoot isolates every config write.
$taskText=$taskText.Replace('if (-not $ConfigRoot) {','if ($true) {').Replace('[Net.HttpWebRequest]::Create($checkUrl)','[Net.HttpWebRequest]::Create($env:CAMPUS_MIRROR_TEST_URL)')
$taskScript=Join-Path $taskRoot 'MirrorConfig-isolated.ps1'
[IO.File]::WriteAllText($taskScript,$taskText,(New-Object Text.UTF8Encoding($true)))
$taskPortFile=Join-Path $taskRoot 'port.json'
$taskServer=Start-Process -FilePath 'C:/Program Files/nodejs/node.exe' -ArgumentList @((Join-Path $PSScriptRoot 'mirror-status-server.cjs'),$taskPortFile) -WindowStyle Hidden -PassThru
$taskResults=New-Object 'Collections.Generic.List[object]'
function Assert-Mirror([bool]$Condition,[string]$Name){
 $taskResults.Add(@{name=$Name;status=$(if($Condition){'PASS'}else{'FAIL'})})
 if(-not $Condition){throw $Name}
}
try{
 for($taskAttempt=0;$taskAttempt -lt 60 -and -not(Test-Path -LiteralPath $taskPortFile);$taskAttempt++){Start-Sleep -Milliseconds 100}
 $taskPort=(Get-Content -LiteralPath $taskPortFile -Raw|ConvertFrom-Json).port
 foreach($taskCase in @('429-seconds','429-date','429-invalid','429-none','503','ok')){
  $taskCaseRoot=Join-Path $taskRoot $taskCase
  $taskConfig=Join-Path $taskCaseRoot 'configs';$taskData=Join-Path $taskCaseRoot 'data'
  [IO.Directory]::CreateDirectory($taskConfig)|Out-Null
  $taskConfigFile=Join-Path $taskConfig 'pip.config'
  [IO.File]::WriteAllText($taskConfigFile,"[global]`r`ntimeout=47`r`nindex-url=https://test.invalid/simple/`r`n",(New-Object Text.UTF8Encoding($false)))
  $taskOriginal=(Get-FileHash -LiteralPath $taskConfigFile -Algorithm SHA256).Hash
  $env:CAMPUS_MIRROR_TEST_URL='http://127.0.0.1:'+$taskPort+'/'+$taskCase
  $taskOutput=Join-Path $taskCaseRoot 'result.json'
  $taskError=Join-Path $taskCaseRoot 'error.txt'
  $taskRun=Start-Process -FilePath "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$taskScript,'-Action','Apply','-Tool','pip','-Provider','huawei','-ConfigRoot',$taskConfig,'-DataRoot',$taskData) -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput $taskOutput -RedirectStandardError $taskError
  $taskParsed=Get-Content -LiteralPath $taskOutput -Raw -Encoding utf8|ConvertFrom-Json
  if($taskCase -like '429*'){
   Assert-Mirror ($taskRun.ExitCode -eq 1 -and -not $taskParsed.ok -and $taskParsed.code -eq 'rate_limited' -and $taskParsed.httpStatus -eq 429) "429 classified as rate limiting: $taskCase"
   Assert-Mirror ($taskParsed.message.Contains('限流') -and $taskParsed.message.Contains('原配置未修改') -and -not $taskParsed.message.Contains('当前不可用') -and -not $taskParsed.message.Contains('GetResponse')) "Actionable localized message: $taskCase"
   if($taskCase -eq '429-seconds'){Assert-Mirror ($taskParsed.retryAfterSeconds -eq 120) 'Numeric Retry-After preserved'}
   elseif($taskCase -eq '429-date'){Assert-Mirror ($taskParsed.retryAfterSeconds -ge 80 -and $taskParsed.retryAfterSeconds -le 90) 'HTTP-date Retry-After converted to remaining seconds'}
   else{Assert-Mirror ($null -eq $taskParsed.retryAfterSeconds) "Missing or invalid Retry-After stays unknown: $taskCase"}
  }elseif($taskCase -eq '503'){
   Assert-Mirror ($taskRun.ExitCode -eq 1 -and $taskParsed.code -ne 'rate_limited' -and $taskParsed.httpStatus -eq 503) '503 remains distinct from rate limiting'
   Assert-Mirror ($taskParsed.message.Contains('HTTP 503') -and -not $taskParsed.message.Contains('GetResponse')) '503 status is readable without exception scaffolding'
  }else{
   Assert-Mirror ($taskRun.ExitCode -eq 0 -and $taskParsed.ok) 'Successful endpoint still allows explicit apply'
   Assert-Mirror ([IO.File]::ReadAllText($taskConfigFile).Contains('repo.huaweicloud.com/repository/pypi/simple/')) 'Configured pip URL remains the official provider URL'
  }
  if($taskCase -ne 'ok'){
   Assert-Mirror ((Get-FileHash -LiteralPath $taskConfigFile -Algorithm SHA256).Hash -eq $taskOriginal) "Failed preflight leaves config byte-for-byte unchanged: $taskCase"
   Assert-Mirror (-not(Test-Path -LiteralPath (Join-Path $taskData 'mirror-backups/pip.json'))) "Failed preflight creates no recovery snapshot: $taskCase"
  }
 }
 $taskCounts=Invoke-RestMethod -Uri ('http://127.0.0.1:'+$taskPort+'/stats')
 Assert-Mirror (@($taskCounts.PSObject.Properties|Where-Object {$_.Value -ne 1}).Count -eq 0) 'Every explicit attempt makes one request; no automatic retry storm'
 $taskExpected=(Get-FileHash -LiteralPath (Join-Path $taskRoot 'ok/configs/pip.config') -Algorithm SHA256).Hash
 $taskRestore= & $taskScript -Action Restore -Tool pip -ConfigRoot (Join-Path $taskRoot 'ok/configs') -DataRoot (Join-Path $taskRoot 'ok/data')
 Assert-Mirror (($taskRestore|ConvertFrom-Json).ok -and (Get-FileHash -LiteralPath (Join-Path $taskRoot 'ok/configs/pip.config') -Algorithm SHA256).Hash -ne $taskExpected) 'Successful explicit apply remains restorable'
}catch{
 $taskResults.Add(@{name='Exception';status='FAIL';detail=$_.Exception.Message})
}finally{
 if(-not $taskServer.HasExited){Stop-Process -Id $taskServer.Id}
 Remove-Item Env:CAMPUS_MIRROR_TEST_URL -ErrorAction SilentlyContinue
 $taskFailed=@($taskResults|Where-Object {$_.status -eq 'FAIL'}).Count
 @{passed=$taskResults.Count-$taskFailed;failed=$taskFailed;results=@($taskResults.ToArray())}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $taskRoot 'mirror-preflight-results.json') -Encoding utf8
 Write-Output "Mirror preflight: $($taskResults.Count-$taskFailed) passed, $taskFailed failed"
}
if($taskFailed){exit 1}
