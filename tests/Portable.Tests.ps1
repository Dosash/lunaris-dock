[CmdletBinding()]
param(
    [string]$ZipPath,
    [string]$OutDir,
    [string]$LaunchName = 'Таблица символов',
    [switch]$PackageOnly,
    [switch]$PrivateDesktop,
    [switch]$StoreOnly
)
$ErrorActionPreference = 'Stop'
if (!$ZipPath) { $ZipPath = Join-Path $PSScriptRoot '..\release\1.3.1.9\LunarisDock-1.3.1.9-x64-Portable.zip' }
if (!$OutDir) { $OutDir = Join-Path $PSScriptRoot '..\build\test-results\1.3.1.9\portable' }
$zipFile = Get-Item -LiteralPath $ZipPath
$log = [IO.Path]::GetFullPath($OutDir)
[void][IO.Directory]::CreateDirectory($log)
$temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$leaf = 'Lunaris-Portable-' + [guid]::NewGuid().ToString('N')
$work = Join-Path $temporary $leaf
$original = Join-Path $work 'Исходная папка\LunarisDock-Portable'
$moved = Join-Path $work 'Перенесённая копия'
$foreign = Join-Path $work 'Посторонний каталог'
$proof = Join-Path $work 'proof.txt'
$mode = if ($PackageOnly) { 'package' } elseif ($StoreOnly) { 'store' } else { 'full' }
$success = $false

function Assert-WorkPath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetDirectoryName($work) -ne $temporary -or [IO.Path]::GetFileName($work) -ne $leaf -or
        ($absolute -ne $work -and !$absolute.StartsWith($work+'\', [StringComparison]::OrdinalIgnoreCase))) {
        throw "Недопустимый временный путь: $absolute"
    }
    if (Test-Path -LiteralPath $absolute) {
        $resolved = (Resolve-Path -LiteralPath $absolute).ProviderPath
        if ($resolved -ne $absolute -or ((Get-Item -LiteralPath $absolute -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Вместо временной папки найдено перенаправление: $absolute"
        }
    }
}

function Expand-VerifiedPackage {
    Add-Type -AssemblyName System.IO.Compression
    $expected = @('LunarisDock.exe', 'LunarisDock.exe.config', 'README.txt', 'LICENSE', 'Lucide-LICENSE.txt', 'Тестовый запуск.cmd')
    $stream = [IO.File]::OpenRead($zipFile.FullName)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
        finally { $sha.Dispose() }
        $record = [IO.File]::ReadAllText($zipFile.FullName + '.sha256').TrimEnd([char[]]"`r`n")
        if ($record -cne ($hash + '  ' + $zipFile.Name)) { throw 'Контрольная сумма ZIP или имя в sidecar не совпали.' }
        Write-Output "PASS SHA-256 $hash  $($zipFile.Name)"
        $stream.Position = 0
        $archive = [IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Read,$true)
        try {
            if ($archive.Entries.Count -ne $expected.Count) { throw 'В ZIP неверное количество файлов.' }
            foreach ($name in $expected) {
                $entry = $archive.GetEntry('LunarisDock-Portable/' + $name)
                if (!$entry -or $entry.Length -eq 0) { throw "Нет обязательного файла: $name" }
                # Only these fixed basenames are used to form paths on disk.
                $target = Join-Path $original $name
                $destination = [IO.File]::Open($target,[IO.FileMode]::CreateNew)
                try {
                    $source = $entry.Open()
                    try { $source.CopyTo($destination) } finally { $source.Dispose() }
                } finally { $destination.Dispose() }
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
    $config = New-Object System.Xml.XmlDocument
    $config.XmlResolver = $null
    $config.Load((Join-Path $original 'LunarisDock.exe.config'))
    $flag = $config.SelectNodes('/configuration/appSettings/add[@key="LunarisPortable"]')
    if ($flag.Count -ne 1 -or $flag[0].GetAttribute('value') -ne 'true') { throw 'ZIP не содержит переносимый конфиг.' }
    if (!$config.SelectSingleNode('/configuration/startup/supportedRuntime[@sku=".NETFramework,Version=v4.8"]')) {
        throw 'В конфиге утрачена версия .NET Framework.'
    }
    $cmd = [IO.File]::ReadAllText((Join-Path $original 'Тестовый запуск.cmd'))
    if (!$cmd.Contains('--preview') -or !$cmd.Contains('%~dp0')) { throw 'Тестовый запуск не привязан к папке пакета.' }
    Write-Output 'PASS root, exact package contents, portable config and preview launcher'
}

function Invoke-PortableProbe([string]$Exe,[string]$Phase) {
    & "$PSScriptRoot\..\scripts\Invoke-TestProgram.ps1" -SourceFile "$PSScriptRoot\PortableProfileTests.cs" -ExePath $Exe -TestArguments @($Phase,$Exe,$foreign,$proof)
}

function Get-UserState {
    foreach ($folder in 'Profile','Preview') {
        foreach ($file in 'settings.json','settings.json.bak') {
            $path = Join-Path $env:LOCALAPPDATA ("LunarisDock\$folder\$file")
            if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } else { '<absent>' }
        }
    }
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
    if ($null -eq $key) { '<no Run key>' }
    else {
        try {
            foreach ($name in 'LunarisDock','LunarisDockPortable') { [string]$key.GetValue($name,'<absent>') }
        } finally { $key.Dispose() }
    }
    [string][LunarisLab]::State()
    [string][LunarisLab]::BarVisible()
}

Start-Transcript -LiteralPath (Join-Path $log "$mode.txt") -Force | Out-Null
try {
    if (Test-Path -LiteralPath $work) { throw "Временное имя уже занято: $work" }
    [void][IO.Directory]::CreateDirectory($original)
    [void][IO.Directory]::CreateDirectory($foreign)
    Write-Output "ZIP: $($zipFile.FullName)"
    Expand-VerifiedPackage
    $exe = Join-Path $original 'LunarisDock.exe'
    Invoke-PortableProbe $exe 'paths'
    if (!$PackageOnly) {
        . "$PSScriptRoot\Lab.ps1"
        $before = @(Get-UserState)
        if ($StoreOnly) {
            & "$PSScriptRoot\Preview.Tests.ps1" -ExePath $exe -OutDir $log -StoreOnly
        } else {
            Invoke-PortableProbe $exe 'prepare'
            Assert-WorkPath $original
            Assert-WorkPath $moved
            Move-Item -LiteralPath $original -Destination $moved
            $exe = Join-Path $moved 'LunarisDock.exe'
            Invoke-PortableProbe $exe 'paths'
            Invoke-PortableProbe $exe 'moved'
            if (Test-Path -LiteralPath (Join-Path $moved 'Data\Profile')) { throw 'Preview затронул основной профиль.' }
            if (Test-Path -LiteralPath (Join-Path $foreign 'Data')) { throw 'В рабочей папке появились данные приложения.' }
            & "$PSScriptRoot\Core.Tests.ps1" -ExePath $exe | Tee-Object -FilePath (Join-Path $log 'core.txt')
            & "$PSScriptRoot\Preview.Tests.ps1" -ExePath $exe -OutDir $log -LaunchName $LaunchName -StoreCalculator:(!$PrivateDesktop) -CaptureFolder:$PrivateDesktop
        }
        $after = @(Get-UserState)
        if (($before -join "|") -cne ($after -join "|")) { throw 'Изменилось состояние установленного профиля, автозапуска или панели задач.' }
        Write-Output 'PASS default installed profiles, startup value and observed taskbar state preserved'
    }
    $success = $true
    Write-Output "PASS portable $mode"
} finally {
    try {
        if ($success) {
            Assert-WorkPath $work
            if (Get-ChildItem -LiteralPath $work -Recurse -Force -Attributes ReparsePoint) { throw "Временная папка содержит перенаправление: $work" }
            Remove-Item -LiteralPath $work -Recurse -Force
        } else { Write-Output "Файлы неудачного прогона сохранены: $work" }
    } finally { Stop-Transcript | Out-Null }
}
