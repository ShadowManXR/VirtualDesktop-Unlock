$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.ServiceProcess
$tokens=$null;$parseErrors=$null
$tree=[System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'HelperInstaller.ps1'),[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw $parseErrors[0]}
foreach($name in @('Invoke-WithStreamerServicePaused','Initialize-InstallerProcessControl','Get-InstallerProcessPath','Stop-InstalledStreamer','Wait-IntegrationFilesUnlocked','Set-IntegrationFile','Restore-IntegrationFiles')){
    $function=$tree.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
    if(!$function){throw ('Installer function is missing: '+$name)}
    . ([scriptblock]::Create($function.Extent.Text))
}

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
Add-Type -Path (Join-Path $PSScriptRoot 'InstallerProcessControl.cs')
$ownProcess=Microsoft.PowerShell.Management\Get-Process -Id $PID
Check ((Get-InstallerProcessPath $ownProcess) -eq $ownProcess.Path) 'Native executable lookup resolves the current process without enumerating modules'

# Regression: an elevated Streamer can expose no Process.Path. The native lookup
# must still identify it, and a different installation must stay running.
$script:candidates=@([pscustomobject]@{Id=101;Path=$null;HasExited=$false},[pscustomobject]@{Id=202;Path='';HasExited=$false})
foreach($candidate in $script:candidates){$candidate | Add-Member ScriptMethod WaitForExit {param($timeout) $true}}
$script:stopped=New-Object 'System.Collections.Generic.List[int]'
function Get-Process {param($Name,$ErrorAction) @($script:candidates | Where-Object {$_.Id -notin $script:stopped})}
function Get-InstallerProcessPath($Process){if($Process.Id -eq 101){'C:\VD\VirtualDesktop.Streamer.exe'}else{'D:\OtherVD\VirtualDesktop.Streamer.exe'}}
function Stop-Process {param($InputObject,$ErrorAction) $script:stopped.Add($InputObject.Id)}
Stop-InstalledStreamer 'C:\VD\VirtualDesktop.Streamer.exe' | Out-Null
Check ($script:stopped.Count -eq 1 -and $script:stopped[0] -eq 101) 'Empty Process.Path does not skip the installed Streamer and other installations stay running'
function Get-InstallerProcessPath($Process){throw 'Access denied'}
$script:candidates=@([pscustomobject]@{Id=303;Path=$null;HasExited=$false})
$caught=$null
try{Stop-InstalledStreamer 'C:\VD\VirtualDesktop.Streamer.exe' | Out-Null}catch{$caught=$_.Exception.Message}
Check ($caught -like '*Cannot verify Streamer process 303*' -and $script:stopped.Count -eq 1) 'An unverifiable live process stops maintenance instead of being silently skipped'

$fixture=Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts\installer-file-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$target=Join-Path $fixture 'installed.dll';$source=Join-Path $fixture 'new.dll'
[IO.File]::WriteAllText($target,'old helper');[IO.File]::WriteAllText($source,'new helper')
$handle=[IO.File]::Open($target,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
$caught=$null
try{Wait-IntegrationFilesUnlocked @($target) -TimeoutMilliseconds 0 | Out-Null}catch{$caught=$_.Exception.Message}finally{$handle.Dispose()}
Check ($caught -like '*still in use*No integration files were changed*' -and [IO.File]::ReadAllText($target) -eq 'old helper') 'A locked helper is rejected before mutation with a lock-owner diagnostic'
Wait-IntegrationFilesUnlocked @($target) -TimeoutMilliseconds 0 | Out-Null
Check ([IO.File]::ReadAllText($target) -eq 'old helper') 'The unlocked-file preflight leaves existing bytes unchanged'
$changes=New-Object 'System.Collections.Generic.List[object]'
Set-IntegrationFile -Target $target -Source $source -Changes $changes
Check ([IO.File]::ReadAllText($target) -eq 'new helper' -and [IO.File]::ReadAllText($changes[0].Backup) -eq 'old helper') 'Atomic replacement retains the original helper in a rollback file'
$newTarget=Join-Path $fixture 'new-profile.xml'
Set-IntegrationFile -Target $newTarget -Contents ([Text.Encoding]::UTF8.GetBytes('profile')) -Changes $changes
Restore-IntegrationFiles $changes
Check ([IO.File]::ReadAllText($target) -eq 'old helper' -and !(Test-Path -LiteralPath $newTarget)) 'Rollback restores overwritten files and removes newly created files'
$changes=New-Object 'System.Collections.Generic.List[object]'
$caught=$null
try{Set-IntegrationFile -Target $target -Source (Join-Path $fixture 'missing-source') -Changes $changes}catch{$caught=$_.Exception.Message}
Restore-IntegrationFiles $changes
Check ($caught -and $changes.Count -eq 0 -and [IO.File]::ReadAllText($target) -eq 'old helper') 'A failure before replacement never rewrites unchanged files during rollback'
# Reproduce the actual CLR DLL lock, not just an ordinary open file handle.
$assemblySource=Join-Path $fixture 'LoadedHelper.cs'
[IO.File]::WriteAllText($assemblySource,'public sealed class LoadedHelperFixture { public static int Value = 1; }')
$loadedDll=Join-Path $fixture 'loaded-helper.dll'
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo /target:library ('/out:'+$loadedDll) $assemblySource
if($LASTEXITCODE -ne 0){throw 'DLL lock fixture did not compile.'}
$loaded=[Reflection.Assembly]::LoadFile($loadedDll)
$caught=$null
try{[IO.File]::Copy($source,$loadedDll,$true)}catch{$caught=$_.Exception.Message}
Check ($caught -and $loaded.GetType('LoadedHelperFixture')) 'The real loaded-DLL fixture reproduces the original Copy-Item sharing violation'
$caught=$null
try{Wait-IntegrationFilesUnlocked @($loadedDll) -TimeoutMilliseconds 0 | Out-Null}catch{$caught=$_.Exception.Message}
Check ($caught -like '*still in use*') 'The preflight also detects a CLR-mapped helper DLL'
$changes=New-Object 'System.Collections.Generic.List[object]'
Set-IntegrationFile -Target $loadedDll -Source $source -Changes $changes
Restore-IntegrationFiles $changes
Check (([Reflection.AssemblyName]::GetAssemblyName($loadedDll)).Name -eq $loaded.GetName().Name) 'Atomic replacement and rollback preserve the original image even while another CLR holds it'
Write-Output 'Helper installer service/file checks passed: 17'
