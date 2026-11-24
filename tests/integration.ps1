# Installs, exercises and removes Virtual Battery on this machine (administrator).
# Meant for disposable CI machines.
param([string]$Setup = "$PSScriptRoot\..\out\VirtualBatterySetup.exe", [switch]$RequireSecureBoot)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Power {
    [StructLayout(LayoutKind.Sequential)]
    public struct Status { public byte AcLine, BatteryFlag, Percent, Saver; public int Life, Full; }
    [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out Status s);
    [DllImport("powrprof.dll")] public static extern bool GetPwrCapabilities(byte[] caps);
    public static Status Get() { Status s; GetSystemPowerStatus(out s); return s; }
}
'@

function Invoke-Setup([string]$Arguments) {
    $log = New-TemporaryFile
    $p = Start-Process $Setup $Arguments -Wait -PassThru -RedirectStandardOutput $log
    Get-Content $log -Encoding UTF8 | ForEach-Object { Write-Host "  | $_" }
    $p.ExitCode
}
function Show-State {
    $s = [Power]::Get()
    Write-Host "GetSystemPowerStatus: AC=$($s.AcLine) Flag=$($s.BatteryFlag) Percent=$($s.Percent)"
    $caps = New-Object byte[] 256
    [void][Power]::GetPwrCapabilities($caps)
    # SYSTEM_POWER_CAPABILITIES: LidPresent=2, SystemBatteriesPresent=30, BatteriesAreShortTerm=31
    Write-Host "PowerCapabilities: SystemBatteriesPresent=$($caps[30]) BatteriesAreShortTerm=$($caps[31]) Lid=$($caps[2])"
    Get-CimInstance Win32_Battery | Format-List Name, Status, EstimatedChargeRemaining, BatteryStatus, Chemistry | Out-String | Write-Host
    Get-PnpDevice -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -match 'VIRTUALBATTERY|VID_1209' -or $_.Class -eq 'Battery' } |
        Format-Table Status, Class, Problem, FriendlyName, InstanceId -AutoSize | Out-String -Width 200 | Write-Host
}
function Show-Diagnostics {
    Get-Content "$env:windir\INF\setupapi.dev.log" -Tail 250 -ErrorAction SilentlyContinue | Write-Host
    Get-WinEvent 'Microsoft-Windows-DriverFrameworks-UserMode/Operational' -MaxEvents 30 -ErrorAction SilentlyContinue |
        Format-List TimeCreated, Id, Message | Out-String | Write-Host
}
function Wait-Until([scriptblock]$Condition, [int]$Seconds = 45) {
    for ($i = 0; $i -lt $Seconds * 2; $i++) { if (& $Condition) { return $true }; Start-Sleep -Milliseconds 500 }
    $false
}
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { Show-State; Show-Diagnostics; throw "FAILED: $Message" }
    Write-Host "PASS  $Message"
}
function Get-OurCertificates {
    @(Get-ChildItem Cert:\LocalMachine\Root, Cert:\LocalMachine\TrustedPublisher | Where-Object Subject -eq 'CN=Virtual Battery (this PC only)')
}

Write-Host "Windows: $([Environment]::OSVersion.Version)"
$secureBoot = try { [bool](Confirm-SecureBootUEFI) } catch { $false }
$testSigning = [bool]((Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Control).SystemStartOptions -match 'TESTSIGNING')
Write-Host "Secure Boot: $secureBoot  Test signing: $testSigning"
if ($RequireSecureBoot) {
    Assert $secureBoot 'Secure Boot is on'
    Assert (-not $testSigning) 'test signing is off'
}
Show-State
Assert (([Power]::Get().BatteryFlag -band 128) -ne 0) 'no battery before installation'

Set-ItemProperty $Setup IsReadOnly $true   # as if started from read-only media
$code = Invoke-Setup '/install /quiet'
Assert ($code -eq 0) "install exit code is 0 (got $code)"
Show-State
$s = [Power]::Get()
Assert (($s.BatteryFlag -band 128) -eq 0 -and $s.BatteryFlag -ne 255) 'Windows reports a battery'
Assert (Wait-Until { $x = [Power]::Get(); $x.AcLine -eq 1 -and $x.Percent -eq 100 }) 'plugged in, 100%'
Assert (@(Get-CimInstance Win32_Battery).Count -eq 1) 'WMI reports one battery'

$certs = Get-OurCertificates
Assert ($certs.Count -eq 2) 'certificate is in Root and TrustedPublisher'
Assert (@($certs | Where-Object HasPrivateKey).Count -eq 0) 'certificate has no private key'
$keys = @(certutil -user -csp 'Microsoft Software Key Storage Provider' -key 2>$null | Where-Object { $_ -match 'VirtualBattery-' })
Assert ($keys.Count -eq 0) 'signing key was destroyed'

# Live settings
$key = 'HKLM:\SYSTEM\CurrentControlSet\Enum\ROOT\VIRTUALBATTERY\0000\Device Parameters'
Set-ItemProperty $key BatteryPercent 73
Assert (Wait-Until { [Power]::Get().Percent -eq 73 }) 'level change is live (73%)'
Set-ItemProperty $key BatteryPercent 1
Assert (Wait-Until { [Power]::Get().Percent -eq 40 }) 'level never drops below 40%'
Assert ([Power]::Get().AcLine -eq 1) 'always reports external power'

# Device restart, as after a reboot
pnputil /restart-device 'ROOT\VIRTUALBATTERY\0000' | Out-Null
Assert (Wait-Until { ([Power]::Get().BatteryFlag -band 128) -eq 0 }) 'battery comes back after a device restart'

# Reinstall over the top, then remove
$code = Invoke-Setup '/install /quiet'
Assert ($code -eq 0) "reinstall exit code is 0 (got $code)"
Assert (@(Get-CimInstance Win32_Battery).Count -eq 1) 'still one battery after reinstall'
$code = Invoke-Setup '/uninstall /quiet'
Assert ($code -eq 0) "uninstall exit code is 0 (got $code)"
Assert (Wait-Until { ([Power]::Get().BatteryFlag -band 128) -ne 0 }) 'no battery after removal'
Assert ((Get-OurCertificates).Count -eq 0) 'certificates are gone'
Assert (@(Get-PnpDevice | Where-Object InstanceId -match 'VIRTUALBATTERY').Count -eq 0) 'device is gone'
Assert (-not ((pnputil /enum-drivers | Out-String) -match 'virtualbattery\.inf')) 'driver package is gone'
Assert (-not (Test-Path "$env:ProgramFiles\Virtual Battery")) 'program folder is gone'
Assert (-not (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VirtualBattery')) 'uninstall entry is gone'
Write-Host 'All checks passed.'
