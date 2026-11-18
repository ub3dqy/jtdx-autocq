[CmdletBinding()]
param([string]$RequestFile,[switch]$LibraryOnly)
$ErrorActionPreference = 'Stop'

function Get-UpdateHash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Get-UpdateChild([string]$Directory,[string]$Name) {
    if ($Name -match '(^[\\/]|:|(^|[\\/])\.\.?([\\/]|$))') { throw 'Unsafe package path.' }
    $root=[IO.Path]::GetFullPath($Directory).TrimEnd('\','/')
    $path=[IO.Path]::GetFullPath((Join-Path $root $Name))
    if (!$path.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escapes its directory.' }
    return $path
}
function Expand-UpdateArchive([string]$Archive,[string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [void][IO.Directory]::CreateDirectory($Destination)
    $zip=[IO.Compression.ZipFile]::OpenRead($Archive)
    $seen=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $total=0L
    try {
        if ($zip.Entries.Count -gt 200) { throw 'Too many package entries.' }
        foreach ($entry in $zip.Entries) {
            $name=$entry.FullName.Replace('\','/')
            $target=Get-UpdateChild $Destination $name
            if (!$seen.Add($name)) { throw 'Duplicate package path.' }
            if ($name.EndsWith('/')) { [void][IO.Directory]::CreateDirectory($target);continue }
            $total+=$entry.Length
            if ($total -gt 128MB) { throw 'Package expands beyond its size limit.' }
            if ($name -match '(?i)(^|/)(operator\.xml|connection\.xml|status\.xml|preferences\.xml|JTDX\.ini|runtime\.log|jtdx\.exe)$') { throw 'Package contains protected settings or an unsupported binary.' }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$false)
        }
    } finally { $zip.Dispose() }
}
function Test-UpdateJtdxRunning {
    return @(Get-Process -Name jtdx -ErrorAction SilentlyContinue).Count -gt 0
}
function Wait-UpdateParent($Request) {
    $deadline=[datetime]::UtcNow.AddSeconds(120)
    while ($true) {
        $parent=Get-Process -Id $Request.ParentId -ErrorAction SilentlyContinue
        if (!$parent) { return }
        try { if ($parent.StartTime.ToUniversalTime().ToString('O') -ne $Request.ParentStartUtc) { return } }
        finally { $parent.Dispose() }
        if ([datetime]::UtcNow -ge $deadline) { throw 'AutoCQ did not close. The installed files were not changed.' }
        Start-Sleep -Milliseconds 200
    }
}
function Invoke-UpdatePatch([string]$Patcher,[string]$Jtdx) {
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$Patcher
    $start.Arguments='--jtdx "'+$Jtdx+'"'
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden'
    $start.RedirectStandardError=$true;$start.RedirectStandardOutput=$true
    $process=[Diagnostics.Process]::Start($start)
    try {
        $output=$process.StandardOutput.ReadToEnd();$errorText=$process.StandardError.ReadToEnd();$process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw ('JTDX patch failed. '+$errorText) }
    } finally { $process.Dispose() }
}
function Start-UpdatedAutoCQ($Request) {
    $arguments=@('--jtdx',('"'+$Request.JtdxExe+'"'))
    if ($Request.Demo) { $arguments+='--demo' }
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $principal=New-Object Security.Principal.WindowsPrincipal($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        try {
            $shell=New-Object -ComObject Shell.Application
            $shell.ShellExecute($Request.TargetExe,($arguments -join ' '),[IO.Path]::GetDirectoryName($Request.TargetExe),'open',1)
            return
        } catch { }
    }
    Start-Process -FilePath $Request.TargetExe -WorkingDirectory ([IO.Path]::GetDirectoryName($Request.TargetExe)) -ArgumentList $arguments -WindowStyle Normal
}
function Invoke-AutoCQUpdate {
    param($Request,[scriptblock]$JtdxRunning={Test-UpdateJtdxRunning},[scriptblock]$ApplyPatch={param($patcher,$jtdx) Invoke-UpdatePatch $patcher $jtdx},[scriptblock]$AfterFile={param($index)},[string[]]$ProtectedPaths)
    if ($Request.Version -notmatch '^\d+\.\d+\.\d+$' -or $Request.ArchiveSha256 -notmatch '^[a-fA-F0-9]{64}$' -or $Request.JtdxSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid update request.' }
    $target=[IO.Path]::GetFullPath($Request.TargetExe)
    if (![IO.File]::Exists($target) -or [IO.Path]::GetExtension($target) -ne '.exe') { throw 'Installed AutoCQ executable was not found.' }
    if (& $JtdxRunning) { throw 'Close JTDX before updating AutoCQ.' }
    if ((Get-UpdateHash $Request.Archive) -ne $Request.ArchiveSha256) { throw 'Downloaded archive checksum no longer matches.' }
    $folder=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Request.Archive))
    $package=Get-UpdateChild $folder ('install-'+[guid]::NewGuid().ToString('N'))
    Expand-UpdateArchive $Request.Archive $package
    $manifest=Get-Content -LiteralPath (Join-Path $package 'update.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $module=Join-Path $package 'JTDX-AutoCQ.exe'
    $patcher=Join-Path $package 'JTDX-AutoCQ-Patch.exe'
    if ($manifest.version -ne $Request.Version -or $manifest.jtdxSha256 -ne $Request.JtdxSha256 -or
        [Diagnostics.FileVersionInfo]::GetVersionInfo($module).FileVersion -ne ($Request.Version+'.0') -or ![IO.File]::Exists($patcher)) { throw 'Package version verification failed.' }
    $appDirectory=[IO.Path]::GetDirectoryName($target)
    $backup=Get-UpdateChild $folder ('backup-'+[guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($backup)
    $protected=@()
    foreach ($path in $ProtectedPaths) { $protected+=@{Path=$path;Exists=[IO.File]::Exists($path);Hash=$(if([IO.File]::Exists($path)){Get-UpdateHash $path}else{''})} }
    $sources=New-Object 'Collections.Generic.List[object]'
    $sources.Add(@{Source=$module;Target=$target})
    foreach ($name in @('JTDX-AutoCQ.exe','JTDX-AutoCQ-Demo.exe')) {
        $sibling=Get-UpdateChild $appDirectory $name
        if ($sibling -ne $target -and [IO.File]::Exists($sibling)) { $sources.Add(@{Source=$module;Target=$sibling}) }
    }
    foreach ($file in Get-ChildItem -LiteralPath $package -File -Recurse) {
        $relative=$file.FullName.Substring($package.Length+1).Replace('\','/')
        if ($relative -eq 'JTDX-AutoCQ.exe') { continue }
        if ($relative -notmatch '^(JTDX-AutoCQ-Patch\.exe|README(?:\.en)?\.md|USER-GUIDE(?:\.en)?\.md|CONTRIBUTING\.md|RELEASE\.md|Patch-JTDX\.ps1|Update-AutoCQ\.ps1|update\.json|docs/[A-Za-z0-9_./-]+\.(png|html|css|md)|patch/[A-Za-z0-9_./-]+\.(cjs|asm|bin))$') { throw ('Unrecognized update file: '+$relative) }
        $sources.Add(@{Source=$file.FullName;Target=(Get-UpdateChild $appDirectory $relative)})
    }
    $instructions=Join-Path $appDirectory 'Инструкция.txt'
    if ([IO.File]::Exists($instructions)) { $sources.Add(@{Source=(Join-Path $package 'README.md');Target=$instructions}) }
    $native=$Request.JtdxExe
    $nativeBefore=if([IO.File]::Exists($native)){Get-UpdateHash $native}else{''}
    $nativeBackup=Join-Path $backup 'jtdx-before.exe'
    $applied=New-Object 'Collections.Generic.List[object]'
    $stages=New-Object 'Collections.Generic.List[string]'
    try {
        if (& $JtdxRunning) { throw 'JTDX was opened during update preparation.' }
        if ($nativeBefore -and $nativeBefore -ne $Request.JtdxSha256) {
            [IO.File]::Copy($native,$nativeBackup,$false)
            & $ApplyPatch $patcher $native
            if ((Get-UpdateHash $native) -ne $Request.JtdxSha256) { throw 'JTDX patch checksum mismatch.' }
        }
        $index=0
        foreach ($item in ($sources | Sort-Object @{Expression={if($_.Target -eq $target){1}else{0}}})) {
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($item.Target))
            $stage=$item.Target+'.autocq-update-'+[guid]::NewGuid().ToString('N')
            $stages.Add($stage)
            $item.Backup=Join-Path $backup ($index.ToString('D3')+'-'+[IO.Path]::GetFileName($item.Target))
            $item.Existed=[IO.File]::Exists($item.Target)
            $item.Before=if($item.Existed){Get-UpdateHash $item.Target}else{''}
            [IO.File]::Copy($item.Source,$stage,$false)
            if ((Get-UpdateHash $stage) -ne (Get-UpdateHash $item.Source)) { throw 'Staged file verification failed.' }
            if ($item.Existed) { [IO.File]::Replace($stage,$item.Target,$item.Backup) } else { [IO.File]::Move($stage,$item.Target) }
            $applied.Add($item)
            if ((Get-UpdateHash $item.Target) -ne (Get-UpdateHash $item.Source)) { throw 'Installed file verification failed.' }
            & $AfterFile $index
            $index++
        }
        foreach ($item in $protected) {
            if ([IO.File]::Exists($item.Path) -ne $item.Exists -or ($item.Exists -and (Get-UpdateHash $item.Path) -ne $item.Hash)) { throw 'An operator settings file changed during the update.' }
        }
        @{version=$Request.Version;success=$true;backup=$backup} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'result.json') -Encoding UTF8
        return $backup
    } catch {
        $failure=$_
        for($index=$applied.Count-1;$index -ge 0;$index--) {
            $item=$applied[$index]
            if ($item.Existed) {
                $restore=$item.Target+'.autocq-restore-'+[guid]::NewGuid().ToString('N')
                [IO.File]::Copy($item.Backup,$restore,$false);[IO.File]::Replace($restore,$item.Target,[NullString]::Value)
                if ((Get-UpdateHash $item.Target) -ne $item.Before) { throw 'Could not verify restored application file.' }
            } else { Remove-Item -LiteralPath $item.Target -Force }
        }
        if ([IO.File]::Exists($nativeBackup) -and (Get-UpdateHash $native) -ne $nativeBefore) {
            $restore=$native+'.autocq-restore-'+[guid]::NewGuid().ToString('N')
            [IO.File]::Copy($nativeBackup,$restore,$false);[IO.File]::Replace($restore,$native,[NullString]::Value)
            if ((Get-UpdateHash $native) -ne $nativeBefore) { throw 'Could not verify restored JTDX file.' }
        }
        throw $failure
    } finally { foreach ($stage in $stages) { if ([IO.File]::Exists($stage)) { Remove-Item -LiteralPath $stage -Force } } }
}

if ($LibraryOnly) { return }
try {
    $request=Get-Content -LiteralPath $RequestFile -Raw -Encoding UTF8 | ConvertFrom-Json
    Wait-UpdateParent $request
    $protected=@((Join-Path $env:LOCALAPPDATA 'JtdxAutoCQ\operator.xml'),(Join-Path $env:LOCALAPPDATA 'JtdxAutoCQ\preferences.xml'),(Join-Path $env:LOCALAPPDATA 'JTDX\JTDX.ini'))
    $null=Invoke-AutoCQUpdate -Request $request -ProtectedPaths $protected
    Start-UpdatedAutoCQ $request
} catch {
    $message=$_.Exception.Message
    if ($request -and [IO.File]::Exists($request.TargetExe)) { try { Start-UpdatedAutoCQ $request } catch {} }
    Add-Type -AssemblyName System.Windows.Forms
    [void][Windows.Forms.MessageBox]::Show("Не удалось установить обновление. Подробности ниже; резервная копия находится в папке загруженного обновления.`r`n`r`n"+$message,"Обновление AutoCQ")
    exit 1
}
