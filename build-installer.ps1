# Builds DeviceMaster-Setup.exe into dist\.
# Reads the whole-number version from DeviceMaster.Ui.csproj (major of <Version>).
$ErrorActionPreference = 'Stop'

$csproj = Join-Path $PSScriptRoot 'src\DeviceMaster.Ui\DeviceMaster.Ui.csproj'
$version = ([regex]::Match((Get-Content $csproj -Raw), '<Version>(\d+)').Groups[1].Value)
if (-not $version) { throw "Could not read <Version> from $csproj" }

Write-Host "Publishing DeviceMaster v$version (self-contained win-x64)..."
dotnet publish (Join-Path $PSScriptRoot 'src\DeviceMaster.Ui') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -o (Join-Path $PSScriptRoot 'dist\ui')
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# strip debug symbols from the payload
Remove-Item (Join-Path $PSScriptRoot 'dist\ui\*.pdb') -ErrorAction SilentlyContinue

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found — install from https://jrsoftware.org/isdl.php' }

Write-Host "Compiling installer with $iscc ..."
& $iscc "/DMyAppVersion=$version" (Join-Path $PSScriptRoot 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

Write-Host "Done: dist\DeviceMaster-Setup.exe (v$version)"
