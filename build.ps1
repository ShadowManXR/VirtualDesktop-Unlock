[CmdletBinding()]
param([string]$StreamerPath,[switch]$SkipTests)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$version='0.3.0-alpha'
$env:DOTNET_CLI_HOME=Join-Path $root '.dotnet-build'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$work=Join-Path $root ('artifacts\build-'+[guid]::NewGuid().ToString('N'))
$publish=Join-Path $work 'publish'
$hook=Join-Path $work 'runtime-override'
$tests=Join-Path $work 'runtime-tests'
$stage=Join-Path $work ('VirtualDesktopUnlock-v'+$version+'-win-x64')
$dist=Join-Path $root 'artifacts\dist'
foreach($path in @($publish,$hook,$tests,$stage,$dist)){New-Item -ItemType Directory -Path $path -Force | Out-Null}
& dotnet publish (Join-Path $root 'SteamVRLibrary\SteamVRLibrary.csproj') -c Release -o $publish ("-p:Version=$version") ('-p:OutputPath='+(Join-Path $work 'app-build')) --configfile (Join-Path $root 'NuGet.Config')
if($LASTEXITCODE -ne 0){throw 'Companion app build failed.'}
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework=Split-Path -Parent $compiler
$references=@("/reference:$framework\WPF\PresentationFramework.dll","/reference:$framework\WPF\WindowsBase.dll",'/reference:System.Xaml.dll')
$dependencies=Join-Path $root 'artifacts\dependencies'
New-Item -ItemType Directory -Path $dependencies -Force | Out-Null
$webpFiles=@(
    @{Name='libwebp-1.6.0-windows-x64.zip'; Url='https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-1.6.0-windows-x64.zip'; Hash='48886F506B21F62E4661F0F4CBFCA19800897C385128E8902542D29A950C93F1'},
    @{Name='libwebp-COPYING.txt'; Url='https://raw.githubusercontent.com/webmproject/libwebp/v1.6.0/COPYING'; Hash='5AEC868F669E384A22372A4E8A1A6CD7D44C64CD451F960CA69CC170D1E13ACF'},
    @{Name='libwebp-PATENTS.txt'; Url='https://raw.githubusercontent.com/webmproject/libwebp/v1.6.0/PATENTS'; Hash='CC3273E0694EA5896145E0677699B53471B03EA43021DDC50E7923FBB9F5023C'}
)
foreach($file in $webpFiles){
    $path=Join-Path $dependencies $file.Name
    if(!(Test-Path -LiteralPath $path)){Invoke-WebRequest -Uri $file.Url -OutFile $path}
    if((Get-FileHash -LiteralPath $path).Hash -ne $file.Hash){throw "libwebp dependency hash mismatch: $($file.Name)"}
    if($file.Name.EndsWith('.txt')){Copy-Item -LiteralPath $path -Destination $stage}
}
$webpArchive=Join-Path $dependencies 'libwebp-1.6.0-windows-x64.zip'
Expand-Archive -LiteralPath $webpArchive -DestinationPath $work
$decoder=Join-Path $work 'libwebp-1.6.0-windows-x64\bin\dwebp.exe'
$ventureSources=@('/reference:System.Web.Extensions.dll','/reference:System.Drawing.dll',
    "/resource:$decoder,VDUnlock.dwebp.exe",
    (Join-Path $root 'src\RuntimeOverrides\VentureRuntime.cs'),
    (Join-Path $root 'src\RuntimeOverrides\VentureWebp.cs'),
    (Join-Path $root 'src\RuntimeOverrides\VentureGameProcess.cs'),
    (Join-Path $root 'Shared\VenturePreferences.cs'))
$remapSources=@((Join-Path $root 'src\RuntimeOverrides\ButtonRemapRuntime.cs'),(Join-Path $root 'Shared\ButtonRemapProfile.cs'))
& $compiler /nologo /target:library ('/out:'+(Join-Path $hook 'VirtualDesktop.LibraryOverrides.dll')) $references $ventureSources $remapSources (Join-Path $root 'src\RuntimeOverrides\LibraryOverrides.cs') (Join-Path $root 'src\RuntimeOverrides\BitrateRuntime.cs') (Join-Path $root 'src\RuntimeOverrides\LaunchPresetRuntime.cs') (Join-Path $root 'src\RuntimeOverrides\LaunchPresetRecovery.cs') (Join-Path $root 'src\RuntimeOverrides\SteamGameLifetime.cs') (Join-Path $root 'Shared\LaunchPresetConfiguration.cs') (Join-Path $root 'Shared\BitrateConfiguration.cs') (Join-Path $root 'Shared\FoveaSizePreferences.cs')
if($LASTEXITCODE -ne 0){throw 'Runtime helper build failed.'}
Copy-Item -LiteralPath (Join-Path $root 'src\RuntimeOverrides\RuntimeHook.config') -Destination (Join-Path $hook 'VirtualDesktop.Streamer.exe.config')
[pscustomobject]@{Version=$version;StreamerVersion='1.34.25.0';StreamerSHA256='0229A322137F55687240D2B51F5F7002056D9277D7FA001EC6CB4EB25AF0BFB8';SupportedStreamers=@(@{Version='1.34.23.0';SHA256='FEAD548C57DF539006E6C97A7A29D46FDE172F756F40D65DBDB77ABAAFA3AEAD'},@{Version='1.34.25.0';SHA256='0229A322137F55687240D2B51F5F7002056D9277D7FA001EC6CB4EB25AF0BFB8'});HelperSHA256=(Get-FileHash -LiteralPath (Join-Path $hook 'VirtualDesktop.LibraryOverrides.dll')).Hash;ConfigSHA256=(Get-FileHash -LiteralPath (Join-Path $hook 'VirtualDesktop.Streamer.exe.config')).Hash} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $hook 'manifest.json')
if(!$SkipTests){
    & $compiler /nologo /optimize+ /target:exe ('/out:'+(Join-Path $hook 'LaunchPresetTests.exe')) ('/reference:'+(Join-Path $hook 'VirtualDesktop.LibraryOverrides.dll')) (Join-Path $root 'src\RuntimeOverrides\LaunchPresetTests.cs')
    if($LASTEXITCODE -ne 0){throw 'Launch-preset tests did not compile.'}
    & (Join-Path $hook 'LaunchPresetTests.exe') (Join-Path $tests 'launch-presets')
    if($LASTEXITCODE -ne 0){throw 'Launch-preset tests failed.'}
    & $compiler /nologo /target:exe ('/out:'+(Join-Path $hook 'LaunchPresetRecoveryTests.exe')) ('/reference:'+(Join-Path $hook 'VirtualDesktop.LibraryOverrides.dll')) (Join-Path $root 'src\RuntimeOverrides\LaunchPresetRecoveryTests.cs')
    if($LASTEXITCODE -ne 0){throw 'Recovery tests did not compile.'}
    & (Join-Path $hook 'LaunchPresetRecoveryTests.exe') (Join-Path $tests 'recovery')
    if($LASTEXITCODE -ne 0){throw 'Recovery tests failed.'}
    $test=Start-Process -FilePath (Join-Path $publish 'Virtual Desktop Unlock.exe') -ArgumentList '--self-test' -WindowStyle Hidden -PassThru
    $test.WaitForExit()
    Get-Content -LiteralPath (Join-Path $publish 'self-test-result.txt') | Write-Output
    if($test.ExitCode -ne 0){throw 'Companion tests failed.'}
    & $compiler /nologo /target:exe ('/out:'+(Join-Path $tests 'RuntimeHookTest.exe')) $references (Join-Path $root 'src\RuntimeOverrides\RuntimeHookTest.cs')
    if($LASTEXITCODE -ne 0){throw 'Runtime test harness build failed.'}
    Copy-Item -LiteralPath (Join-Path $hook 'VirtualDesktop.LibraryOverrides.dll') -Destination $tests
    Copy-Item -LiteralPath (Join-Path $hook 'VirtualDesktop.Streamer.exe.config') -Destination (Join-Path $tests 'RuntimeHookTest.exe.config')
    & (Join-Path $tests 'RuntimeHookTest.exe')
    if($LASTEXITCODE -ne 0){throw 'AppDomain startup-loading smoke test failed.'}
    if($StreamerPath){
        & $compiler /nologo /target:exe ('/out:'+(Join-Path $hook 'LaunchPresetHookTest.exe')) $references ('/reference:'+(Join-Path $hook 'VirtualDesktop.LibraryOverrides.dll')) (Join-Path $root 'src\RuntimeOverrides\LaunchPresetHookTest.cs')
        if($LASTEXITCODE -ne 0){throw 'Launch-preset hook harness did not compile.'}
        & (Join-Path $hook 'LaunchPresetHookTest.exe') ([IO.Path]::GetFullPath($StreamerPath)) (Join-Path $tests 'launch-hook')
        if($LASTEXITCODE -ne 0){throw 'Isolated launch-preset hook test failed.'}
        if([Diagnostics.FileVersionInfo]::GetVersionInfo($StreamerPath).FileVersion -eq '1.34.25.0'){
            & $compiler /nologo /target:exe ('/out:'+(Join-Path $hook 'ButtonRemapHookTest.exe')) ('/reference:'+(Join-Path $hook 'VirtualDesktop.LibraryOverrides.dll')) (Join-Path $root 'src\RuntimeOverrides\ButtonRemapHookTest.cs')
            if($LASTEXITCODE -ne 0){throw 'Button remap hook harness did not compile.'}
            & (Join-Path $hook 'ButtonRemapHookTest.exe') ([IO.Path]::GetFullPath($StreamerPath))
            if($LASTEXITCODE -ne 0){throw 'Isolated button remap hook test failed.'}
            & $compiler /nologo /target:exe ('/out:'+(Join-Path $tests 'VentureIntegrationTest.exe')) (Join-Path $root 'src\RuntimeOverrides\VentureIntegrationTest.cs')
            if($LASTEXITCODE -ne 0){throw 'Venture integration test did not compile.'}
            & (Join-Path $tests 'VentureIntegrationTest.exe') ([IO.Path]::GetFullPath($StreamerPath)) (Join-Path $hook 'VirtualDesktop.LibraryOverrides.dll') (Join-Path $tests 'venture-integration') (Join-Path $work 'libwebp-1.6.0-windows-x64\test.webp')
            if($LASTEXITCODE -ne 0){throw 'Isolated Venture game-list test failed.'}
            & $compiler /nologo /target:exe ('/out:'+(Join-Path $tests 'FovHookTest.exe')) (Join-Path $root 'src\RuntimeOverrides\FovHookTest.cs')
            if($LASTEXITCODE -ne 0){throw 'FOV forwarding harness did not compile.'}
            & (Join-Path $tests 'FovHookTest.exe') ([IO.Path]::GetFullPath($StreamerPath)) (Join-Path $tests 'fov-settings') (Join-Path $hook 'VirtualDesktop.LibraryOverrides.dll')
            if($LASTEXITCODE -ne 0){throw 'Isolated FOV forwarding test failed.'}
            & $compiler /nologo /target:exe ('/out:'+(Join-Path $tests 'RenderSharpenHookTest.exe')) (Join-Path $root 'src\RuntimeOverrides\RenderSharpenHookTest.cs')
            if($LASTEXITCODE -ne 0){throw 'Render/sharpen forwarding harness did not compile.'}
            & (Join-Path $tests 'RenderSharpenHookTest.exe') ([IO.Path]::GetFullPath($StreamerPath)) (Join-Path $tests 'render-sharpen-settings') (Join-Path $hook 'VirtualDesktop.LibraryOverrides.dll')
            if($LASTEXITCODE -ne 0){throw 'Isolated render/sharpen forwarding test failed.'}
        }
        $oldData=$env:VD_LIBRARY_TEST_DATA;$oldDelay=$env:VD_LIBRARY_TEST_DELAYED;$oldLog=$env:VD_LIBRARY_OVERRIDE_LOG
        $oldFile=$env:VD_LIBRARY_OVERRIDES_FILE;$oldExpected=$env:VD_LIBRARY_TEST_EXPECTED_IDS
        try{
            $env:VD_LIBRARY_TEST_DATA=Join-Path $tests 'data'; New-Item -ItemType Directory -Path $env:VD_LIBRARY_TEST_DATA -Force | Out-Null
            $cases=@(
                @{Name='re3';Ids='952060';Xml='<VirtualDesktopLibrary version="1"><Game appId="952060"/></VirtualDesktopLibrary>';Delayed=$true},
                @{Name='gothic-and-re3';Ids='65540,952060';Xml='<VirtualDesktopLibrary version="1"><Game appId="65540"/><Game appId="952060"/></VirtualDesktopLibrary>';Delayed=$true},
                @{Name='arbitrary-and-builtin';Ids='123456789,271590,6020';Xml='<VirtualDesktopLibrary version="1"><Game appId="123456789"/><Game appId="271590"/><Game appId="6020"/><Game appId="123456789"/></VirtualDesktopLibrary>';Delayed=$true},
                @{Name='empty';Ids=',';Xml='<VirtualDesktopLibrary version="1"/>';Delayed=$false},
                @{Name='invalid';Ids=',';Xml='<!DOCTYPE x [<!ENTITY x SYSTEM "file:///not-allowed">]><VirtualDesktopLibrary version="1">&x;</VirtualDesktopLibrary>';Delayed=$false}
            )
            foreach($case in $cases){
                $env:VD_LIBRARY_OVERRIDES_FILE=Join-Path $tests ($case.Name+'.xml')
                [IO.File]::WriteAllText($env:VD_LIBRARY_OVERRIDES_FILE,$case.Xml)
                $env:VD_LIBRARY_TEST_EXPECTED_IDS=$case.Ids
                $env:VD_LIBRARY_TEST_DELAYED=if($case.Delayed){'1'}else{$null}
                $env:VD_LIBRARY_OVERRIDE_LOG=Join-Path $tests ($case.Name+'.log')
                if($case.Delayed){& (Join-Path $tests 'RuntimeHookTest.exe') ([IO.Path]::GetFullPath($StreamerPath)) --delayed}
                else{& (Join-Path $tests 'RuntimeHookTest.exe') ([IO.Path]::GetFullPath($StreamerPath))}
                if($LASTEXITCODE -ne 0){throw ('Streamer override test failed: '+$case.Name)}
                if($case.Name -eq 'invalid' -and !(Get-Content -LiteralPath $env:VD_LIBRARY_OVERRIDE_LOG -Raw).Contains('Override failed:')){throw 'Unsafe XML was not rejected.'}
            }
            & $compiler /nologo /target:exe ('/out:'+(Join-Path $tests 'BitrateHookTest.exe')) ('/reference:'+(Join-Path $hook 'VirtualDesktop.LibraryOverrides.dll')) (Join-Path $root 'src\RuntimeOverrides\BitrateHookTest.cs')
            if($LASTEXITCODE -ne 0){throw 'Bitrate server test harness build failed.'}
            Copy-Item -LiteralPath (Join-Path $hook 'VirtualDesktop.Streamer.exe.config') -Destination (Join-Path $tests 'BitrateHookTest.exe.config')
            $savedBitrateTest=$env:VD_BITRATE_TEST; $savedBitrateConfig=$env:VD_BITRATE_CONFIG; $savedBitrateStatus=$env:VD_BITRATE_STATUS
            try{
                $env:VD_BITRATE_TEST='1'; $env:VD_BITRATE_CONFIG=Join-Path $tests 'BitrateOverrides.xml'; $env:VD_BITRATE_STATUS=Join-Path $tests 'BitrateStatus.xml'
                $env:VD_LIBRARY_OVERRIDES_FILE=Join-Path $tests 're3.xml'; $env:VD_LIBRARY_OVERRIDE_LOG=Join-Path $tests 'bitrate-hook.log'
                if([Diagnostics.FileVersionInfo]::GetVersionInfo($StreamerPath).FileVersion -eq '1.34.23.0' -or [Diagnostics.FileVersionInfo]::GetVersionInfo($StreamerPath).FileVersion -eq '1.34.25.0'){
                    & (Join-Path $tests 'BitrateHookTest.exe') ([IO.Path]::GetFullPath($StreamerPath))
                    if($LASTEXITCODE -ne 0){throw 'Bitrate server tests failed.'}
                }else{
                    $env:VD_LIBRARY_TEST_DELAYED=$null;$env:VD_LIBRARY_TEST_EXPECTED_IDS='952060'
                    '<BitrateOverrides version="1" enabled="true"><Profile headset="1000" h264="1000" hevc="300" av1="250"/></BitrateOverrides>' | Set-Content -LiteralPath $env:VD_BITRATE_CONFIG
                    & (Join-Path $tests 'RuntimeHookTest.exe') ([IO.Path]::GetFullPath($StreamerPath))
                    if($LASTEXITCODE -ne 0){throw 'Library override failed with unsupported bitrate configuration.'}
                    [xml]$bitrateStatus=Get-Content -LiteralPath $env:VD_BITRATE_STATUS -Raw
                    if($bitrateStatus.BitrateStatus.unsupported -ne 'true' -or $bitrateStatus.BitrateStatus.ready -ne 'false'){throw 'Unsupported bitrate version did not report its limitation.'}
                    'PASS Unsupported bitrate patch stays inactive while game-library overrides apply'
                }
            }finally{$env:VD_BITRATE_TEST=$savedBitrateTest;$env:VD_BITRATE_CONFIG=$savedBitrateConfig;$env:VD_BITRATE_STATUS=$savedBitrateStatus}
        }finally{
            $env:VD_LIBRARY_TEST_DATA=$oldData;$env:VD_LIBRARY_TEST_DELAYED=$oldDelay;$env:VD_LIBRARY_OVERRIDE_LOG=$oldLog
            $env:VD_LIBRARY_OVERRIDES_FILE=$oldFile;$env:VD_LIBRARY_TEST_EXPECTED_IDS=$oldExpected
        }
    }
}
foreach($name in @('Virtual Desktop Unlock.exe','Virtual Desktop Unlock.dll','Virtual Desktop Unlock.deps.json','Virtual Desktop Unlock.runtimeconfig.json')){Copy-Item -LiteralPath (Join-Path $publish $name) -Destination $stage}
New-Item -ItemType Directory -Path (Join-Path $stage 'runtime-override') -Force | Out-Null
foreach($name in @('VirtualDesktop.LibraryOverrides.dll','VirtualDesktop.Streamer.exe.config','manifest.json')){Copy-Item -LiteralPath (Join-Path $hook $name) -Destination (Join-Path $stage 'runtime-override')}
Get-ChildItem -LiteralPath (Join-Path $root 'scripts') -File |
    Where-Object { $_.Extension -in '.cmd','.ps1' } | Copy-Item -Destination $stage
$setupName='Install Streamer Helper.exe'
$setupResources=@("/resource:$root\scripts\HelperInstaller.ps1,HelperInstaller.ps1")
foreach($name in @('VirtualDesktop.LibraryOverrides.dll','VirtualDesktop.Streamer.exe.config','manifest.json')){
    $setupResources+=("/resource:"+(Join-Path $hook $name)+","+$name)
}
$setupOptions=@('/nologo','/target:winexe','/platform:x64','/reference:System.Windows.Forms.dll',
    ('/out:'+(Join-Path $stage $setupName)),('/win32icon:'+(Join-Path $root 'assets\icon.ico')),
    ('/win32manifest:'+(Join-Path $root 'scripts\StreamerSetup.manifest')))
& $compiler $setupOptions $setupResources (Join-Path $root 'scripts\StreamerSetup.cs') (Join-Path $root 'Shared\StreamerLocation.cs')
if($LASTEXITCODE -ne 0){throw 'Streamer setup build failed.'}
foreach($name in @('README.md','LICENSE','CHANGELOG.md','TESTING.md')){Copy-Item -LiteralPath (Join-Path $root $name) -Destination $stage}
New-Item -ItemType Directory -Path (Join-Path $stage 'assets') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'assets\README.md') -Destination (Join-Path $stage 'assets\README.md')
$zip=Join-Path $dist ('VirtualDesktopUnlock-v'+$version+'-win-x64.zip')
Compress-Archive -LiteralPath $stage -DestinationPath $zip -Force
$sum=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+(Split-Path -Leaf $zip)
$sum | Set-Content -LiteralPath ($zip+'.sha256') -Encoding ASCII
$setup=Join-Path $dist ('VirtualDesktopUnlock-v'+$version+'-Streamer-Setup.exe')
Copy-Item -LiteralPath (Join-Path $stage $setupName) -Destination $setup -Force
((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+(Split-Path -Leaf $setup)) |
    Set-Content -LiteralPath ($setup+'.sha256') -Encoding ASCII
Write-Output ('Streamer setup: '+$setup)
Write-Output ('Release: '+$zip)
Write-Output ('Staging directory: '+$stage)
