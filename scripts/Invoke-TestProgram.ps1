param(
    [Parameter(Mandatory)][string]$SourceFile,
    [Parameter(Mandatory)][string]$ExePath,
    [string[]]$TestArguments = @(),
    [switch]$CompileOnly
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $ExePath).Path
$framework = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319'
$temp = Join-Path $env:TEMP ('Lunaris-Tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $temp | Out-Null
$output = Join-Path $temp 'LunarisTest.exe'
try {
    $refs = @("/reference:$exe")
    foreach ($dll in 'System','System.Core','System.Xml','System.Xaml','System.Runtime.Serialization','System.Windows.Forms','System.Drawing','Microsoft.CSharp') {
        $refs += "/reference:$framework\$dll.dll"
    }
    foreach ($dll in 'PresentationCore','PresentationFramework','WindowsBase','UIAutomationClient','UIAutomationTypes') {
        $refs += "/reference:$framework\WPF\$dll.dll"
    }
    & "$framework\csc.exe" /nologo /target:exe /platform:x64 /codepage:65001 "/out:$output" @refs $SourceFile
    if ($LASTEXITCODE -ne 0) { throw "Test did not compile ($LASTEXITCODE)." }
    if ($CompileOnly) { return }
    Copy-Item -LiteralPath $exe -Destination (Join-Path $temp 'LunarisDock.exe')
    & $output @TestArguments
    if ($LASTEXITCODE -ne 0) { throw "Test failed ($LASTEXITCODE)." }
} finally {
    foreach ($file in $output, (Join-Path $temp 'LunarisDock.exe')) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    }
    if (Test-Path -LiteralPath $temp) { [IO.Directory]::Delete($temp, $false) }
}
