[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$cache = Join-Path (Split-Path $PSScriptRoot) 'build\tools'
$dir = Join-Path $cache 'InnoSetup-6.5.3'
$iscc = Join-Path $dir 'ISCC.exe'
if (Test-Path -LiteralPath $iscc) { return $iscc }
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$package = Join-Path $cache 'innosetup-6.5.3.exe'
$uri = 'https://github.com/jrsoftware/issrc/releases/download/is-6_5_3/innosetup-6.5.3.exe'
# SHA-256 published by Microsoft's winget-pkgs manifest JRSoftware.InnoSetup/6.5.3.
$expected = '9345EE029FAA0B7AED0818C3D5B227699EF9A496CCE79E20C19EB9D6EF2E2C2D'
if (!(Test-Path -LiteralPath $package) -or (Get-FileHash $package).Hash -ne $expected) {
    (New-Object Net.WebClient).DownloadFile($uri, $package)
}
if ((Get-FileHash $package).Hash -ne $expected) { throw 'Inno Setup download failed SHA-256 verification.' }
$argsInno = @('/PORTABLE=1', '/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', ('/DIR="' + $dir + '"'))
$proc = Start-Process $package -ArgumentList $argsInno -Wait -PassThru -WindowStyle Hidden
if ($proc.ExitCode -ne 0 -or !(Test-Path -LiteralPath $iscc)) { throw 'Portable Inno Setup compiler could not be prepared.' }
$iscc
