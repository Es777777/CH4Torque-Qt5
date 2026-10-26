param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'CH4Torque'),
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$repo = 'Es777777/CH4Torque-Qt5'
$asset = 'CH4Torque-windows-x64.zip'
$work = Join-Path $env:TEMP ('CH4Torque-' + [guid]::NewGuid().ToString('N'))

function Test-VcRuntime {
    $keys = @(
        'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64'
    )
    foreach ($key in $keys) {
        if (Test-Path $key) {
            $runtime = Get-ItemProperty $key
            if ($runtime.Installed -eq 1 -and $runtime.Major -ge 14) { return $true }
        }
    }
    return $false
}

try {
    if ($env:OS -ne 'Windows_NT') { throw 'This installer requires Windows.' }
    if ([Environment]::Is64BitOperatingSystem -eq $false) { throw 'A 64-bit Windows installation is required.' }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    New-Item -ItemType Directory -Path $work -Force | Out-Null

    if (-not (Test-VcRuntime)) {
        Write-Host 'Microsoft Visual C++ 2015-2022 x64 Runtime is missing. Downloading it from Microsoft...'
        $redist = Join-Path $work 'vc_redist.x64.exe'
        Invoke-WebRequest -Uri 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $redist
        $process = Start-Process -FilePath $redist -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru -Verb RunAs
        if ($process.ExitCode -notin @(0, 1638, 3010)) { throw "VC++ Runtime installer returned exit code $($process.ExitCode)." }
        if (-not (Test-VcRuntime)) { throw 'VC++ Runtime installation could not be verified.' }
    }

    Write-Host 'Downloading the latest offline application package from GitHub Releases...'
    $zip = Join-Path $work $asset
    $checksums = Join-Path $work 'SHA256SUMS.txt'
    Invoke-WebRequest -Uri "https://github.com/$repo/releases/latest/download/$asset" -OutFile $zip
    Invoke-WebRequest -Uri "https://github.com/$repo/releases/latest/download/SHA256SUMS.txt" -OutFile $checksums
    $line = Get-Content $checksums | Where-Object { $_ -match ('\s' + [regex]::Escape($asset) + '$') } | Select-Object -First 1
    if (-not $line) { throw 'Release checksum entry was not found.' }
    $expected = ($line -split '\s+')[0].ToLowerInvariant()
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw 'The downloaded package checksum does not match the published checksum.' }

    $stage = Join-Path $work 'unpacked'
    Expand-Archive -LiteralPath $zip -DestinationPath $stage -Force
    New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $stage '*') -Destination $InstallDirectory -Recurse -Force
    $app = Join-Path $InstallDirectory 'CH4Torque.exe'
    if (-not (Test-Path $app)) { throw 'CH4Torque.exe was not found in the downloaded package.' }
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'CH4 Torque Monitor.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $app
    $shortcut.WorkingDirectory = $InstallDirectory
    $shortcut.Save()
    Write-Host "Installed CH4 Torque Monitor to $InstallDirectory"
    if (-not $NoLaunch) { Start-Process -FilePath $app -WorkingDirectory $InstallDirectory }
}
catch {
    Write-Error $_
    exit 1
}
finally {
    if (Test-Path $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
