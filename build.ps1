[CmdletBinding()]
param(
    [switch]$AppOnly,
    [switch]$PortableOnly,
    [switch]$InstallerOnly,
    [string]$Iscc,
    [string]$PackageVersion = '1.3.1.6'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root 'build'
$release = Join-Path (Join-Path $root 'release') $PackageVersion
$source = Join-Path $root 'src'
if ($PackageVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Expected a numeric package version.' }
if ((@($AppOnly, $PortableOnly, $InstallerOnly) | Where-Object { $_ }).Count -gt 1) {
    throw 'Choose only one of -AppOnly, -PortableOnly and -InstallerOnly.'
}
New-Item -ItemType Directory -Force -Path $build | Out-Null
# Keep compilation, config copying and publication in the same workspace operation.
$lockPath = Join-Path $build '.lunaris-build.lock'
try {
    $buildLock = [IO.FileStream]::new($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None, 4096, [IO.FileOptions]::DeleteOnClose)
} catch [IO.IOException] {
    throw "Cannot acquire the build lock at $lockPath. If a build is already running, wait for it to finish and retry. $($_.Exception.Message)"
}
try {
    $framework = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319'
    $compiler = Join-Path $framework 'csc.exe'
    $argsCsc = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/debug-', '/checked-',
        '/warn:4', '/codepage:65001', '/main:Lunaris.Program', "/out:$build\LunarisDock.exe",
        "/win32manifest:$source\app.manifest", '/utf8output')
    if (Test-Path "$source\LunarisDock.ico") { $argsCsc += "/win32icon:$source\LunarisDock.ico" }
    $argsCsc += "/resource:$source\UI\Preferences.xaml,Lunaris.Preferences.xaml"
    $argsCsc += "/resource:$source\UI\StatusMenuStyles.xaml,Lunaris.StatusMenuStyles.xaml"
    foreach ($code in 'en','ru','de','es','zh-CN') {
        $argsCsc += "/resource:$source\Languages\$code.txt,Lunaris.Languages.$code.txt"
    }
    foreach ($name in 'System','System.Core','System.Net.Http','System.Xaml','System.Xml','System.Drawing','System.Windows.Forms',
                      'System.Runtime.Serialization','Microsoft.CSharp','System.Management','System.Security','System.Configuration') {
        $argsCsc += "/reference:$framework\$name.dll"
    }
    foreach ($name in 'PresentationCore','PresentationFramework','WindowsBase','UIAutomationTypes','UIAutomationProvider','UIAutomationClient') {
        $argsCsc += "/reference:$framework\WPF\$name.dll"
    }
    $argsCsc += "/reference:$framework\System.Runtime.dll"
    foreach ($namespace in 'Windows.Devices','Windows.Foundation') {
        $argsCsc += "/reference:$env:windir\System32\WinMetadata\$namespace.winmd"
    }
    $sources = @(Get-ChildItem -Path $source -Recurse -File -Filter '*.cs' | Sort-Object FullName | ForEach-Object FullName)
    & $compiler @argsCsc @sources
    if ($LASTEXITCODE -ne 0) { throw "C# compiler failed ($LASTEXITCODE)." }
    if (Test-Path "$source\App.config") { Copy-Item "$source\App.config" "$build\LunarisDock.exe.config" -Force }
    if ($AppOnly) { return }
    New-Item -ItemType Directory -Force -Path $release | Out-Null
    if (!$InstallerOnly) {
        & "$root\scripts\Publish-Portable.ps1" -BuildDir $build -ReleaseDir $release -PackageVersion $PackageVersion
    }
    if ($PortableOnly) { return }
    if (-not $Iscc) { $Iscc = & "$root\scripts\Get-Inno.ps1" }
    $arguments = @('/Qp', "/DProductVersion=$PackageVersion", "/O$release", "$root\packaging\Lunaris.iss")
    & $Iscc @arguments
    if ($LASTEXITCODE -ne 0) { throw "Inno compiler failed ($LASTEXITCODE)." }
    $file = Get-Item "$release\LunarisDock-$PackageVersion-x64-Setup.exe"
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($file.FullName + '.sha256'), ($hash + '  ' + $file.Name + [Environment]::NewLine), [Text.Encoding]::ASCII)
    Write-Host "Built $($file.FullName)"
} finally {
    $buildLock.Dispose()
}
