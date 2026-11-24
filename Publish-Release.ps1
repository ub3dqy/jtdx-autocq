[CmdletBinding()]
param(
    [string] $JtdxBin,
    [string] $GitHubCli = 'gh',
    [switch] $PackageOnly
)

$ErrorActionPreference = 'Stop'
$repository = 'ub3dqy/jtdx-autocq'
$notes = Join-Path $PSScriptRoot 'RELEASE.md'
$versionMatch = [regex]::Match((Get-Content -LiteralPath $notes -Raw -Encoding UTF8), '\A# AutoCQ (\d+\.\d+\.\d+)[ \t]*\r?\n')
if (!$versionMatch.Success) { throw 'Start RELEASE.md with the heading # AutoCQ X.Y.Z.' }
$version = $versionMatch.Groups[1].Value
$tag = 'v' + $version
$name = "JTDX-AutoCQ-$version-win64.zip"

function Invoke-ReleaseGit {
    param([string[]] $Arguments)
    & git -C $PSScriptRoot @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Git failed: $($Arguments[0])" }
}

function Invoke-ReleaseGh {
    param([string[]] $Arguments)
    & $gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub CLI failed: $($Arguments[0])" }
}

if (@(Invoke-ReleaseGit @('status', '--porcelain')).Count) {
    throw 'Commit the source and release notes before building or publishing a release.'
}
$head = (Invoke-ReleaseGit @('rev-parse', 'HEAD')).Trim()
$release = $null

if (!$PackageOnly) {
    $gh = (Get-Command $GitHubCli -CommandType Application).Source
    $publicRepo = Invoke-ReleaseGh @('api', "repos/$repository", '--hostname', 'github.com', '--jq', '{full_name,visibility,permissions}') | ConvertFrom-Json
    if ($publicRepo.full_name -ne $repository -or $publicRepo.visibility -ne 'public' -or !$publicRepo.permissions.push) {
        throw 'Sign in to GitHub CLI with access to publish in ub3dqy/jtdx-autocq.'
    }
    if ((Invoke-ReleaseGit @('branch', '--show-current')) -ne 'main') {
        throw 'Publish the committed update from main.'
    }
    $origin = (Invoke-ReleaseGit @('remote', 'get-url', '--push', 'origin')).Trim()
    if ($origin -notin "https://github.com/$repository.git", "https://github.com/$repository",
        "git@github.com:$repository.git", "ssh://git@github.com/$repository.git") {
        throw 'Set the origin remote to ub3dqy/jtdx-autocq.'
    }
    # Windows PowerShell 5.1 turns redirected stderr into an error record.
    # For these two lookups, inspect the exit code and the HTTP response.
    $published, $publishedExit, $remoteTag, $remoteTagExit = & {
        $ErrorActionPreference = 'Continue'
        $details = & $gh release view $tag --repo $repository --json url,assets,isDraft,targetCommitish 2>&1
        $detailsExit = $LASTEXITCODE
        $commit = & $gh api "repos/$repository/commits/$tag" --jq '.sha' 2>&1
        $commitExit = $LASTEXITCODE
        ,@($details)
        $detailsExit
        ,@($commit)
        $commitExit
    }
    if ($publishedExit -eq 0) {
        $release = $published | ConvertFrom-Json
        if (!$release.isDraft) {
            if ($remoteTagExit -ne 0 -or ($remoteTag -join '').Trim() -ne $head) {
                throw "The release $tag is already published. Use a new version for the next program update."
            }
            if (@($release.assets.name | Where-Object { $_ -in $name, 'SHA256SUMS.txt' }).Count -ne 2) {
                throw "The published release is missing an asset: $($release.url)"
            }
            Write-Output $release.url
            return
        }
        if ($release.targetCommitish -ne $head) {
            throw "The draft $tag targets a different commit: $($release.url)"
        }
    } elseif (($published | Out-String) -notmatch 'release not found|HTTP 404') {
        throw ($published | Out-String)
    }
    if ($remoteTagExit -eq 0) {
        if (($remoteTag -join '').Trim() -ne $head) { throw "Tag $tag already points to a different commit." }
    } elseif (($remoteTag | Out-String) -notmatch 'HTTP (404|422)') {
        throw ($remoteTag | Out-String)
    }
}

& (Join-Path $PSScriptRoot 'build.ps1') -Test -JtdxBin $JtdxBin
$program = Join-Path $PSScriptRoot 'build/JTDX-AutoCQ.exe'
if ((Get-Item -LiteralPath $program).VersionInfo.FileVersion -ne "$version.0") {
    throw 'The installed file version in src/AutoCqApp.cs must match RELEASE.md.'
}
if ((Invoke-ReleaseGit @('rev-parse', 'HEAD')).Trim() -ne $head -or
    @(Invoke-ReleaseGit @('status', '--porcelain')).Count) {
    throw 'The source changed while building the release.'
}

$package = Join-Path $PSScriptRoot "build/release-$version"
[void][IO.Directory]::CreateDirectory($package)
$archivePath = Join-Path $package $name
$manifestPath = Join-Path $package 'SHA256SUMS.txt'
$tempPath = Join-Path $package ([Guid]::NewGuid().ToString('N') + '.zip')
$entries = [ordered]@{
    'JTDX-AutoCQ.exe' = 'build/JTDX-AutoCQ.exe'
    'JTDX-AutoCQ-Patch.exe' = 'build/JTDX-AutoCQ-Patch.exe'
    'Update-AutoCQ.ps1' = 'Update-AutoCQ.ps1'
    'update.json' = 'build/update.json'
    'README.md' = 'README.md'
    'README.en.md' = 'README.en.md'
    'RELEASE.md' = 'RELEASE.md'
    'USER-GUIDE.md' = 'USER-GUIDE.md'
    'USER-GUIDE.en.md' = 'USER-GUIDE.en.md'
    'CONTRIBUTING.md' = 'CONTRIBUTING.md'
    'Patch-JTDX.ps1' = 'Patch-JTDX.ps1'
    'docs/autocq-demo.png' = 'docs/autocq-demo.png'
    'docs/frequency-stop-demo.png' = 'docs/frequency-stop-demo.png'
    'docs/signoff-retry-demo.png' = 'docs/signoff-retry-demo.png'
    'docs/setup-demo.png' = 'docs/setup-demo.png'
    'docs/updates-demo.png' = 'docs/updates-demo.png'
    'docs/monitor-demo.png' = 'docs/monitor-demo.png'
    'docs/monitor-timeouts-demo.png' = 'docs/monitor-timeouts-demo.png'
    'docs/monitor-manual-demo.png' = 'docs/monitor-manual-demo.png'
    'docs/monitor-callers-demo.png' = 'docs/monitor-callers-demo.png'
    'docs/about-demo.png' = 'docs/about-demo.png'
    'docs/updates-download-demo.png' = 'docs/updates-download-demo.png'
    'docs/bug-report-demo.png' = 'docs/bug-report-demo.png'
    'docs/logbook-source-demo.png' = 'docs/logbook-source-demo.png'
    'docs/profiles-demo.png' = 'docs/profiles-demo.png'
    'docs/dxcc-targets-demo.png' = 'docs/dxcc-targets-demo.png'
    'docs/station-reason-demo.png' = 'docs/station-reason-demo.png'
    'docs/session-summary-demo.png' = 'docs/session-summary-demo.png'
    'docs/awards-demo.png' = 'docs/awards-demo.png'
    'docs/award-goals-demo.png' = 'docs/award-goals-demo.png'
    'docs/award-points-journal-demo.png' = 'docs/award-points-journal-demo.png'
    'docs/hamlog-account-demo.png' = 'docs/hamlog-account-demo.png'
    'docs/hamlog-plan-demo.png' = 'docs/hamlog-plan-demo.png'
    'docs/hamlog-additional-demo.png' = 'docs/hamlog-additional-demo.png'
    'docs/award-catalog.md' = 'docs/award-catalog.md'
    'docs/pdfpig-license.md' = 'docs/pdfpig-license.md'
    'docs/dotnet-license.md' = 'docs/dotnet-license.md'
    'docs/user-guide.ru.html' = 'docs/user-guide.ru.html'
    'docs/user-guide.en.html' = 'docs/user-guide.en.html'
    'docs/guide.css' = 'docs/guide.css'
    'docs/icon.png' = 'docs/icon.png'
    'patch/build-native.cjs' = 'patch/build-native.cjs'
    'patch/queue-reply.asm' = 'patch/queue-reply.asm'
    'patch/queue-reply.bin' = 'patch/queue-reply.bin'
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    $zip = [IO.Compression.ZipFile]::Open($tempPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $entries.GetEnumerator()) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, (Join-Path $PSScriptRoot $entry.Value), $entry.Key, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $zip.Dispose() }
    Move-Item -LiteralPath $tempPath -Destination $archivePath -Force
} finally {
    if (Test-Path -LiteralPath $tempPath) { Remove-Item -LiteralPath $tempPath }
}
$digest = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($manifestPath, "$digest  $name" + [Environment]::NewLine)
$packageTest = Join-Path $PSScriptRoot 'build/TestPackage.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$packageTest" "/r:$program" /r:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'tests/TestPackage.cs')
if ($LASTEXITCODE -ne 0) { throw 'Package verifier compilation failed.' }
& $packageTest $archivePath
if ($LASTEXITCODE -ne 0) { throw 'Final archive verification failed.' }
Write-Output $archivePath
if ($PackageOnly) { return }

$helper = '!"' + $gh.Replace('\', '/') + '" auth git-credential'
Invoke-ReleaseGit @('-c', 'credential.helper=', '-c', "credential.helper=$helper", 'push', 'origin', 'HEAD:refs/heads/main')
if ($release -and $release.isDraft) {
    $draft = Invoke-ReleaseGh @('release', 'view', $tag, '--repo', $repository, '--json', 'isDraft,targetCommitish') | ConvertFrom-Json
    if (!$draft.isDraft -or $draft.targetCommitish -ne $head) { throw "Draft $tag changed while building." }
    Invoke-ReleaseGh @('release', 'upload', $tag, $archivePath, $manifestPath, '--repo', $repository, '--clobber')
} else {
    Invoke-ReleaseGh @('release', 'create', $tag, $archivePath, $manifestPath, '--repo', $repository,
        '--target', $head, '--title', "AutoCQ $version", '--notes-file', $notes, '--draft')
}

$actual = Invoke-ReleaseGh @('release', 'view', $tag, '--repo', $repository, '--json', 'url,assets,isDraft,targetCommitish') | ConvertFrom-Json
$asset = $actual.assets | Where-Object Name -eq $name
$manifest = $actual.assets | Where-Object Name -eq 'SHA256SUMS.txt'
$manifestDigest = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
if (!$actual.isDraft -or $actual.targetCommitish -ne $head -or
    $asset.digest -ne "sha256:$digest" -or $manifest.digest -ne "sha256:$manifestDigest") {
    throw "Verify the draft and its assets: $($actual.url)"
}
Invoke-ReleaseGh @('release', 'edit', $tag, '--repo', $repository, '--draft=false', '--latest')
$publishedCommit = (Invoke-ReleaseGh @('api', "repos/$repository/commits/$tag", '--jq', '.sha')).Trim()
$latest = Invoke-ReleaseGh @('api', "repos/$repository/releases/latest", '--jq', '.tag_name')
if ($publishedCommit -ne $head -or $latest -ne $tag) { throw "Verify the published release: $($actual.url)" }
