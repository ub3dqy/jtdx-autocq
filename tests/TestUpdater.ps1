param([string]$BuildDirectory,[string]$HelperPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root=Join-Path $BuildDirectory ('update-helper-tests-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$helper=Join-Path $root 'helper.ps1'
[IO.File]::WriteAllText($helper,[IO.File]::ReadAllText($HelperPath),(New-Object Text.UTF8Encoding($true)))
. $helper -LibraryOnly
$script:passed=0
function Assert-UpdateTest($Condition,[string]$Message) { if(!$Condition){throw $Message} }
function Test-UpdateCase([string]$Name,[scriptblock]$Action) { & $Action;$script:passed++;Write-Output ('PASS  '+$Name) }
function New-UpdateFixture {
    $directory=Join-Path $root ([guid]::NewGuid().ToString('N'))
    $app=Join-Path $directory 'Radio tools';$profile=Join-Path $directory 'profile';$payload=Join-Path $directory 'payload'
    foreach($folder in @($app,$profile,$payload)){[void][IO.Directory]::CreateDirectory($folder)}
    $exe=Join-Path $app 'JTDX-AutoCQ.exe';$demo=Join-Path $app 'JTDX-AutoCQ-Demo.exe';$native=Join-Path $app 'jtdx.exe';$settings=Join-Path $profile 'operator.xml'
    [IO.File]::WriteAllText($exe,'previous-module');[IO.File]::WriteAllText($demo,'previous-demo');[IO.File]::WriteAllText($native,'previous-native');[IO.File]::WriteAllText($settings,'keep-profile');[IO.File]::WriteAllText((Join-Path $app 'README.md'),'previous-guide')
    $nativeWanted=Join-Path $directory 'native-wanted';[IO.File]::WriteAllText($nativeWanted,'updated-native')
    $nativeHash=Get-UpdateHash $nativeWanted
    Copy-Item -LiteralPath (Join-Path $BuildDirectory 'JTDX-AutoCQ.exe') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $BuildDirectory 'JTDX-AutoCQ-Patch.exe') -Destination $payload
    [IO.File]::WriteAllText((Join-Path $payload 'README.md'),'new-guide')
    $version=([version][Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'JTDX-AutoCQ.exe')).FileVersion).ToString(3)
    @{version=$version;jtdxSha256=$nativeHash}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $payload 'update.json') -Encoding UTF8
    $zip=Join-Path $directory 'update.zip';[IO.Compression.ZipFile]::CreateFromDirectory($payload,$zip)
    return [pscustomobject]@{Directory=$directory;App=$app;Settings=$settings;NativeWanted=$nativeWanted;BeforeExe=(Get-UpdateHash $exe);BeforeDemo=(Get-UpdateHash $demo);BeforeNative=(Get-UpdateHash $native);Request=[pscustomobject]@{TargetExe=$exe;JtdxExe=$native;Archive=$zip;ArchiveSha256=(Get-UpdateHash $zip);Version=$version;JtdxSha256=$nativeHash}}
}
$patch={param($tool,$file)[IO.File]::WriteAllText($file,'updated-native')}
Test-UpdateCase 'Update helper installs verified files and keeps operator settings' {
    $f=New-UpdateFixture
    $null=Invoke-AutoCQUpdate -Request $f.Request -JtdxRunning {$false} -ApplyPatch $patch -ProtectedPaths @($f.Settings)
    Assert-UpdateTest ((Get-UpdateHash $f.Request.TargetExe) -eq (Get-UpdateHash (Join-Path $BuildDirectory 'JTDX-AutoCQ.exe'))) 'Wrong updated module'
    Assert-UpdateTest ((Get-UpdateHash (Join-Path $f.App 'JTDX-AutoCQ-Demo.exe')) -eq (Get-UpdateHash $f.Request.TargetExe)) 'Demo was not updated'
    Assert-UpdateTest ((Get-UpdateHash $f.Request.JtdxExe) -eq $f.Request.JtdxSha256) 'Native patch was not applied'
    Assert-UpdateTest ([IO.File]::ReadAllText($f.Settings) -eq 'keep-profile') 'Operator data changed'
    Assert-UpdateTest ([IO.File]::ReadAllText((Join-Path $f.App 'README.md')) -eq 'new-guide') 'Guide was not updated'
}
Test-UpdateCase 'Update helper rolls application and native files back after a partial failure' {
    $f=New-UpdateFixture;$failed=$false
    try{$null=Invoke-AutoCQUpdate -Request $f.Request -JtdxRunning {$false} -ApplyPatch $patch -ProtectedPaths @($f.Settings) -AfterFile {param($index)if($index -eq 1){throw 'Injected write failure'}}}catch{$failed=$true}
    Assert-UpdateTest $failed 'Injected failure was not observed'
    Assert-UpdateTest ((Get-UpdateHash $f.Request.TargetExe) -eq $f.BeforeExe) 'Main module was not restored'
    Assert-UpdateTest ((Get-UpdateHash (Join-Path $f.App 'JTDX-AutoCQ-Demo.exe')) -eq $f.BeforeDemo) 'Demo was not restored'
    Assert-UpdateTest ((Get-UpdateHash $f.Request.JtdxExe) -eq $f.BeforeNative) 'Native executable was not restored'
    Assert-UpdateTest ([IO.File]::ReadAllText($f.Settings) -eq 'keep-profile') 'Settings were not preserved'
    Assert-UpdateTest ([IO.File]::ReadAllText((Join-Path $f.App 'README.md')) -eq 'previous-guide') 'Guide was not restored'
}
Test-UpdateCase 'Update helper refuses installation while JTDX is running' {
    $f=New-UpdateFixture;$failed=$false
    try{$null=Invoke-AutoCQUpdate -Request $f.Request -JtdxRunning {$true} -ApplyPatch $patch}catch{$failed=$true}
    Assert-UpdateTest ($failed -and (Get-UpdateHash $f.Request.TargetExe) -eq $f.BeforeExe -and (Get-UpdateHash $f.Request.JtdxExe) -eq $f.BeforeNative) 'Running JTDX did not block file changes'
}
Test-UpdateCase 'Update helper refuses a changed archive before replacing files' {
    $f=New-UpdateFixture;$f.Request.ArchiveSha256=('0'*64);$failed=$false
    try{$null=Invoke-AutoCQUpdate -Request $f.Request -JtdxRunning {$false} -ApplyPatch $patch}catch{$failed=$true}
    Assert-UpdateTest ($failed -and (Get-UpdateHash $f.Request.TargetExe) -eq $f.BeforeExe -and (Get-UpdateHash $f.Request.JtdxExe) -eq $f.BeforeNative) 'Unverified download changed installed files'
}
Test-UpdateCase 'Update helper blocks ZIP traversal independently of the UI' {
    $f=New-UpdateFixture;$zip=Join-Path $f.Directory 'unsafe.zip'
    $archive=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Create)
    try{$writer=New-Object IO.StreamWriter($archive.CreateEntry('../escaped.txt').Open());try{$writer.Write('unsafe')}finally{$writer.Dispose()}}finally{$archive.Dispose()}
    $f.Request.Archive=$zip;$f.Request.ArchiveSha256=Get-UpdateHash $zip;$failed=$false
    try{$null=Invoke-AutoCQUpdate -Request $f.Request -JtdxRunning {$false} -ApplyPatch $patch}catch{$failed=$true}
    Assert-UpdateTest ($failed -and !(Test-Path -LiteralPath (Join-Path $f.Directory 'escaped.txt')) -and (Get-UpdateHash $f.Request.TargetExe) -eq $f.BeforeExe) 'Unsafe archive was applied'
}
Write-Output ('ALL '+$script:passed+' UPDATE HELPER CHECKS PASSED')
