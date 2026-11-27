param([Parameter(Mandatory=$true)][string]$Tool,[Parameter(Mandatory=$true)][string]$Streamer,[Parameter(Mandatory=$true)][string]$Signatures,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$map=Join-Path $OutputDirectory 'resolved.json'
& $Tool analyze $Streamer $Signatures $map
if($LASTEXITCODE -ne 0){throw 'Compatible Streamer was rejected.'}
$profile=$map+'.xml'
& $Tool verify $Streamer $profile
if($LASTEXITCODE -ne 0){throw 'Generated profile failed runtime validation.'}
'PASS Complete generated mapping passes independent runtime verification'
function Reject-Profile($name,[scriptblock]$mutate){
    [xml]$document=Get-Content -LiteralPath $profile -Raw
    & $mutate $document
    $path=Join-Path $OutputDirectory ($name+'.xml');$document.Save($path)
    $output=& $Tool verify $Streamer $path 2>&1
    if($LASTEXITCODE -eq 0){throw ('Unsafe profile accepted: '+$name)}
    'PASS Rejected '+$name
}
Reject-Profile 'changed-sha256' {param($document) $document.StreamerCompatibility.sha256='0'*64}
Reject-Profile 'changed-mvid' {param($document) $document.StreamerCompatibility.mvid=[guid]::NewGuid().ToString()}
Reject-Profile 'missing-symbol' {param($document) $null=$document.StreamerCompatibility.RemoveChild($document.StreamerCompatibility.Symbol[0])}
Reject-Profile 'wrong-symbol' {param($document) $document.StreamerCompatibility.Symbol[0].actual='040FFFFF'}
$xxe=Join-Path $OutputDirectory 'xxe.xml'
[IO.File]::WriteAllText($xxe,'<!DOCTYPE x [<!ENTITY x SYSTEM "file:///not-allowed">]><StreamerCompatibility format="1"/>')
$output=& $Tool verify $Streamer $xxe 2>&1
if($LASTEXITCODE -eq 0){throw 'Unsafe profile XML was accepted.'}
'PASS Rejected external entity profile XML'
$empty=Join-Path $OutputDirectory 'empty-signatures.xml'
[IO.File]::WriteAllText($empty,'<StreamerSignatures version="1"/>')
$output=& $Tool analyze $Streamer $empty (Join-Path $OutputDirectory 'unknown.json') 2>&1
if($LASTEXITCODE -eq 0){throw 'Missing hook signatures were accepted.'}
'PASS Missing signatures stop compatibility analysis'
'Compatibility rejection checks passed: 8'
