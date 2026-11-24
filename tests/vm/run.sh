#!/usr/bin/env bash
# Installs Windows 11 (evaluation) in QEMU with Secure Boot on and test signing off,
# then runs the integration test in it. Usage: run.sh <windows.iso> <VirtualBatterySetup.exe>
set -euo pipefail
iso=$1
setup=$2
here=$(cd "$(dirname "$0")" && pwd)
work=${WORK:-/mnt/vbtest}
mkdir -p "$work/cd" "$work/shots"
cp "$here/autounattend.xml" "$here/guest.ps1" "$here/../integration.ps1" "$setup" "$work/cd/"
xorriso -as mkisofs -quiet -J -r -V VBTEST -o "$work/test.iso" "$work/cd"
cp /usr/share/OVMF/OVMF_VARS_4M.ms.fd "$work/vars.fd"   # Microsoft keys enrolled
qemu-img create -q -f qcow2 "$work/disk.qcow2" 40G
: > "$work/serial.log"

qemu-system-x86_64 -enable-kvm -machine q35,smm=on -cpu host,hv_relaxed,hv_vapic,hv_spinlocks=0x1fff,hv_time \
    -smp "$(nproc)" -m 5G -display none -vga std \
    -global driver=cfi.pflash01,property=secure,value=on \
    -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.secboot.fd \
    -drive if=pflash,format=raw,file="$work/vars.fd" \
    -drive id=d0,if=none,format=qcow2,cache=unsafe,discard=unmap,file="$work/disk.qcow2" -device ide-hd,drive=d0,bus=ide.0,bootindex=1 \
    -drive id=c0,if=none,media=cdrom,readonly=on,file="$iso" -device ide-cd,drive=c0,bus=ide.1,bootindex=2 \
    -drive id=c1,if=none,media=cdrom,readonly=on,file="$work/test.iso" -device ide-cd,drive=c1,bus=ide.2 \
    -device qemu-xhci -device usb-tablet -nic none \
    -serial "file:$work/serial.log" -monitor "unix:$work/mon.sock,server,nowait" &
vm=$!
monitor() { echo "$1" | timeout 3 socat - "UNIX-CONNECT:$work/mon.sock" >/dev/null 2>&1 || true; }

# "Press any key to boot from CD"
for _ in $(seq 25); do sleep 1; monitor 'sendkey ret'; done

deadline=$((SECONDS + ${TIMEOUT:-4500}))
shot=0
while kill -0 "$vm" 2>/dev/null && [ "$SECONDS" -lt "$deadline" ]; do
    sleep 60
    shot=$((shot + 1))
    monitor "screendump $work/shots/$(printf '%03d' $shot).ppm"
done
kill "$vm" 2>/dev/null || true
echo '--- serial log ---'
cat "$work/serial.log"
grep -q 'VM-INTEGRATION-EXIT 0' "$work/serial.log"
grep -q 'VM-INSTALL-EXIT 0' "$work/serial.log"
grep -q 'VM-AFTER-REBOOT batteries=1' "$work/serial.log"
grep -q 'VM-UNINSTALL-EXIT 0 batteries=0' "$work/serial.log"
