param(
    [string]$OutputDirectory,
    [string]$Exe,
    [switch]$RunPortableTests
)
$ErrorActionPreference = 'Stop'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot '..\release\1.3.1.8' }
if (!$Exe) { $Exe = Join-Path $PSScriptRoot '..\release\1.3.1.8\LunarisDock-Portable\LunarisDock.exe' }
$framework = "$env:windir\Microsoft.NET\Framework64\v4.0.30319"
$tool = Join-Path $PSScriptRoot '..\build\CaptureDesktop.exe'
& "$framework\csc.exe" /nologo /codepage:65001 /platform:x64 "/out:$tool" /r:System.Drawing.dll /r:System.Windows.Forms.dll "/r:$framework\WPF\UIAutomationClient.dll" "/r:$framework\WPF\UIAutomationTypes.dll" "/r:$framework\WPF\WindowsBase.dll" "$PSScriptRoot\CaptureDesktop.cs"
if ($LASTEXITCODE) { throw 'Ошибка компиляции инструмента съёмки.' }
if ($RunPortableTests) {
    & $tool /tests (Resolve-Path (Join-Path $PSScriptRoot '..\tests\Portable.Tests.ps1')).Path ([IO.Path]::GetFullPath($OutputDirectory))
} else {
    & $tool (Resolve-Path $Exe).Path ([IO.Path]::GetFullPath($OutputDirectory))
}
if ($LASTEXITCODE) {
    if ($RunPortableTests) { throw "Проверка переносной сборки завершилась с ошибкой. Журнал $OutputDirectory\full.txt." }
    throw "Съёмка не удалась. Подробности в $OutputDirectory\capture-error.txt."
}
if ($RunPortableTests) {
    & "$PSScriptRoot\..\tests\Portable.Tests.ps1" -StoreOnly -OutDir $OutputDirectory
}
