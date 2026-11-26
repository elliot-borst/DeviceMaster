# Builds DeviceMaster-Setup.exe into dist\.
# Reads the whole-number version from Directory.Build.props (major of <Version>).
#
# Code signing (#123): give the code-signing certificate's thumbprint with -SignThumbprint or the
# DEVICEMASTER_SIGN_THUMBPRINT environment variable. The certificate must be in CurrentUser\My with
# its private key reachable (a cloud certificate needs its desktop client logged in, e.g. Certum
# SimplySign). DeviceMaster.exe, the setup and the uninstaller are then signed with SHA-256 and an
# RFC 3161 timestamp, so the signatures stay valid after the certificate expires. Without a usable
# certificate the build warns and stays unsigned; -RequireSigning makes that an error instead.
param(
    [string]$SignThumbprint = $env:DEVICEMASTER_SIGN_THUMBPRINT,
    [string]$TimestampUrl = 'http://time.certum.pl',
    [switch]$RequireSigning
)
$ErrorActionPreference = 'Stop'

function Find-SignTool {
    Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName | Select-Object -Last 1 -ExpandProperty FullName
}

$signTool = $null
$thumb = ($SignThumbprint -replace '\s', '').ToUpperInvariant()
if ($thumb) {
    $cert = Get-Item "Cert:\CurrentUser\My\$thumb" -ErrorAction SilentlyContinue
    $signTool = Find-SignTool
    $why = if (-not $cert) { "certificate $thumb is not in CurrentUser\My (is the signing client logged in?)" }
           elseif (-not $cert.HasPrivateKey) { "certificate $thumb has no reachable private key (is the signing client logged in?)" }
           elseif ($cert.NotAfter -lt (Get-Date)) { "certificate $thumb expired on $($cert.NotAfter)" }
           elseif (-not $signTool) { 'signtool.exe not found (install the Windows SDK signing tools)' }
    if ($why) {
        if ($RequireSigning) { throw "Signing required but $why" }
        Write-Warning "Building UNSIGNED: $why"
        $signTool = $null
    } else {
        Write-Host "Signing with $($cert.Subject) ($thumb), valid to $($cert.NotAfter.ToString('yyyy-MM-dd'))"
    }
} elseif ($RequireSigning) {
    throw 'Signing required but no thumbprint given (-SignThumbprint or DEVICEMASTER_SIGN_THUMBPRINT)'
} else {
    Write-Warning 'Building UNSIGNED: no signing thumbprint set'
}

# signtool arguments shared by the app and (through ISCC) the setup and uninstaller
$signArgs = "sign /sha1 $thumb /fd sha256 /tr $TimestampUrl /td sha256 /d DeviceMaster /du https://github.com/elliot-borst/DeviceMaster"

$csproj = Join-Path $PSScriptRoot 'Directory.Build.props'
$version = ([regex]::Match((Get-Content $csproj -Raw), '<Version>(\d+)').Groups[1].Value)
if (-not $version) { throw "Could not read <Version> from $csproj" }

Write-Host "Publishing DeviceMaster v$version (self-contained win-x64)..."
dotnet publish (Join-Path $PSScriptRoot 'src\DeviceMaster.Ui') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -o (Join-Path $PSScriptRoot 'dist\ui')
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# strip debug symbols from the payload
Remove-Item (Join-Path $PSScriptRoot 'dist\ui\*.pdb') -ErrorAction SilentlyContinue

if ($signTool) {
    $app = Join-Path $PSScriptRoot 'dist\ui\DeviceMaster.exe'
    & $signTool sign /sha1 $thumb /fd sha256 /tr $TimestampUrl /td sha256 /d DeviceMaster /du https://github.com/elliot-borst/DeviceMaster $app
    if ($LASTEXITCODE -ne 0) { throw "signing $app failed" }
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found — install from https://jrsoftware.org/isdl.php' }

Write-Host "Compiling installer with $iscc ..."
$isccArgs = @("/DMyAppVersion=$version")
if ($signTool) {
    # ISCC substitutes $q with a quote and $f with the quoted file it signs (the setup, then the
    # uninstaller inside it); $q avoids Windows PowerShell's broken quoting of native arguments
    $isccArgs += "/Sdmsign=`$q$signTool`$q $signArgs `$f", '/DSignTool=dmsign'
}
& $iscc @isccArgs (Join-Path $PSScriptRoot 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

$setup = Join-Path $PSScriptRoot 'dist\DeviceMaster-Setup.exe'
if ($signTool) {
    & $signTool verify /pa $setup | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "the signed setup doesn't verify" }
}

Write-Host "Done: dist\DeviceMaster-Setup.exe (v$version)$(if ($signTool) { ', signed' } else { ', UNSIGNED' })"
