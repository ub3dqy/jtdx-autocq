[CmdletBinding()]
param(
    [switch] $Test,
    [string] $JtdxBin,
    [switch] $Screenshots
)

$ErrorActionPreference = 'Stop'
if ($JtdxBin) { $JtdxBin = (Resolve-Path -LiteralPath $JtdxBin).Path }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The 64-bit .NET Framework 4 C# compiler was not found.'
}

$source = Join-Path $PSScriptRoot 'src'
$tests = Join-Path $PSScriptRoot 'tests'
$build = Join-Path $PSScriptRoot 'build'
$icon = Join-Path $source 'AutoCQ.ico'
$stub = Join-Path $PSScriptRoot 'patch/queue-reply.bin'
$program = Join-Path $build 'JTDX-AutoCQ.exe'
[void] [IO.Directory]::CreateDirectory($build)
$files = @(Get-ChildItem -LiteralPath $source -Filter '*.cs' -File | Sort-Object Name | Select-Object -ExpandProperty FullName)
$references = @('/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Core.dll',
    '/r:System.Runtime.Serialization.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$helper = Join-Path $PSScriptRoot 'Update-AutoCQ.ps1'
$patcher = Join-Path $build 'JTDX-AutoCQ-Patch.exe'
$patchVersion = Join-Path $build 'PatchVersionInfo.cs'
$appVersion = [regex]::Match((Get-Content -LiteralPath (Join-Path $source 'AutoCqApp.cs') -Raw), 'AssemblyFileVersion\("([0-9.]+)"\)').Groups[1].Value
$versionSource = '[assembly: System.Reflection.AssemblyVersion("' + $appVersion + '")]' + [Environment]::NewLine + '[assembly: System.Reflection.AssemblyFileVersion("' + $appVersion + '")]'
$versionSource += [Environment]::NewLine + '[assembly: System.Reflection.AssemblyTitle(JtdxAutoCq.AppBrand.DisplayName + " — JTDX preparation")]' + [Environment]::NewLine + '[assembly: System.Reflection.AssemblyProduct(JtdxAutoCq.AppBrand.DisplayName)]' + [Environment]::NewLine + '[assembly: System.Reflection.AssemblyCompany(JtdxAutoCq.AppBrand.Author)]' + [Environment]::NewLine + '[assembly: System.Reflection.AssemblyCopyright(JtdxAutoCq.AppBrand.Copyright)]'
[IO.File]::WriteAllText($patchVersion,$versionSource,(New-Object Text.UTF8Encoding($false)))
& $compiler /nologo /target:exe /platform:x64 /main:JtdxAutoCq.PatchProgram /optimize+ /codepage:65001 "/out:$patcher" "/resource:$stub,queue-reply.bin" /r:System.Core.dll (Join-Path $source 'JtdxPatcher.cs') (Join-Path $source 'AppBrand.cs') $patchVersion
if ($LASTEXITCODE -ne 0) { throw 'Built-in JTDX patcher compilation failed.' }
$resources = @("/resource:$icon,AutoCQ.ico", "/resource:$stub,queue-reply.bin", "/resource:$helper,Update-AutoCQ.ps1", "/resource:$patcher,JTDX-AutoCQ-Patch.exe")

& $compiler /nologo /target:winexe /main:JtdxAutoCq.Program /optimize+ /codepage:65001 "/out:$program" "/win32icon:$icon" @resources @references @files
if ($LASTEXITCODE -ne 0) { throw 'AutoCQ compilation failed.' }
Write-Output $program
$nativeHash = [regex]::Match((Get-Content -LiteralPath (Join-Path $source 'JtdxPatcher.cs') -Raw), 'PatchedHash\s*=\s*"([A-F0-9]{64})"').Groups[1].Value
if (!$nativeHash) { throw 'The verified JTDX patch checksum was not found.' }
@{version=([version]$appVersion).ToString(3);jtdxSha256=$nativeHash} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $build 'update.json') -Encoding UTF8

if ($Screenshots) {
    $renderer = Join-Path $build 'RenderScreenshots.exe'
    & $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$renderer" "/r:$program" /r:System.Drawing.dll /r:System.Windows.Forms.dll (Join-Path $tests 'RenderScreenshots.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Screenshot renderer compilation failed.' }
    & $renderer (Join-Path $PSScriptRoot 'docs')
    if ($LASTEXITCODE -ne 0) { throw 'Demo screenshot rendering failed.' }
}

if ($Test) {
    $managed = Join-Path $build 'AutoCqTests.exe'
    & $compiler /nologo /target:exe /optimize+ /codepage:65001 /main:AutoCqTests "/out:$managed" @resources @references @files (Join-Path $tests 'AutoCqTests.cs') (Join-Path $tests 'UpdateTests.cs') (Join-Path $tests 'BugReportTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Managed test compilation failed.' }
    Push-Location -LiteralPath $build
    try {
        $original = if ($JtdxBin) { Join-Path $JtdxBin 'jtdx.exe.autocq-original' } else { '' }
        & $managed (Join-Path $build 'managed-tests.txt') $original
        if ($LASTEXITCODE -ne 0) { throw 'Managed tests failed.' }
    } finally { Pop-Location }
    $windowsPowerShell = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
    & $windowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File (Join-Path $tests 'TestUpdater.ps1') -BuildDirectory $build -HelperPath $helper
    if ($LASTEXITCODE -ne 0) { throw 'Update helper tests failed.' }

    if ($JtdxBin) {
        $JtdxBin = (Resolve-Path -LiteralPath $JtdxBin).Path
        $native = Join-Path $build 'TestNativeQueue.exe'
        & $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 "/out:$native" "/r:$program" (Join-Path $tests 'TestNativeQueue.cs')
        if ($LASTEXITCODE -ne 0) { throw 'Native test compilation failed.' }
        & $native (Join-Path $PSScriptRoot 'patch/queue-reply.bin') $JtdxBin (Join-Path $JtdxBin 'jtdx.exe')
        if ($LASTEXITCODE -ne 0) { throw 'Native tests failed.' }
    }
}
