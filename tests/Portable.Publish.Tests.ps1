[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sourceRoot = Split-Path $PSScriptRoot
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$leaf = 'Lunaris-Publish-' + [guid]::NewGuid().ToString('N')
$sandbox = [IO.Path]::Combine($tempRoot, $leaf)
$release = Join-Path $sandbox 'release\1.3.1.6'
$folder = Join-Path $release 'LunarisDock-Portable'
$zipName = 'LunarisDock-1.3.1.6-x64-Portable.zip'
$zip = Join-Path $release $zipName
$cmd = 'Тестовый запуск.cmd'
$names = @('LunarisDock.exe', 'LunarisDock.exe.config', 'README.txt', 'LICENSE', 'Lucide-LICENSE.txt', $cmd)

function Assert-Package {
    $rootNames = @(Get-ChildItem -LiteralPath $release -Force | ForEach-Object Name | Sort-Object)
    $expectedRoot = @('LunarisDock-Portable', $zipName, ($zipName + '.sha256')) | Sort-Object
    if (Compare-Object $expectedRoot $rootNames -CaseSensitive) { throw 'Unexpected files in release.' }
    $digest = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
    $sidecar = [IO.File]::ReadAllText($zip + '.sha256', [Text.Encoding]::ASCII)
    if ($sidecar -cne ($digest + '  ' + $zipName + [Environment]::NewLine)) { throw 'Incorrect ZIP checksum sidecar.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entries = @($archive.Entries | ForEach-Object FullName | Sort-Object)
        $expected = @($names | ForEach-Object { 'LunarisDock-Portable/' + $_ } | Sort-Object)
        if (Compare-Object $expected $entries -CaseSensitive) { throw 'ZIP allowlist mismatch.' }
        foreach ($name in $names) {
            $stream = $archive.GetEntry('LunarisDock-Portable/' + $name).Open()
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try { $archivedHash = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
            finally { $algorithm.Dispose(); $stream.Dispose() }
            if ($archivedHash -cne (Get-FileHash -LiteralPath (Join-Path $folder $name)).Hash) {
                throw "ZIP and loose copy differ for $name"
            }
        }
    } finally { $archive.Dispose() }
    $config = [xml][IO.File]::ReadAllText((Join-Path $folder 'LunarisDock.exe.config'))
    if ('true' -cne $config.SelectSingleNode('/configuration/appSettings/add[@key="LunarisPortable"]').GetAttribute('value')) {
        throw 'Portable mode missing in published config.'
    }
}

function Read-ReleaseHashes {
    Get-ChildItem -LiteralPath $release -File -Force -Recurse | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($release.Length) + ' ' + (Get-FileHash -LiteralPath $_.FullName).Hash
    }
}

try {
    $scripts = Join-Path $sandbox 'scripts'
    $assets = Join-Path $sandbox 'packaging\portable'
    New-Item -ItemType Directory -Path $sandbox, $scripts, $assets | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'src') -Destination $sandbox -Recurse
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'build.ps1') -Destination $sandbox
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'LICENSE') -Destination $sandbox
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'packaging\Lucide-LICENSE.txt') -Destination (Join-Path $sandbox 'packaging')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'scripts\Publish-Portable.ps1') -Destination $scripts
    foreach ($asset in @('README.txt', $cmd)) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot ('packaging\portable\' + $asset)) -Destination $assets
    }
    $build = Join-Path $sandbox 'build.ps1'
    & $build -PortableOnly
    Assert-Package
    Write-Host 'PASS clean portable-only build, ZIP allowlist, file hashes and sidecar'

    $profile = Join-Path $folder 'Data\Profile'
    New-Item -ItemType Directory -Path $profile | Out-Null
    $settings = Join-Path $profile 'settings.json'
    $personal = Join-Path $folder 'personal.txt'
    $pin = '{"Pins":["publish-test"],"Size":72}'
    $note = 'PUBLISH-TEST-' + [guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText($settings, $pin)
    [IO.File]::WriteAllText($personal, $note)
    [IO.File]::AppendAllText((Join-Path $assets 'README.txt'), [Environment]::NewLine + 'test revision two')
    & $build -PortableOnly
    Assert-Package
    if ([IO.File]::ReadAllText($settings) -cne $pin -or [IO.File]::ReadAllText($personal) -cne $note) {
        throw 'Republishing changed a private file.'
    }
    if ((Get-FileHash -LiteralPath (Join-Path $folder 'README.txt')).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $assets 'README.txt')).Hash) { throw 'Second publication did not update.' }
    Write-Host 'PASS republish updates package and preserves private profile and unrelated file'

    $before = @(Read-ReleaseHashes)
    [IO.File]::AppendAllText((Join-Path $assets 'README.txt'), [Environment]::NewLine + 'test revision three')
    $imagePath = Join-Path $folder 'LunarisDock.exe'
    $image = [IO.File]::Open($imagePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $refused = $false
        try { & $build -PortableOnly }
        catch {
            if (!$_.Exception.Message.Contains($imagePath)) { throw }
            $refused = $true
        }
        if (!$refused) { throw 'Publisher overwrote a locked running image.' }
    } finally { $image.Dispose() }
    if (Compare-Object $before @(Read-ReleaseHashes) -CaseSensitive) { throw 'Locked publication changed committed files.' }
    Assert-Package
    Write-Host 'PASS sharing violation preserves all existing published files and checksums'

    # A reader that grants WRITE but denies DELETE allows preflight and blocks
    # File.Replace on the ZIP after the loose files have already been replaced.
    $archiveReader = [IO.File]::Open($zip, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $refused = $false
        try { & $build -PortableOnly } catch { $refused = $true }
        if (!$refused) { throw 'Publisher replaced a ZIP without delete sharing.' }
    } finally { $archiveReader.Dispose() }
    if (Compare-Object $before @(Read-ReleaseHashes) -CaseSensitive) { throw 'Mid-commit rollback changed files.' }
    Assert-Package
    Write-Host 'PASS mid-commit ZIP replacement failure rolls all loose files back to their original bytes'
} finally {
    if (Test-Path -LiteralPath $sandbox) {
        $resolved = (Resolve-Path -LiteralPath $sandbox).ProviderPath
        if ($resolved -ne $sandbox -or [IO.Path]::GetDirectoryName($resolved) -ne $tempRoot -or
            [IO.Path]::GetFileName($resolved) -ne $leaf) { throw 'Unexpected private test directory.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
