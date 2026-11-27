[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [switch]$Uninstall,
    [string]$StreamerDirectory=(Join-Path $env:ProgramFiles 'Virtual Desktop Streamer')
)
$ErrorActionPreference='Stop'

function Invoke-WithStreamerServicePaused([scriptblock]$Action){
    # The service can relaunch Streamer between Stop-Process and Copy-Item.
    # Preserve its initial state, including when installation or rollback fails.
    $service=Get-Service -Name 'VirtualDesktop.Service.exe' -ErrorAction SilentlyContinue
    $resumeService=$false
    try{
        if($service -and $service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped){
            $resumeService=$true
            Write-Output 'Pausing the Virtual Desktop service during helper maintenance.'
            Stop-Service -InputObject $service -ErrorAction Stop
            $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(30))
        }
        & $Action
    }finally{
        if($resumeService){
            Start-Service -InputObject $service -ErrorAction Stop
            $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(30))
            Write-Output 'Restored the Virtual Desktop service.'
        }
    }
}

function Initialize-InstallerProcessControl {
    if(-not ('VDUnlockInstaller.ProcessControl' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot 'InstallerProcessControl.cs')}
}

function Get-InstallerProcessPath($Process){
    # Process.Path uses MainModule and may be empty for an elevated Streamer.
    # QueryFullProcessImageName needs only PROCESS_QUERY_LIMITED_INFORMATION.
    [VDUnlockInstaller.ProcessControl]::ExecutablePath($Process.Id)
}

function Stop-InstalledStreamer([string]$Executable){
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    do{
        $matches=@()
        foreach($candidate in @(Get-Process -Name VirtualDesktop.Streamer -ErrorAction SilentlyContinue)){
            try{$image=Get-InstallerProcessPath $candidate}catch{
                if($candidate.HasExited){continue}
                throw ('Cannot verify Streamer process '+$candidate.Id+'. No integration files were changed. '+$_.Exception.Message)
            }
            if(!$image){continue} # Process exited during enumeration.
            Write-Output ('Streamer process '+$candidate.Id+': '+$image)
            if([IO.Path]::GetFullPath($image) -eq $Executable){$matches+=@($candidate)}
        }
        if(!$matches.Count){return}
        foreach($candidate in $matches){
            Write-Output ('Stopping installed Streamer PID '+$candidate.Id+'.')
            Stop-Process -InputObject $candidate -ErrorAction Stop
            $script:installerStoppedStreamer=$true
            if(!$candidate.WaitForExit(15000)){throw ('Streamer PID '+$candidate.Id+' did not exit. No integration files were changed.')}
        }
        if([DateTime]::UtcNow -ge $deadline){throw 'Streamer keeps restarting during maintenance. No integration files were changed.'}
    }while($true)
}

function Wait-IntegrationFilesUnlocked([string[]]$Paths,[int]$TimeoutMilliseconds=10000){
    $deadline=[DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do{
        $locked=@()
        foreach($path in $Paths){
            if(!(Test-Path -LiteralPath $path)){continue}
            try{$handle=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$handle.Dispose()}
            catch [IO.IOException]{$locked+=@($path)}
        }
        if(!$locked.Count){Write-Output 'Integration files are unlocked.';return}
        if([DateTime]::UtcNow -ge $deadline){
            $owners=[VDUnlockInstaller.ProcessControl]::DescribeLocks($locked)
            throw ('Integration files are still in use: '+($locked -join ', ')+'. Lock owner: '+$owners+' No integration files were changed. Close the listed program and retry.')
        }
        Start-Sleep -Milliseconds 200
    }while($true)
}

function Set-IntegrationFile([string]$Target,[string]$Source,[byte[]]$Contents,$Changes){
    # Replace by rename rather than writing over a loaded DLL. Keep the original
    # on disk until the complete update has succeeded, for atomic rollback.
    $temporary=$Target+'.'+[guid]::NewGuid().ToString('N')+'.pending'
    $backup=$Target+'.'+[guid]::NewGuid().ToString('N')+'.rollback'
    try{
        if($Source){[IO.File]::Copy($Source,$temporary)}else{[IO.File]::WriteAllBytes($temporary,$Contents)}
        $existed=Test-Path -LiteralPath $Target
        if($existed){[IO.File]::Replace($temporary,$Target,$backup)}else{[IO.File]::Move($temporary,$Target)}
        $Changes.Add([pscustomobject]@{Target=$Target;Backup=$backup;Existed=$existed})
    }finally{if(Test-Path -LiteralPath $temporary){[IO.File]::Delete($temporary)}}
}

function Restore-IntegrationFiles($Changes){
    $failures=@()
    for($index=$Changes.Count-1;$index -ge 0;$index--){
        $change=$Changes[$index]
        try{
            if($change.Existed){
                if(Test-Path -LiteralPath $change.Target){
                    $discard=$change.Target+'.'+[guid]::NewGuid().ToString('N')+'.failed'
                    # Replace opens its source for writing, which fails if the
                    # original image is still mapped. Renames preserve that image.
                    [IO.File]::Move($change.Target,$discard)
                    try{[IO.File]::Move($change.Backup,$change.Target)}
                    catch{[IO.File]::Move($discard,$change.Target);throw}
                    try{[IO.File]::Delete($discard)}catch{Write-Warning ('Could not remove failed-update backup: '+$discard)}
                }
                else{[IO.File]::Move($change.Backup,$change.Target)}
            }elseif(Test-Path -LiteralPath $change.Target){[IO.File]::Delete($change.Target)}
        }catch{$failures+=@($change.Target+': '+$_.Exception.Message)}
    }
    if($failures.Count){throw ('Rollback could not restore: '+($failures -join '; '))}
}

$StreamerDirectory=[IO.Path]::GetFullPath($StreamerDirectory)
$exe=Join-Path $StreamerDirectory 'VirtualDesktop.Streamer.exe'
$dll=Join-Path $StreamerDirectory 'VirtualDesktop.LibraryOverrides.dll'
$config=Join-Path $StreamerDirectory 'VirtualDesktop.Streamer.exe.config'
$profilePath=Join-Path $StreamerDirectory 'VirtualDesktop.Unlock.Compatibility.xml'
$recordPath=Join-Path $StreamerDirectory 'VirtualDesktop.LibraryOverrides.install.json'
$payload=Join-Path $PSScriptRoot 'runtime-override'
$manifest=Get-Content -LiteralPath (Join-Path $payload 'manifest.json') -Raw | ConvertFrom-Json
$action=if($Uninstall){'Remove the VD Unlock Streamer helper and restart Streamer'}else{'Install the VD Unlock Streamer helper and restart Streamer'}
if(!$PSCmdlet.ShouldProcess($StreamerDirectory,$action)){return}

$principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    if($StreamerDirectory.Contains('"')){throw 'Invalid installation path.'}
    $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -StreamerDirectory "'+$StreamerDirectory+'"'
    if($Uninstall){$arguments+=' -Uninstall'}
    $elevated=Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $elevated.WaitForExit()
    if($elevated.ExitCode -ne 0){throw 'Elevated installer failed. See the installation log in ProgramData\Virtual Desktop Unlock.'}
    return
}

$logFolder=Join-Path $env:ProgramData 'Virtual Desktop Unlock'
$locationKey='HKLM:\SOFTWARE\Virtual Desktop Unlock'
New-Item -ItemType Directory -Path $logFolder -Force | Out-Null
Start-Transcript -LiteralPath (Join-Path $logFolder 'installer.log') -Append | Out-Null
try{
    if(!(Test-Path -LiteralPath $exe)){throw 'Virtual Desktop Streamer was not found.'}
    $supportedHashes=if($manifest.SupportedStreamers){@($manifest.SupportedStreamers | ForEach-Object {$_.SHA256})}else{@($manifest.StreamerSHA256)}
    $profileSource=Join-Path $payload 'VirtualDesktop.Unlock.Compatibility.xml'
    if(!$Uninstall -and (Test-Path -LiteralPath $profileSource)){
        $reader=[Xml.XmlReader]::Create($profileSource,(New-Object Xml.XmlReaderSettings -Property @{DtdProcessing=[Xml.DtdProcessing]::Prohibit;XmlResolver=$null}))
        try{$reader.MoveToContent() | Out-Null; $analyzedHash=$reader.GetAttribute('sha256')}finally{$reader.Dispose()}
        $supportedHashes+=@($analyzedHash)
    }
    if(!$Uninstall -and (Get-FileHash -LiteralPath $exe).Hash -notin $supportedHashes){throw 'Unsupported Streamer binary. Library integration supports verified 1.34.23.0 and 1.34.25.0 builds. No installation files were changed.'}
    $signature=Get-AuthenticodeSignature -LiteralPath $exe
    if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN="?Virtual Desktop, Inc'){throw 'Streamer must have a valid Virtual Desktop developer signature.'}
    $record=if(Test-Path -LiteralPath $recordPath){Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json}else{$null}
    if($Uninstall){
        if(!$record){throw 'No managed installation record was found. No files were removed.'}
        if($record.Product -ne 'Virtual Desktop Unlock'){throw 'Unknown installation record.'}
        foreach($item in @(@($dll,$record.HelperSHA256),@($config,$record.ConfigSHA256))){
            if((Test-Path -LiteralPath $item[0]) -and (Get-FileHash -LiteralPath $item[0]).Hash -ne $item[1]){throw ('File has changed since installation; refusing to remove it: '+$item[0])}
        }
    }else{
        foreach($item in @(@((Join-Path $payload 'VirtualDesktop.LibraryOverrides.dll'),$manifest.HelperSHA256),@((Join-Path $payload 'VirtualDesktop.Streamer.exe.config'),$manifest.ConfigSHA256))){
            if((Get-FileHash -LiteralPath $item[0]).Hash -ne $item[1]){throw 'Release payload hash check failed.'}
        }
        if($record -and $record.Product -ne 'Virtual Desktop Unlock'){throw 'Unknown existing installation record.'}
        foreach($item in @(@($dll,$record.HelperSHA256),@($config,$record.ConfigSHA256))){
            if((Test-Path -LiteralPath $item[0]) -and (!$record -or (Get-FileHash -LiteralPath $item[0]).Hash -ne $item[1])){throw ('An unrelated or different-version file already exists: '+$item[0]+'. Remove that helper with its original uninstaller before installing this release.')}
        }
    }
    if((Test-Path -LiteralPath $profilePath) -and (!$record -or !$record.CompatibilitySHA256 -or (Get-FileHash -LiteralPath $profilePath).Hash -ne $record.CompatibilitySHA256)){
        throw 'The existing compatibility profile is not managed by this installation or has changed. No files were changed.'
    }
    Initialize-InstallerProcessControl
    Invoke-WithStreamerServicePaused {
    $script:installerStoppedStreamer=$false
    if($Uninstall){
        Stop-InstalledStreamer $exe
        Wait-IntegrationFilesUnlocked @($dll,$config,$recordPath,$profilePath)
    }
    if($Uninstall){
        foreach($path in @($config,$dll,$recordPath,$profilePath)){if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path}}
        if((Get-ItemProperty -LiteralPath $locationKey -ErrorAction SilentlyContinue).StreamerExecutable -eq $exe){
            Remove-ItemProperty -LiteralPath $locationKey -Name StreamerExecutable
        }
        Start-Process -FilePath $exe -WorkingDirectory $StreamerDirectory -WindowStyle Hidden
        'Removed the startup helper. The signed Streamer executable is unchanged.'
        return
    }
    $process=$null
    $changes=New-Object 'System.Collections.Generic.List[object]'
    try{
        Stop-InstalledStreamer $exe
        Wait-IntegrationFilesUnlocked @($dll,$config,$recordPath,$profilePath)
        Set-IntegrationFile -Target $dll -Source (Join-Path $payload 'VirtualDesktop.LibraryOverrides.dll') -Changes $changes
        Set-IntegrationFile -Target $config -Source (Join-Path $payload 'VirtualDesktop.Streamer.exe.config') -Changes $changes
        if(Test-Path -LiteralPath $profileSource){Set-IntegrationFile -Target $profilePath -Source $profileSource -Changes $changes}
        elseif(Test-Path -LiteralPath $profilePath){
            $backup=$profilePath+'.'+[guid]::NewGuid().ToString('N')+'.rollback'
            [IO.File]::Move($profilePath,$backup)
            $changes.Add([pscustomobject]@{Target=$profilePath;Backup=$backup;Existed=$true})
        }
        $process=Start-Process -FilePath $exe -WorkingDirectory $StreamerDirectory -WindowStyle Hidden -PassThru
        if($process.WaitForExit(4000)){throw ('Streamer exited during startup: '+$process.ExitCode)}
        if((Get-FileHash -LiteralPath $exe).Hash -notin $supportedHashes){throw 'Streamer executable changed unexpectedly.'}
        $recordJson=[pscustomobject]@{Product='Virtual Desktop Unlock';Version=$manifest.Version;HelperSHA256=$manifest.HelperSHA256;ConfigSHA256=$manifest.ConfigSHA256;CompatibilitySHA256=$(if(Test-Path -LiteralPath $profilePath){(Get-FileHash -LiteralPath $profilePath).Hash}else{$null});Installed=[DateTimeOffset]::Now.ToString('O')} | ConvertTo-Json
        Set-IntegrationFile -Target $recordPath -Contents ([Text.Encoding]::UTF8.GetBytes($recordJson)) -Changes $changes
        if(!(Test-Path -LiteralPath $locationKey)){New-Item -Path $locationKey | Out-Null}
        New-ItemProperty -LiteralPath $locationKey -Name StreamerExecutable -Value $exe -PropertyType String -Force | Out-Null
        foreach($change in $changes){if(Test-Path -LiteralPath $change.Backup){try{[IO.File]::Delete($change.Backup)}catch{Write-Warning ('Could not remove old backup: '+$change.Backup)}}}
        'Installed the VD Unlock Streamer helper. Reconnect the headset and reopen Virtual Desktop Games.'
    }catch{
        $failure=$_
        if($process -and !$process.HasExited){
            try{$process.Kill();if(!$process.WaitForExit(15000)){throw 'New Streamer did not exit before rollback.'}}
            catch{throw ($failure.Exception.Message+'; could not stop the new Streamer for rollback: '+$_.Exception.Message)}
        }
        try{Restore-IntegrationFiles $changes}catch{throw ($failure.Exception.Message+'; '+$_.Exception.Message)}
        if($script:installerStoppedStreamer){Start-Process -FilePath $exe -WorkingDirectory $StreamerDirectory -WindowStyle Hidden}
        throw $failure
    }
    }
}finally{Stop-Transcript | Out-Null}
