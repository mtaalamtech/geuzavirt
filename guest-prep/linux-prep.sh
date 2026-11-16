#!/usr/bin/env bash
# linux-prep.sh - run as root INSIDE a Linux guest while it is still on VMware,
# then shut it down and import it with vmware2pve.sh.
#
#   - adds VirtIO drivers to the initramfs (dracut's host-only mode omits them)
#   - pins NIC names to their MACs so ens192/ens160 keep their names on KVM
#   - installs qemu-guest-agent
#   - makes UEFI installs bootable from the removable path that fresh OVMF uses
#   - warns about fstab entries that will change name
#
# Usage: linux-prep.sh [--remove-vmtools]
set -euo pipefail

REMOVE_VMTOOLS=0
[[ ${1:-} == --remove-vmtools ]] && REMOVE_VMTOOLS=1
[[ $EUID -eq 0 ]] || { echo "run as root" >&2; exit 1; }

info() { echo "==> $*"; }
warn() { echo "warning: $*" >&2; }
# storage drivers only: virtio_net must load from the real root so the .link
# files below decide the NIC name (dracut wouldn't carry them into the initramfs)
MODS="virtio_pci virtio_scsi virtio_blk"

pkg_install() {
  if   command -v apt-get >/dev/null; then apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y "$@"
  elif command -v dnf     >/dev/null; then dnf install -y "$@"
  elif command -v yum     >/dev/null; then yum install -y "$@"
  elif command -v zypper  >/dev/null; then zypper --non-interactive install "$@"
  elif command -v pacman  >/dev/null; then pacman -S --noconfirm --needed "$@"
  else return 1; fi
}
pkg_remove() {
  if   command -v apt-get >/dev/null; then apt-get purge -y "$@"
  elif command -v dnf     >/dev/null; then dnf remove -y "$@"
  elif command -v yum     >/dev/null; then yum remove -y "$@"
  elif command -v zypper  >/dev/null; then zypper --non-interactive remove "$@"
  elif command -v pacman  >/dev/null; then pacman -Rns --noconfirm "$@"
  fi
}

# --- 1. guest agent -----------------------------------------------------------
info "installing qemu-guest-agent"
pkg_install qemu-guest-agent || warn "could not install qemu-guest-agent (no network?) - install it after migration"
systemctl enable qemu-guest-agent >/dev/null 2>&1 || true

# --- 2. NIC names pinned to MAC (vmware2pve.sh keeps the MACs) ------------------
for dev in /sys/class/net/*; do
  [[ -e $dev/device ]] || continue          # physical (virtual hw) NICs only
  ifname=${dev##*/}
  mac=$(<"$dev/address")
  link=/etc/systemd/network/10-vmware2pve-$ifname.link
  info "pinning $ifname to $mac ($link)"
  mkdir -p /etc/systemd/network
  printf '[Match]\nMACAddress=%s\n\n[Link]\nName=%s\n' "$mac" "$ifname" > "$link"
done

# --- 3. VirtIO in initramfs (written after the .link files so they are included) -
if command -v update-initramfs >/dev/null; then
  info "adding VirtIO modules to initramfs-tools"
  for m in $MODS; do grep -qx "$m" /etc/initramfs-tools/modules || echo "$m" >> /etc/initramfs-tools/modules; done
  update-initramfs -u -k all
elif command -v dracut >/dev/null; then
  info "adding VirtIO drivers to dracut"
  echo "add_drivers+=\" $MODS \"" > /etc/dracut.conf.d/90-virtio.conf
  dracut -f --regenerate-all
elif command -v mkinitcpio >/dev/null; then
  info "adding VirtIO modules to mkinitcpio"
  mkdir -p /etc/mkinitcpio.conf.d
  echo "MODULES+=($MODS)" > /etc/mkinitcpio.conf.d/virtio.conf
  mkinitcpio -P
else
  warn "unknown initramfs tool - make sure $MODS are in the initramfs"
fi

# --- 4. UEFI fallback boot path -------------------------------------------------
if [[ -d /sys/firmware/efi ]]; then
  esp=$(findmnt -n -o TARGET /boot/efi 2>/dev/null || findmnt -n -o TARGET /efi 2>/dev/null || true)
  if [[ -n $esp ]] && ! compgen -G "$esp/EFI/[Bb][Oo][Oo][Tt]/[Bb][Oo][Oo][Tt][Xx]64.[Ee][Ff][Ii]" >/dev/null; then
    if command -v debconf-set-selections >/dev/null && dpkg -s grub-efi-amd64 >/dev/null 2>&1; then
      info "installing GRUB to the removable EFI path"
      echo "grub-efi-amd64 grub2/force_efi_extra_removable boolean true" | debconf-set-selections
      dpkg-reconfigure -f noninteractive grub-efi-amd64
    else
      warn "$esp/EFI/BOOT/BOOTX64.EFI missing - after import, add a boot entry in the OVMF menu (Esc at boot) or run 'grub-install --removable'"
    fi
  fi
fi

# --- 5. fstab sanity ----------------------------------------------------------
if grep -Eq '^[[:space:]]*/dev/(nvme|hd|vd)' /etc/fstab; then
  warn "/etc/fstab uses /dev/nvme*/hd*/vd* names - these become /dev/sdX on Proxmox. Switch them to UUID= (see blkid):"
  grep -E '^[[:space:]]*/dev/(nvme|hd|vd)' /etc/fstab >&2
fi

# --- 6. VMware tools ----------------------------------------------------------
if (( REMOVE_VMTOOLS )); then
  info "removing open-vm-tools"
  pkg_remove open-vm-tools open-vm-tools-desktop 2>/dev/null || true
fi

info "done - shut down with 'poweroff' and import with vmware2pve.sh"
