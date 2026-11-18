[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $JtdxExe = 'C:\JTDX64\160-32A-rc10\bin\jtdx.exe',
    [switch] $Restore
)

$ErrorActionPreference = 'Stop'
$builtIn = Join-Path $PSScriptRoot 'JTDX-AutoCQ-Patch.exe'
if (Test-Path -LiteralPath $builtIn -PathType Leaf) {
    if ($PSCmdlet.ShouldProcess($JtdxExe, $(if ($Restore) { 'Restore original JTDX' } else { 'Prepare JTDX for AutoCQ' }))) {
        $arguments = @('--jtdx', $JtdxExe)
        if ($Restore) { $arguments += '--restore' }
        & $builtIn @arguments
        if ($LASTEXITCODE -ne 0) { throw 'JTDX preparation failed. See the message above.' }
    }
    return
}
$originalHash = '6B6718432EDB55B3E3C8A5B884522BDC5EF5E466BF074E9DAC09CC0B64CE399C'
$patchedHash = 'B615BFBDC23291A7CA297FE1EAE4EB7D2AF0E8966C962D4FB0EA620C6FF6B12F'
$previousHashes = @(
    '0ABBA5EA8429CE4158DF0D2822D9A3108DD629A7C17E8332A8D40736ED6A413A',
    'EB846375E60FB972467246B8A3C1061A8B2B32984BBF838EAA47571F9814C5A9'
)

function Get-Sha256([string] $Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::OpenRead($Path)
        try { [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $stream.Dispose() }
    } finally { $sha.Dispose() }
}

function Assert-JtdxClosed {
    if (Get-Process -Name jtdx -ErrorAction SilentlyContinue) {
        throw 'Close JTDX before changing jtdx.exe.'
    }
}

$target = Get-Item -LiteralPath $JtdxExe
if ($target.PSIsContainer -or $target.Name -ne 'jtdx.exe') {
    throw 'Specify an installed jtdx.exe file with -JtdxExe.'
}
$path = $target.FullName
$backup = $path + '.autocq-original'
$currentHash = Get-Sha256 $path

if ($currentHash -notin (@($originalHash, $patchedHash) + $previousHashes)) {
    throw "Unsupported jtdx.exe. Expected JTDX 2.2.160-32A-rc10 Windows x64. SHA-256: $currentHash"
}
if ((Test-Path -LiteralPath $backup) -and (Get-Sha256 $backup) -ne $originalHash) {
    throw "The existing backup has a different SHA-256: $backup"
}
$source = $path
if ($currentHash -in $previousHashes) {
    if (!(Test-Path -LiteralPath $backup -PathType Leaf)) {
        throw "The previous patch needs the verified original rc10 to upgrade. Restore that executable first or place it here: $backup"
    }
    $source = $backup
}

if ($Restore) {
    if ($currentHash -eq $originalHash) {
        Write-Output 'The original JTDX executable is already in use.'
        return
    }
    if (!(Test-Path -LiteralPath $backup -PathType Leaf)) {
        throw "Install the original rc10 again or restore jtdx.exe from a verified backup. Missing: $backup"
    }
    $action = 'Restore the original JTDX executable'
    $resultHash = $originalHash
} else {
    if ($currentHash -eq $patchedHash) {
        Write-Output 'The verified AutoCQ patch is already installed.'
        return
    }
    $action = 'Apply the AutoCQ patch and keep the original executable'
    $resultHash = $patchedHash
}

if (-not $PSCmdlet.ShouldProcess($path, $action)) { return }
Assert-JtdxClosed
$temporary = Join-Path $target.DirectoryName ([Guid]::NewGuid().ToString('N') + '.autocq.tmp')
try {
    if ($Restore) {
        [IO.File]::Copy($backup, $temporary)
    } else {
        $node = Get-Command node.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (!$node) { throw 'Install Node.js LTS from https://nodejs.org/en/download and reopen PowerShell.' }
        & $node.Source (Join-Path $PSScriptRoot 'patch/build-native.cjs') $source (Join-Path $PSScriptRoot 'patch/queue-reply.bin') $temporary
        if ($LASTEXITCODE -ne 0) { throw 'The patch builder could not create the verified JTDX executable.' }
    }
    if ((Get-Sha256 $temporary) -ne $resultHash) { throw 'The prepared executable failed SHA-256 verification.' }
    Assert-JtdxClosed
    if ((Get-Sha256 $path) -ne $currentHash) { throw 'jtdx.exe changed during preparation. Try again after checking the installation.' }
    if (!$Restore -and !(Test-Path -LiteralPath $backup)) {
        [IO.File]::Copy($path, $backup, $false)
    }
    if ((Get-Sha256 $backup) -ne $originalHash) { throw "The original backup failed verification: $backup" }
    [IO.File]::Replace($temporary, $path, [NullString]::Value)
    if ((Get-Sha256 $path) -ne $resultHash) { throw "The installed file changed. Verified original backup: $backup" }
    Write-Output "Verified executable: $path"
    Write-Output "Original backup: $backup"
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
