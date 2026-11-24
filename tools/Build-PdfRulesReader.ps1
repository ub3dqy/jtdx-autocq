param(
    [Parameter(Mandatory = $true)] [string] $Directory,
    [Parameter(Mandatory = $true)] [string] $Compiler
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packages = @(
    @('pdfpig','0.1.16','net462','D67171846EA8C28F50359137065FEC4514266D7A32B23EAE6C5F2EBED8FFCFC4'),
    @('microsoft.bcl.hashcode','6.0.0','net462','F3B9B2BAB0BF8CC717D5FDF6D7AEE3EC54E36D9E85BD41347ACAE161319CBD6B'),
    @('system.memory','4.6.0','net462','3A10042B350CE9E11A1FDF4370668CCF6A452C6FEAFCDE0A556AAA881614385A'),
    @('system.valuetuple','4.5.0','net47','9E21FA9767D4E76BC0CEE065C1D40CC34384A114BFEC4D70E6C981168A926802'),
    @('system.buffers','4.6.0','net462','736425805075E8895F066B3960BB1308543F41E2A84BAA61D5AF6675192AFC97'),
    @('system.numerics.vectors','4.6.0','net462','7CA4B7B964361E647AF6F3610C7A8F2D834EB77AA98E25903976431EF444D475'),
    @('system.runtime.compilerservices.unsafe','6.1.0','net462','372AAAA51707BA6CD2C69B2044382E0F9486C1D50D1C16E8D0E39DCCB4C8CC25')
)
$cache = Join-Path $Directory 'pdf-packages'
$binaries = Join-Path $Directory 'pdf-reader'
[void][IO.Directory]::CreateDirectory($cache)
[void][IO.Directory]::CreateDirectory($binaries)
$files = foreach ($package in $packages) {
    $id, $version, $framework, $sha256 = $package
    $name = "$id.$version.nupkg"
    $download = Join-Path $cache $name
    if (!(Test-Path -LiteralPath $download -PathType Leaf)) {
        & curl.exe --fail --silent --show-error --max-time 120 "https://api.nuget.org/v3-flatcontainer/$id/$version/$name" --output "$download.part"
        if ($LASTEXITCODE -ne 0) { throw "Could not download $name." }
        if ((Get-FileHash -LiteralPath "$download.part").Hash -ne $sha256) { throw "The checksum of $name is incorrect." }
        Move-Item -LiteralPath "$download.part" -Destination $download -Force
    }
    if ((Get-FileHash -LiteralPath $download).Hash -ne $sha256) { throw "The checksum of $name is incorrect." }
    $zip = [IO.Compression.ZipFile]::OpenRead($download)
    try {
        foreach ($entry in $zip.Entries | Where-Object { $_.FullName -like "lib/$framework/*.dll" }) {
            $dll = Join-Path $binaries $entry.Name
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dll, $true)
            $dll
        }
    } finally { $zip.Dispose() }
}
$reader = Join-Path $binaries 'PdfRulesReader.exe'
$references = $files | ForEach-Object { "/r:$_" }
& $Compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 "/out:$reader" @references (Join-Path $PSScriptRoot 'PdfRulesReader.cs')
if ($LASTEXITCODE -ne 0) { throw 'PDF rules reader compilation failed.' }
$output = Join-Path $Directory 'PdfRulesReader.zip'
$temporary = "$output.part"
if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
$bundle = [IO.Compression.ZipFile]::Open($temporary, 'Create')
try {
    foreach ($file in @($files) + $reader) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($bundle, $file, [IO.Path]::GetFileName($file))
    }
} finally { $bundle.Dispose() }
Move-Item -LiteralPath $temporary -Destination $output -Force
$output
