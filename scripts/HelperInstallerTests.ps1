$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.ServiceProcess
$tokens=$null;$parseErrors=$null
$tree=[System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'HelperInstaller.ps1'),[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw $parseErrors[0]}
$function=$tree.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-WithStreamerServicePaused'},$true)
if(!$function){throw 'Service maintenance function is missing.'}
. ([scriptblock]::Create($function.Extent.Text))

function Get-Service { param($Name,$ErrorAction) if($Name -ne 'VirtualDesktop.Service.exe'){throw 'Unexpected service'}; $script:service }
function Stop-Service { param($InputObject,$ErrorAction) $script:events.Add('stop'); if($script:failStop){throw 'stop failed'}; $InputObject.Status=[System.ServiceProcess.ServiceControllerStatus]::Stopped }
function Start-Service { param($InputObject,$ErrorAction) $script:events.Add('start'); $InputObject.Status=[System.ServiceProcess.ServiceControllerStatus]::Running }
function Reset-Case($state){
    $script:events=New-Object 'System.Collections.Generic.List[string]'
    $script:failStop=$false
    $script:service=if($state){[pscustomobject]@{Status=[System.ServiceProcess.ServiceControllerStatus]$state}}else{$null}
    if($script:service){$script:service | Add-Member ScriptMethod WaitForStatus { param($desired,$timeout) if($this.Status -ne $desired){throw 'Service did not reach requested status'} }}
}
function Check($condition,$message){if(!$condition){throw $message};Write-Output ('PASS '+$message)}

Reset-Case 'Running'
Invoke-WithStreamerServicePaused { $script:events.Add('files') } | Out-Null
Check (($script:events -join ',') -eq 'stop,files,start') 'Running service is paused before file changes and restored afterward'
Reset-Case 'Running'
$caught=$null
try{Invoke-WithStreamerServicePaused { $script:events.Add('rollback');throw 'copy failed' } | Out-Null}catch{$caught=$_.Exception.Message}
Check ($caught -eq 'copy failed' -and ($script:events -join ',') -eq 'stop,rollback,start') 'Installer failure propagates after the service is restored'
Reset-Case 'Stopped'
Invoke-WithStreamerServicePaused { $script:events.Add('files') } | Out-Null
Check (($script:events -join ',') -eq 'files' -and $script:service.Status -eq 'Stopped') 'An initially stopped service stays stopped'
Reset-Case $null
Invoke-WithStreamerServicePaused { $script:events.Add('files') } | Out-Null
Check (($script:events -join ',') -eq 'files') 'Missing optional service permits helper maintenance'
Reset-Case 'Running'
Invoke-WithStreamerServicePaused { $script:events.Add('remove'); return } | Out-Null
Check (($script:events -join ',') -eq 'stop,remove,start') 'Uninstall return still restores the service'
Reset-Case 'Running'
$script:failStop=$true;$caught=$null
try{Invoke-WithStreamerServicePaused { $script:events.Add('files') } | Out-Null}catch{$caught=$_.Exception.Message}
Check ($caught -eq 'stop failed' -and ($script:events -join ',') -eq 'stop,start') 'Failure to stop the service prevents file changes'
Write-Output 'Helper installer service checks passed: 6'
