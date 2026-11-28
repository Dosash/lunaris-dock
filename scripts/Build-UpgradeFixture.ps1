[CmdletBinding()]
param([string]$Version = '0.9.0')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$' -or [version]$Version -ge [version]'1.0.0') {
    throw 'The upgrade fixture must use a numeric version below 1.0.0.'
}
$root = Split-Path $PSScriptRoot
$fixture = Join-Path $root "build\upgrade-fixture\$Version"
$src = Join-Path $fixture 'src'
$package = Join-Path $fixture 'packaging'
New-Item -ItemType Directory -Path $src,$package -Force | Out-Null
Copy-Item (Join-Path $root 'src\*') $src -Recurse -Force
Copy-Item (Join-Path $root 'packaging\Lunaris.iss') $package -Force
Copy-Item (Join-Path $root 'packaging\languages') $package -Recurse -Force
Copy-Item (Join-Path $root 'build.ps1') $fixture -Force
Copy-Item (Join-Path $root 'LICENSE') $fixture -Force
$meta = Join-Path $src 'AssemblyInfo.cs'
$text = [regex]::Replace([IO.File]::ReadAllText($meta), '(Assembly(?:File)?Version\(")[^"]+("\))', {
    param($m)
    $m.Groups[1].Value + $Version + '.0' + $m.Groups[2].Value
})
[IO.File]::WriteAllText($meta, $text, (New-Object Text.UTF8Encoding($false)))
$compiler = & (Join-Path $root 'scripts\Get-Inno.ps1')
& (Join-Path $fixture 'build.ps1') -InstallerOnly -Iscc $compiler -PackageVersion $Version
Write-Host "Upgrade test fixture (same product source, earlier assembly and installer versions): $fixture\release\$Version"
