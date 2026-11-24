# Runs inside the test VM; output goes to the first serial port.
param([int]$Phase = 1)

$port = New-Object IO.Ports.SerialPort 'COM1', 115200
$port.Open()
function Out-Serial { process { $port.WriteLine([string]$_) } }
function Invoke-Script([string]$Path, [string[]]$Arguments) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $Path @Arguments 2>&1 | Out-Serial
    $LASTEXITCODE
}

if ($Phase -eq 1) {
    "VM-PHASE-1 $((Get-CimInstance Win32_OperatingSystem).Caption)" | Out-Serial
    Set-ItemProperty HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce VirtualBatteryTest `
        'powershell -NoProfile -ExecutionPolicy Bypass -File C:\vb\guest.ps1 -Phase 2'
    $code = Invoke-Script C:\vb\integration.ps1 @('-Setup', 'C:\vb\VirtualBatterySetup.exe', '-RequireSecureBoot')
    "VM-INTEGRATION-EXIT $code" | Out-Serial
    $p = Start-Process C:\vb\VirtualBatterySetup.exe '/install /quiet' -Wait -PassThru
    "VM-INSTALL-EXIT $($p.ExitCode)" | Out-Serial
    $port.Close()
    Restart-Computer -Force
} else {
    Start-Sleep 20
    $battery = @(Get-CimInstance Win32_Battery)
    "VM-AFTER-REBOOT batteries=$($battery.Count) charge=$($battery.EstimatedChargeRemaining)" | Out-Serial
    $p = Start-Process 'C:\Program Files\Virtual Battery\VirtualBatterySetup.exe' '/uninstall /quiet' -Wait -PassThru
    Start-Sleep 5
    "VM-UNINSTALL-EXIT $($p.ExitCode) batteries=$(@(Get-CimInstance Win32_Battery).Count)" | Out-Serial
    'VM-DONE' | Out-Serial
    $port.Close()
    Stop-Computer -Force
}
