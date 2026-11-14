[CmdletBinding()]
param(
    [switch] $Test,
    [string] $JtdxBin
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The 64-bit .NET Framework 4 C# compiler was not found.'
}

$source = Join-Path $PSScriptRoot 'src'
$tests = Join-Path $PSScriptRoot 'tests'
$build = Join-Path $PSScriptRoot 'build'
$icon = Join-Path $source 'AutoCQ.ico'
$program = Join-Path $build 'JTDX-AutoCQ.exe'
[void] [IO.Directory]::CreateDirectory($build)
$files = @(Get-ChildItem -LiteralPath $source -Filter '*.cs' -File | Sort-Object Name | Select-Object -ExpandProperty FullName)
$references = @('/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Core.dll')

& $compiler /nologo /target:winexe /optimize+ /codepage:65001 "/out:$program" "/win32icon:$icon" "/resource:$icon,AutoCQ.ico" @references @files
if ($LASTEXITCODE -ne 0) { throw 'AutoCQ compilation failed.' }
Write-Output $program

if ($Test) {
    $managed = Join-Path $build 'AutoCqTests.exe'
    & $compiler /nologo /target:exe /optimize+ /codepage:65001 /main:AutoCqTests "/out:$managed" "/resource:$icon,AutoCQ.ico" @references @files (Join-Path $tests 'AutoCqTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Managed test compilation failed.' }
    Push-Location -LiteralPath $build
    try {
        & $managed (Join-Path $build 'managed-tests.txt')
        if ($LASTEXITCODE -ne 0) { throw 'Managed tests failed.' }
    } finally { Pop-Location }

    if ($JtdxBin) {
        $JtdxBin = (Resolve-Path -LiteralPath $JtdxBin).Path
        $native = Join-Path $build 'TestNativeQueue.exe'
        & $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 "/out:$native" "/r:$program" (Join-Path $tests 'TestNativeQueue.cs')
        if ($LASTEXITCODE -ne 0) { throw 'Native test compilation failed.' }
        & $native (Join-Path $PSScriptRoot 'patch/queue-reply.bin') $JtdxBin (Join-Path $JtdxBin 'jtdx.exe')
        if ($LASTEXITCODE -ne 0) { throw 'Native tests failed.' }
    }
}
