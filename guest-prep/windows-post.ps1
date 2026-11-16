#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Run INSIDE the Windows guest on Proxmox once it boots on its final hardware
  (after "vmware2pve.sh finalize", if you needed that step).

.DESCRIPTION
  - Removes ghost VMware / e1000e devices left over from the old hardware.
  - Re-applies the static IPs saved by windows-prep.ps1 (matched by MAC,
    which vmware2pve.sh keeps).
  - Resumes BitLocker and checks the QEMU guest agent.
#>
$ErrorActionPreference = 'Stop'
$stateDir = Join-Path $env:ProgramData 'vmware2pve'

# 1. Ghost devices --------------------------------------------------------------
$ghosts = Get-PnpDevice | Where-Object {
    -not $_.Present -and $_.FriendlyName -match 'VMware|vmxnet|PVSCSI|82574L'
}
foreach ($g in $ghosts) {
    Write-Host "==> removing ghost device: $($g.FriendlyName)"
    pnputil.exe /remove-device "$($g.InstanceId)" | Out-Null
}

# 2. Static IPs -----------------------------------------------------------------
$file = Join-Path $stateDir 'netconfig.json'
if (Test-Path $file) {
    foreach ($cfg in @(Get-Content $file -Raw | ConvertFrom-Json)) {
        if (-not $cfg) { continue }
        $ad = Get-NetAdapter -Physical | Where-Object MacAddress -eq $cfg.Mac
        if (-not $ad) { Write-Warning "no adapter with MAC $($cfg.Mac) - configure $($cfg.Name) manually"; continue }
        Write-Host "==> applying static IP config to $($ad.Name) ($($cfg.Mac))"
        Set-NetIPInterface -InterfaceIndex $ad.ifIndex -Dhcp Disabled
        Get-NetIPAddress -InterfaceIndex $ad.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Remove-NetIPAddress -Confirm:$false
        Get-NetRoute -InterfaceIndex $ad.ifIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
            Remove-NetRoute -Confirm:$false
        $first = $true
        foreach ($ip in $cfg.IPs) {
            $params = @{ InterfaceIndex = $ad.ifIndex; IPAddress = $ip.IP; PrefixLength = $ip.Prefix }
            if ($first -and $cfg.Gateway) { $params.DefaultGateway = $cfg.Gateway }
            New-NetIPAddress @params | Out-Null
            $first = $false
        }
        if ($cfg.Dns) { Set-DnsClientServerAddress -InterfaceIndex $ad.ifIndex -ServerAddresses $cfg.Dns }
    }
}

# 3. BitLocker ------------------------------------------------------------------
if (Get-Command Get-BitLockerVolume -ErrorAction SilentlyContinue) {
    Get-BitLockerVolume | Where-Object ProtectionStatus -eq 'Off' | Where-Object VolumeStatus -eq 'FullyEncrypted' |
        ForEach-Object {
            Write-Host "==> resuming BitLocker on $($_.MountPoint)"
            Resume-BitLocker -MountPoint $_.MountPoint | Out-Null
        }
}

# 4. Guest agent ------------------------------------------------------------------
$qga = Get-Service QEMU-GA -ErrorAction SilentlyContinue
if ($qga -and $qga.Status -eq 'Running') { Write-Host "==> QEMU guest agent running" }
else { Write-Warning "QEMU guest agent not running - install virtio-win-guest-tools.exe from the virtio-win CD" }
