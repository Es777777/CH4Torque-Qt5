$ErrorActionPreference = 'Stop'
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) {
    $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$taskOutput = Join-Path (Split-Path -Parent $PSScriptRoot) 'CH4Torque_v1.1.exe'
$taskSources = @('Core.cs', 'MainForm.cs', 'Program.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$taskManifest = Join-Path $PSScriptRoot 'app.manifest'
& $taskCompiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Runtime.Serialization.dll "/win32manifest:$taskManifest" "/out:$taskOutput" $taskSources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output $taskOutput
