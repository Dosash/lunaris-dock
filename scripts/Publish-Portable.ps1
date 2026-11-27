[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildDir,
    [Parameter(Mandatory)][string]$ReleaseDir,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$PackageVersion
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path $PSScriptRoot
$assets = Join-Path $root 'packaging\portable'
$release = (Get-Item -LiteralPath $ReleaseDir).FullName
$folderName = 'LunarisDock-Portable'
$folder = Join-Path $release $folderName
$zipName = "LunarisDock-$PackageVersion-x64-Portable.zip"
$exeName = 'LunarisDock.exe'
$configName = 'LunarisDock.exe.config'
$launcher = 'Тестовый запуск.cmd'
$files = @($exeName, $configName, 'README.txt', 'LICENSE', $launcher)

# The folder can also contain a private profile, files from the user, or junctions.
# Only the names above are read from staging or replaced in the existing folder.
$stageLeaf = '.lunaris-package-' + [guid]::NewGuid().ToString('N')
$stage = Join-Path $release $stageLeaf
$backup = Join-Path $stage 'backup'
$sha = [Security.Cryptography.SHA256]::Create()
try { $lockId = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($release.ToUpperInvariant()))) }
finally { $sha.Dispose() }
$gate = New-Object Threading.Mutex($false, ('Global\LunarisDock.PortablePublish.' + $lockId))
$held = $false
$createdFolder = $false
$allRolledBack = $true
$guards = New-Object 'System.Collections.Generic.List[System.IO.FileStream]'
$done = New-Object System.Collections.ArrayList
try {
    try { $held = $gate.WaitOne(15000) } catch [Threading.AbandonedMutexException] { $held = $true }
    if (!$held) { throw 'Another portable publication is in progress in this release directory.' }
    New-Item -ItemType Directory -Path $stage, $backup | Out-Null
    Copy-Item -LiteralPath (Join-Path $BuildDir $exeName) -Destination (Join-Path $stage $exeName)
    Copy-Item -LiteralPath (Join-Path $assets $launcher), (Join-Path $assets 'README.txt') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $stage
    $config = New-Object Xml.XmlDocument
    $config.Load((Join-Path $BuildDir $configName))
    $section = $config.SelectSingleNode('/configuration/appSettings')
    if (!$section) {
        $section = $config.CreateElement('appSettings')
        [void]$config.DocumentElement.AppendChild($section)
    }
    $setting = $section.SelectSingleNode("add[@key='LunarisPortable']")
    if (!$setting) {
        $setting = $config.CreateElement('add')
        $setting.SetAttribute('key','LunarisPortable')
        [void]$section.AppendChild($setting)
    }
    $setting.SetAttribute('value','true')
    $config.Save((Join-Path $stage $configName))

    $archivePath = Join-Path $stage $zipName
    $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $files) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $stage $name),
                ($folderName + '/' + $name), [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($archivePath+'.sha256'), ($hash+'  '+$zipName+[Environment]::NewLine), [Text.Encoding]::ASCII)

    if (Test-Path -LiteralPath $folder) {
        $existing = Get-Item -LiteralPath $folder -Force
        if (!$existing.PSIsContainer -or ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Expected a plain directory for portable files: $folder"
        }
    } else {
        New-Item -ItemType Directory -Path $folder | Out-Null
        $createdFolder = $true
    }

    # Take writable handles before replacing anything. A running Windows executable
    # denies write access to its mapped image. Hold the old EXE handle until the end.
    $order = @($configName, 'README.txt', 'LICENSE', $launcher, $zipName, ($zipName+'.sha256'), $exeName)
    $writes = foreach ($name in $order) {
        $dir = if ($files -contains $name) { $folder } else { $release }
        $destination = Join-Path $dir $name
        $exists = Test-Path -LiteralPath $destination
        $stream = $null
        if ($exists) {
            $item = Get-Item -LiteralPath $destination -Force
            if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Expected a regular file: $destination"
            }
            try {
                $stream = [IO.File]::Open($destination, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite,
                    ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
                $guards.Add($stream)
            } catch {
                throw "Cannot update $destination. Close the portable Dock and other programs using the package, then retry. $($_.Exception.Message)"
            }
        }
        [pscustomobject]@{ File = $destination; New = (Join-Path $stage $name); Backup = (Join-Path $backup $name); Existed = $exists; Guard = $stream }
    }
    foreach ($write in $writes) {
        if ($write.Existed) { [IO.File]::Replace($write.New, $write.File, $write.Backup) }
        else { [IO.File]::Move($write.New, $write.File) }
        [void]$done.Add($write)
        # The open handle now points to the *backup*. Release it so a later
        # rollback can reopen that image as the replacement source.
        if ($write.Guard) { $write.Guard.Dispose() }
    }
} catch {
    $original = $_
    for ($i = $done.Count - 1; $i -ge 0; $i--) {
        $write = $done[$i]
        try {
            if ($write.Existed) { [IO.File]::Replace($write.Backup, $write.File, ($write.Backup + '.replaced')) }
            else { [IO.File]::Delete($write.File) }
        } catch {
            $allRolledBack = $false
            Write-Warning "Could not roll back $($write.File): $($_.Exception.Message). Original files retained in $backup."
        }
    }
    if ($createdFolder -and !(Get-ChildItem -LiteralPath $folder -Force | Select-Object -First 1)) {
        [IO.Directory]::Delete($folder, $false)
    }
    throw $original
} finally {
    foreach ($stream in $guards) { $stream.Dispose() }
    $expectedStage = [IO.Path]::Combine($release, $stageLeaf)
    try {
        if ($allRolledBack -and [IO.Path]::GetFullPath($stage) -eq $expectedStage -and
            (Split-Path -Parent $expectedStage) -eq $release -and (Test-Path -LiteralPath $expectedStage)) {
            Remove-Item -LiteralPath $expectedStage -Recurse -Force
        }
    } finally {
        if ($held) { $gate.ReleaseMutex() }
        $gate.Dispose()
    }
}
Write-Host "Built $folder and $(Join-Path $release $zipName)"
