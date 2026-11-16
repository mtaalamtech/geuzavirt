#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Run INSIDE a Windows guest while it is still on VMware, before migrating.

.DESCRIPTION
  - Saves static IP settings so windows-post.ps1 can re-apply them on Proxmox
    (Windows sees the new NIC as a different adapter).
  - Suspends BitLocker (the VMware vTPM does not move to Proxmox).
  - Optionally installs the VirtIO drivers + QEMU guest agent now, so the VM
    can be imported with --virtio-ready.
  - Optionally uninstalls VMware Tools (it uninstalls cleanly only on VMware).

.EXAMPLE
  .\windows-prep.ps1 -VirtioInstaller D:\virtio-win-guest-tools.exe -RemoveVmwareTools -Shutdown
#>
param(
    [string]$VirtioInstaller,
    [switch]$RemoveVmwareTools,
    [switch]$Shutdown
)
$ErrorActionPreference = 'Stop'
$stateDir = Join-Path $env:ProgramData 'vmware2pve'
New-Item -ItemType Directory -Force $stateDir | Out-Null

# 1. Static IP configuration ----------------------------------------------------
$saved = foreach ($ad in Get-NetAdapter -Physical | Where-Object Status -eq 'Up') {
    $ip = Get-NetIPAddress -InterfaceIndex $ad.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
          Where-Object PrefixOrigin -eq 'Manual'
    if (-not $ip) { continue }
    [pscustomobject]@{
        Mac     = $ad.MacAddress
        Name    = $ad.Name
        IPs     = @($ip | ForEach-Object { @{ IP = $_.IPAddress; Prefix = $_.PrefixLength } })
        Gateway = (Get-NetRoute -InterfaceIndex $ad.ifIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
                   Select-Object -First 1).NextHop
        Dns     = @((Get-DnsClientServerAddress -InterfaceIndex $ad.ifIndex -AddressFamily IPv4).ServerAddresses)
    }
}
ConvertTo-Json @($saved) -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $stateDir 'netconfig.json')
Write-Host "==> saved $(@($saved).Count) static adapter config(s) to $stateDir\netconfig.json"

# 2. BitLocker --------------------------------------------------------------------
if (Get-Command Get-BitLockerVolume -ErrorAction SilentlyContinue) {
    foreach ($v in Get-BitLockerVolume | Where-Object ProtectionStatus -eq 'On') {
        Write-Host "==> suspending BitLocker on $($v.MountPoint) (windows-post.ps1 resumes it)"
        Write-Host "    Recovery key - store it somewhere safe:"
        $v.KeyProtector | Where-Object KeyProtectorType -eq 'RecoveryPassword' |
            ForEach-Object { Write-Host "    $($_.RecoveryPassword)" }
        Suspend-BitLocker -MountPoint $v.MountPoint -RebootCount 0 | Out-Null
    }
}

# 3. VirtIO drivers -------------------------------------------------------------
if ($VirtioInstaller) {
    Write-Host "==> installing VirtIO drivers + guest agent from $VirtioInstaller"
    $p = Start-Process -FilePath $VirtioInstaller -ArgumentList '/install', '/passive', '/norestart' -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) { throw "virtio installer exited with $($p.ExitCode)" }
    Write-Host "    You can import this VM with: vmware2pve.sh import <vmx> --virtio-ready"
}

# 4. VMware Tools (last: removes the vmxnet3 driver) -----------------------------
if ($RemoveVmwareTools) {
    $tools = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' |
             Where-Object DisplayName -eq 'VMware Tools'
    if ($tools) {
        Write-Host "==> uninstalling VMware Tools"
        Start-Process msiexec.exe -ArgumentList '/x', $tools.PSChildName, '/qn', '/norestart' -Wait
    }
}

if ($Shutdown) { shutdown.exe /s /t 5 }   # full shutdown, not a Fast Startup hibernate
else { Write-Host "==> done. Shut down with: shutdown /s /t 0" }
