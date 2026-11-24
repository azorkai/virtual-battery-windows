# Builds VirtualBattery.dll (UMDF driver), its catalog and VirtualBatterySetup.exe.
# Needs: Visual Studio 2022 C++ build tools and the .NET SDK. The Windows SDK/WDK
# come from pinned, hash-checked NuGet packages, so no WDK installation is required.
param([string]$Cache = "$PSScriptRoot\.cache")

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$kitVersion = '10.0.26100.2454'
$kitDir = '10.0.26100.0'
$out = "$PSScriptRoot\out"

$packages = @(
    @{ Id = 'microsoft.windows.sdk.cpp';     Hash = 'E1ADA25D24444AC62FD7CB33894D7A7827EC4FA55A22956D0C1E51C2A66F7462' }
    @{ Id = 'microsoft.windows.sdk.cpp.x64'; Hash = '8A5390763FBE8BBCBBEB8E9D1C46B196D36DD8D5C13A6EADC8113FF043BCE0A8' }
    @{ Id = 'microsoft.windows.wdk.x64';     Hash = '9C4A31B01583599560E38D206E4401A64423B84D7AE2661DC7222A6B8E419D2B' }
)

New-Item -ItemType Directory -Force $Cache | Out-Null
foreach ($p in $packages) {
    $target = "$Cache\$($p.Id)"
    if (Test-Path "$target\.ok") { continue }
    $zip = "$target.zip"
    Write-Host "Downloading $($p.Id) $kitVersion"
    Invoke-WebRequest -UseBasicParsing "https://api.nuget.org/v3-flatcontainer/$($p.Id)/$kitVersion/$($p.Id).$kitVersion.nupkg" -OutFile $zip
    if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $p.Hash) { throw "Hash mismatch: $($p.Id)" }
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $target)
    Remove-Item $zip
    New-Item -ItemType File "$target\.ok" | Out-Null
}
$sdk = "$Cache\microsoft.windows.sdk.cpp\c"
$sdkLib = "$Cache\microsoft.windows.sdk.cpp.x64\c"
$wdk = "$Cache\microsoft.windows.wdk.x64\c"

# MSVC
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msvc = & $vswhere -all -products * -property installationPath |
    ForEach-Object { Get-ChildItem "$_\VC\Tools\MSVC" -Directory -ErrorAction SilentlyContinue } |
    Where-Object { (Test-Path "$($_.FullName)\bin\Hostx64\x64\cl.exe") -and (Test-Path "$($_.FullName)\lib\x64\libcmt.lib") } |
    Sort-Object { [version]$_.Name } | Select-Object -Last 1
if (-not $msvc) { throw 'Visual Studio C++ x64 build tools were not found.' }
$msvc = $msvc.FullName

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory "$out\obj", "$out\driver" | Out-Null

# Driver
$env:INCLUDE = @("$msvc\include", "$wdk\Include\wdf\umdf\2.15", "$wdk\Include\$kitDir\um", "$wdk\Include\$kitDir\shared", "$wdk\Include\$kitDir\km",
    "$sdk\Include\$kitDir\um", "$sdk\Include\$kitDir\shared", "$sdk\Include\$kitDir\ucrt") -join ';'
$env:LIB = @("$msvc\lib\x64", "$wdk\Lib\wdf\umdf\x64\2.15", "$wdk\Lib\$kitDir\um\x64", "$sdkLib\um\x64", "$sdkLib\ucrt\x64") -join ';'

& "$msvc\bin\Hostx64\x64\cl.exe" /nologo /W4 /WX /wd4324 /wd4201 /wd4214 /O2 /MT /GS /guard:cf /Brepro `
    /D_AMD64_ /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /DWINVER=0x0A00 /DWINNT=1 `
    /DUMDF_VERSION_MAJOR=2 /DUMDF_VERSION_MINOR=15 /DUMDF_USING_NTSTATUS /DUMDF_DRIVER `
    "$PSScriptRoot\driver\VirtualBattery.c" "/Fo$out/obj/" /link /NOLOGO /DLL /SUBSYSTEM:WINDOWS /MACHINE:X64 /Brepro `
    /GUARD:CF /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /CETCOMPAT /OPT:REF,ICF "/OUT:$out\driver\VirtualBattery.dll" "/IMPLIB:$out\obj\VirtualBattery.lib" `
    WdfDriverStubUm.lib ntdll.lib kernel32.lib
if ($LASTEXITCODE) { throw 'Driver build failed.' }

# Catalog (unsigned here; signed on the target PC with a throw-away key)
Copy-Item "$PSScriptRoot\driver\VirtualBattery.inf" "$out\driver"
& "$wdk\bin\$kitDir\x86\Inf2Cat.exe" "/driver:$out\driver" '/os:10_VB_X64' /uselocaltime
if ($LASTEXITCODE) { throw 'Catalog generation failed.' }

# Setup
dotnet build "$PSScriptRoot\app\VirtualBatterySetup.csproj" -c Release -o "$out\app" --nologo -v minimal
if ($LASTEXITCODE) { throw 'Setup build failed.' }
Copy-Item "$out\app\VirtualBatterySetup.exe" "$out\VirtualBatterySetup.exe"
$hash = (Get-FileHash "$out\VirtualBatterySetup.exe" -Algorithm SHA256).Hash.ToLower()
"$hash  VirtualBatterySetup.exe" | Out-File "$out\VirtualBatterySetup.exe.sha256" -Encoding ascii
Write-Host "OK  $out\VirtualBatterySetup.exe  $hash"
