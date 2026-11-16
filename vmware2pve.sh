#!/usr/bin/env bash
# vmware2pve.sh - import VMware (Workstation / ESXi) VMs into Proxmox VE with
# KVM-tuned hardware: q35, VirtIO SCSI single + iothread, discard/TRIM,
# VirtIO NIC (original MAC kept), QEMU guest agent, OVMF/TPM when the source
# used them.
#
# Run as root on a Proxmox VE 8+ node. The VM folder (.vmx + .vmdk files) must
# be reachable from the node (copied over with scp, an NFS/SMB mount, ...).
set -euo pipefail

STORAGE=local-lvm BRIDGE=vmbr0 CPU=host FORMAT= VMID= NAME= CORES= MEMORY=
VIRTIO_READY=0 VIRTIO_ISO= NEW_MAC=0 START=0 DRY_RUN=0 PREALLOC=0 TEMPLATE=0
ESXI_URLS= ESXI_AUTH= STYPE=
declare -A DISKSIZE   # vmx slot (scsi0:0) -> requested size in bytes
declare -A VOLSIZE    # "slot@partition offset" -> requested NTFS volume size in bytes
RESCAN=0 SHRINK_DISKS=0
DUMMY_SLOT=scsi30   # temporary VirtIO disk that makes Windows install vioscsi
LINUX_SPEC= LINUX_KEY= LINUX_FROM= LINUX_PORT=22   # running Linux source (file-level copy)
declare -a LINUX_VOLS=()
declare -A L_PLAN L_FINAL   # Linux disk index -> partition plan (VOL_TOOL) / new disk size

usage() {
  cat <<'EOF'
Usage:
  vmware2pve.sh import <path/to/vm.vmx> [options]
  vmware2pve.sh finalize <vmid> [--shrink-disks] [--dry-run]
      Windows only: after the first boot on SATA (VirtIO drivers installed),
      move disks SATA -> VirtIO SCSI and NICs e1000e -> VirtIO.
      --shrink-disks: also cut each disk behind its last partition (after the
      copy's agent shrank the volumes with Windows at its first start).

Import options:
  --vmid N          VM ID (default: next free ID)
  --name NAME       VM name (default: displayName from the .vmx)
  --cores N         vCPU cores (default: numvcpus from the .vmx)
  --memory MB       memory in MiB (default: memsize from the .vmx)
  --storage ID      target storage for disks (default: local-lvm)
  --bridge BR       network bridge for all NICs (default: vmbr0)
  --cpu TYPE        CPU type (default: host; use x86-64-v2-AES or similar in
                    clusters with mixed CPU generations for live migration)
  --format FMT      raw|qcow2 (default: qcow2 on file storage, raw otherwise)
  --preallocate     thick disks: allocate the full disk size on the storage
                    (default: thin, only the space the guest has used)
  --esxi-urls FILE  "name<TAB>url" lines for disk extents that are still on the
                    ESXi datastore; a disk whose descriptor has one flat extent
                    listed there is streamed straight into its Proxmox volume
  --esxi-auth FILE  curl config file with the ESXi credentials (user = "u:p")
  --disk-size SLOT=BYTES
                    new size for the disk in that .vmx slot (e.g. scsi0:0=64424509440);
                    repeatable. Growing always works (extend the volume in the guest
                    afterwards); shrinking only down to the end of the last partition
  --volume-size SLOT@OFFSET=BYTES
                    live copies: new size of the volume whose partition starts at byte
                    OFFSET of that disk (repeatable); the partitions behind it move and
                    the Proxmox disk is created at the size the new layout needs - like
                    VMware Converter's volume-based copy.
                    Windows (agent 1.4+, --esxi-urls lines "NAME#head" and "NAME@OFFSET"):
                    the volume is created new (mkntfs) and its files are copied into it
                    (wimlib, package wimtools). Older agents: the volume is copied block by
                    block and shrunk with ntfsresize. Linux: ext2/3/4, XFS, swap and LVM
                    physical volumes are created at the new size.
  --virtio-ready    Windows guest already has VirtIO drivers installed:
                    import straight onto VirtIO SCSI/NIC (no finalize step)
  --virtio-iso VOL  virtio-win ISO to attach for Windows guests, e.g.
                    local:iso/virtio-win.iso (default: auto-detect)
  --new-mac         generate new MAC addresses instead of keeping VMware's
  --start           start the VM after import
  --template        convert the imported VM into a Proxmox template
  --dry-run         print the qm commands without running them

  --esxi-urls lines may carry a third field "mtv2v:KEYHEX:IVHEX": the URL is then
  a Mtaalam V2V Agent hot-clone stream (AES-256-CTR, offset/data records) of a
  running machine, written straight into the new volume.

  --linux-spec FILE --linux-key KEY --linux-from root@HOST [--linux-port N]
                    running Linux machine: FILE describes its disks (written by the
                    converter); the disks of the .vmx are created empty, then the
                    partition tables, LVM and file systems are recreated with the
                    source's UUIDs and the files are copied with rsync over SSH.

  vmware2pve.sh restore --from root@HOST --key KEY --source-vmid N [--port N]
                [--total BYTES] [--zstd] [import options]
      Copies a VM from another Proxmox VE host: its vzdump stream (forced command
      of KEY on that host) is restored here with qmrestore.
EOF
}

die()  { DIED=1; echo "error: $*" >&2; exit 1; }
warn() { echo "warning: $*" >&2; }
info() { echo "==> $*"; }
run()  { printf '+'; printf ' %q' "$@"; echo; if (( ! DRY_RUN )); then "$@"; fi; }

declare -A VMX
parse_vmx() {
  local line
  while IFS= read -r line || [[ -n $line ]]; do
    line=${line%$'\r'}
    [[ $line =~ ^[[:space:]]*([^#=[:space:]]+)[[:space:]]*=[[:space:]]*\"(.*)\"[[:space:]]*$ ]] || continue
    VMX[${BASH_REMATCH[1],,}]=${BASH_REMATCH[2]}
  done < "$1"
}
vmx()     { printf '%s' "${VMX[${1,,}]-}"; }
is_true() { [[ ${1,,} == true ]]; }

# VMware guestOS id -> Proxmox ostype (drives Hyper-V enlightenments, timers, ...)
map_ostype() {
  case $1 in
    windows11*|windows2019srvnext*|windows2022srvnext*|windows2025*) echo win11 ;;
    windows9*|windows10*|windows2016*|windows2019*)                   echo win10 ;;
    windows8*|windows2012*|winserver2012*)                            echo win8 ;;
    windows7*|windows2008r2*|winserver2008r2*)                        echo win7 ;;
    longhorn*|winserver2008*)                                         echo w2k8 ;;
    winvista*)                                                        echo wvista ;;
    winnet*)                                                          echo w2k3 ;;
    winxp*)                                                           echo wxp ;;
    win*)                                                             echo win10 ;;
    *solaris*)                                                        echo solaris ;;
    freebsd*|darwin*|netware*)                                        echo other ;;
    *)                                                                echo l26 ;;
  esac
}

find_virtio_iso() {
  local s
  for s in $(pvesm status --content iso 2>/dev/null | awk 'NR>1{print $1}'); do
    pvesm list "$s" --content iso 2>/dev/null | awk '$1 ~ /virtio-win/ {print $1}'
  done | sort -V | tail -n1
}

import_vm() {
  local vmx_path=$1
  check_other_runs "$VMID"
  [[ -f $vmx_path ]] || die "no such file: $vmx_path"
  local vmx_dir; vmx_dir=$(cd "$(dirname "$vmx_path")" && pwd)
  parse_vmx "$vmx_path"

  # --- guest type -----------------------------------------------------------
  local guest ostype is_windows=0 virtio=1
  guest=$(vmx guestos); guest=${guest,,}
  ostype=$(map_ostype "$guest")
  [[ $ostype == w* ]] && is_windows=1
  if (( is_windows && ! VIRTIO_READY )); then virtio=0; fi
  info "guest '$guest' -> ostype $ostype"

  # --- disks ----------------------------------------------------------------
  local -a disks=()
  local key slot file
  for key in "${!VMX[@]}"; do
    [[ $key =~ ^((scsi|sata|nvme|ide)[0-9]+:[0-9]+)\.filename$ ]] || continue
    slot=${BASH_REMATCH[1]}
    [[ ${VMX[$slot.present]-true} == [Ff][Aa][Ll][Ss][Ee] ]] && continue
    [[ ${VMX[$slot.devicetype]-} == *[Cc][Dd][Rr][Oo][Mm]* ]] && continue
    file=${VMX[$key]}
    [[ ${file,,} == *.vmdk ]] || continue
    [[ $file == /* ]] || file=$vmx_dir/$file
    [[ -f $file ]] || file=$vmx_dir/${file##*/}   # ESXi /vmfs/volumes/... paths
    [[ -f $file ]] || die "disk $slot not found: ${VMX[$key]}"
    [[ $file != *,* ]] || die "disk path contains a comma, rename it: $file"
    disks+=("$slot"$'\t'"$file")
  done
  (( ${#disks[@]} )) || die "no virtual disks found in $vmx_path"

  # boot disk (bios.hddOrder) first, the rest in controller order
  local boot; boot=$(vmx bios.hddorder); boot=${boot%%,*}; boot=${boot,,}
  mapfile -t disks < <(printf '%s\n' "${disks[@]}" | sort -V |
    awk -F'\t' -v b="$boot" '$1==b{print; next} {r[++n]=$0} END{for(i=1;i<=n;i++) print r[i]}')

  if (( ! virtio && ${#disks[@]} > 6 )); then
    die "more than 6 disks don't fit on SATA; install VirtIO drivers in the guest and use --virtio-ready"
  fi
  if compgen -G "$vmx_dir/*.lck" >/dev/null; then
    warn "lock files (*.lck) found - make sure the VM is shut down in VMware"
  fi
  [[ -z $(vmx encryption.keysafe) ]] || warn "VM uses VMware encryption; if disks are encrypted, remove encryption in VMware first"

  local d
  for d in "${disks[@]}"; do
    file=${d#*$'\t'}
    if [[ -n $LINUX_SPEC ]]; then continue; fi               # files come from the running machine
    if stream_source "$file" >/dev/null; then continue; fi   # data still on ESXi
    qemu-img info "$file" >/dev/null 2>&1 ||
      die "qemu-img cannot read $file (encrypted, missing -flat/-sNNN extent files, or still locked by VMware)"
    if [[ ${file##*/} =~ -[0-9]{6}\.vmdk$ ]]; then
      warn "${file##*/} is a snapshot delta - the chain will be flattened; consolidating snapshots in VMware first is cleaner"
    fi
  done

  # --- target ---------------------------------------------------------------
  local stype
  stype=$(pvesm status --storage "$STORAGE" 2>/dev/null | awk 'NR==2{print $2}')
  STYPE=$stype
  [[ -n $stype ]] || die "storage '$STORAGE' not found (see: pvesm status)"
  case $stype in
    dir|nfs|cifs|glusterfs) FORMAT=${FORMAT:-qcow2} ;;
    *) [[ ${FORMAT:-raw} == raw ]] || die "storage type $stype only supports raw"; FORMAT=raw ;;
  esac
  if [[ -n $LINUX_SPEC ]]; then
    case $stype in dir|nfs|cifs|lvm|lvmthin|zfspool) FORMAT=raw ;;
      *) die "storage type $stype cannot receive a copy of a running Linux machine (use LVM-thin, ZFS, LVM or a directory storage)" ;; esac
    [[ -f $LINUX_SPEC && -f $LINUX_KEY && -n $LINUX_FROM ]] || die "--linux-spec, --linux-key and --linux-from are needed together"
    linux_check_host
    local -a lorder=()
    read -r -a lorder <<<"$(lval order)"
    local li=0
    for d in "${disks[@]}"; do
      linux_plan "$li" "${d%%$'\t'*}" "${lorder[li]-}"
      li=$((li + 1))
    done
  fi

  VMID=${VMID:-$(pvesh get /cluster/nextid)}
  if qm config "$VMID" >/dev/null 2>&1; then die "VMID $VMID already exists"; fi

  local name=${NAME:-$(vmx displayname)}
  name=$(sed -E 's/[^A-Za-z0-9.-]+/-/g; s/^-+//; s/-+$//' <<<"$name")
  name=${name:-imported-vm}

  local vcpus mem
  vcpus=${CORES:-$(vmx numvcpus)}; vcpus=${vcpus:-1}
  mem=${MEMORY:-$(vmx memsize)};   mem=${mem:-1024}

  # Windows: pin the machine version so QEMU upgrades never change the
  # virtual hardware under it (avoids re-detected NICs / reactivation).
  local machine=q35
  if (( is_windows )); then
    machine=$(kvm -machine help | awk '/^pc-q35-[0-9]+\.[0-9]+ /{print $1}' | sort -V | tail -n1)
    machine=${machine:-q35}
  fi

  # Single socket, N cores: same performance as VMware's sockets x cores
  # layout, and avoids the 2-socket limit of Windows client editions.
  local -a create=(qm create "$VMID" --name "$name" --ostype "$ostype"
    --machine "$machine" --cpu "$CPU" --sockets 1 --cores "$vcpus" --memory "$mem"
    --scsihw virtio-scsi-single --agent enabled=1,fstrim_cloned_disks=1)

  if [[ $(vmx firmware) == [Ee][Ff][Ii] ]]; then
    local keys=0
    if is_true "$(vmx uefi.secureboot.enabled)"; then keys=1; fi
    create+=(--bios ovmf --efidisk0 "$STORAGE:1,efitype=4m,pre-enrolled-keys=$keys")
  fi
  if is_true "$(vmx vtpm.present)"; then
    create+=(--tpmstate0 "$STORAGE:1,version=v2.0")
  fi

  local n=0 i mac model=virtio
  (( virtio )) || model=e1000e
  for i in $(printf '%s\n' "${!VMX[@]}" | sed -nE 's/^ethernet([0-9]+)\.present$/\1/p' | sort -n); do
    is_true "${VMX[ethernet$i.present]}" || continue
    mac=${VMX[ethernet$i.address]:-${VMX[ethernet$i.generatedaddress]:-}}
    if (( NEW_MAC )) || [[ -z $mac ]]; then
      create+=(--net$n "$model,bridge=$BRIDGE")
    else
      create+=(--net$n "$model=${mac^^},bridge=$BRIDGE")
    fi
    n=$((n+1))
  done
  if (( n == 0 )); then warn "no network adapters in the .vmx"; fi
  if (( n > 1 )); then warn "all $n NICs were put on $BRIDGE - adjust bridges/VLAN tags afterwards"; fi

  if (( is_windows )); then
    local iso=${VIRTIO_ISO:-$(find_virtio_iso || true)}
    if [[ -n $iso ]]; then
      create+=(--ide2 "$iso,media=cdrom")
    else
      warn "no virtio-win ISO found - upload one to an ISO storage (https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/stable-virtio/virtio-win.iso)"
    fi
  fi

  # --- create + import --------------------------------------------------------
  local alloc=thin; (( PREALLOC )) && alloc=thick
  info "creating VM $VMID ($name): $vcpus vCPU, $mem MiB, disks -> $STORAGE ($FORMAT, $alloc)"
  run "${create[@]}"
  trap 'warn "import failed - remove the partial VM with: qm destroy '"$VMID"' --purge"' ERR

  local idx=0 bus opts bootdisk=
  for d in "${disks[@]}"; do
    file=${d#*$'\t'}
    if (( virtio )); then bus=scsi$idx; opts=iothread=1,discard=on,ssd=1
    else                  bus=sata$idx; opts=discard=on,ssd=1; fi
    slot=${d%%$'\t'*}
    info "importing $slot ($file) -> $bus"
    local src cur s_sectors s_url s_crypt
    if [[ -n $LINUX_SPEC ]]; then
      s_sectors=$(sed -nE 's/^RW ([0-9]+) .*/\1/p' "$file" | head -n1)
      [[ -n $s_sectors ]] || die "cannot read the size of $slot from $file"
      LINUX_VOLS+=("$(alloc_disk "$bus" "${L_FINAL[$idx]:-$(( s_sectors * 512 ))}" raw "$opts")")
    elif src=$(stream_source "$file"); then
      IFS=$'\t' read -r s_sectors s_url s_crypt <<<"$src"
      if [[ -n $s_crypt ]]; then
        stream_agent "$slot" "$bus" "$s_sectors" "$s_url" "$s_crypt" "$opts"
      else
        stream_disk "$slot" "$bus" "$s_sectors" "$s_url" "$opts"
      fi
    else
      cur=$(disk_bytes "$file")
      if (( PREALLOC )) || [[ -n ${DISKSIZE[$slot]-} ]]; then
        import_alloc "$slot" "$bus" "$file" "$opts" "$cur"
      else
        run qm set "$VMID" --"$bus" "$STORAGE:0,import-from=$file,format=$FORMAT,$opts"
      fi
    fi
    bootdisk=${bootdisk:-$bus}
    idx=$((idx+1))
  done

  if [[ -n $LINUX_SPEC ]] && (( ! DRY_RUN )); then linux_fill; fi
  if (( RESCAN )); then
    qm rescan --vmid "$VMID" >/dev/null 2>&1 || warn "qm rescan failed - the disk size shown in Proxmox may be out of date"
  fi
  if (( ! virtio )); then
    run qm set "$VMID" --"$DUMMY_SLOT" "$STORAGE:1,iothread=1"
  fi
  run qm set "$VMID" --boot "order=$bootdisk"
  trap - ERR

  if (( TEMPLATE )); then
    run qm template "$VMID"
    info "VM $VMID converted to a template (clone it to deploy new VMs)"
  elif (( START )); then
    run qm start "$VMID"
  fi

  echo
  info "VM $VMID imported."
  if (( is_windows && ! virtio )); then
    cat <<EOF
Next steps (Windows):
  1. Boot VM $VMID, open the virtio-win CD and run virtio-win-guest-tools.exe
     (installs all VirtIO drivers + QEMU guest agent). Then shut down.
  2. On this node:  $0 finalize $VMID
  3. Boot again and run guest-prep/windows-post.ps1 as Administrator.
EOF
  elif (( is_windows )); then
    echo "Next: boot VM $VMID and run guest-prep/windows-post.ps1 as Administrator."
  else
    echo "Next: boot VM $VMID; check 'systemctl status qemu-guest-agent' and networking."
  fi
}

disk_bytes() {
  local bytes
  bytes=$(qemu-img info "$1" | sed -n 's/^virtual size: .*(\([0-9]*\) bytes).*/\1/p' | head -n1)
  [[ -n $bytes ]] || die "cannot read the disk size of $1"
  echo "$bytes"
}

gib() { awk -v b="$1" 'BEGIN { printf "%.10f", int((b + 1048575) / 1048576) / 1024 }'; }
gb()  { awk -v b="$1" 'BEGIN { printf "%.1f GiB", b / 1073741824 }'; }

# Allocates a new volume on $STORAGE for $bus; prints its volume ID.
alloc_disk() {
  local bus=$1 bytes=$2 format=$3 opts=$4
  run qm set "$VMID" --"$bus" "$STORAGE:$(gib "$bytes"),format=$format,$opts" >&2
  (( DRY_RUN )) && return
  qm config "$VMID" | sed -n "s/^$bus: \([^,]*\),.*/\1/p"
}

# Reads the MBR/GPT in the first MiB of a disk (stdin) and prints the bytes
# needed to keep every partition (plus the backup GPT), or 0 if none found.
part_end() {
  perl -e '
    binmode STDIN; local $/; my $d = <STDIN>; my ($max, $gpt) = (0, 0);
    if (length($d) < 1024 || substr($d, 510, 2) ne "\x55\xAA") { print 0; exit }
    for my $i (0 .. 3) {
      my ($type, $start, $n) = unpack("x4 C x3 V V", substr($d, 446 + 16 * $i, 16));
      next unless $type;
      if ($type == 0xEE) { $gpt = 1; next }
      $max = $start + $n if $start + $n > $max;
    }
    if ($gpt && substr($d, 512, 8) eq "EFI PART") {
      my ($lba, $count, $size) = unpack("Q< V V", substr($d, 512 + 72, 16));
      for my $i (0 .. $count - 1) {
        my $o = $lba * 512 + $i * $size;
        last if $o + $size > length($d);
        my $e = substr($d, $o, $size);
        next if substr($e, 0, 16) eq "\0" x 16;
        my $last = unpack("Q<", substr($e, 40, 8));
        $max = $last + 1 if $last + 1 > $max;
      }
      $max += 33;
    }
    print $max * 512;'
}

is_gpt() { [[ $(dd if="$1" bs=1 skip=512 count=8 status=none 2>/dev/null) == "EFI PART" ]]; }

# Size the new disk gets: the requested --disk-size, but never cutting into a
# partition ($3 = file with the disk's first MiB). Prints bytes.
target_size() {
  local slot=$1 cur=$2 head=$3 want need
  want=${DISKSIZE[$slot]:-0}
  if (( want <= 0 )); then echo "$cur"; return; fi
  want=$(( (want + 1048575) / 1048576 * 1048576 ))
  if (( want >= cur )); then echo "$want"; return; fi
  need=$(part_end < "$head")
  if (( need == 0 )); then
    warn "$slot: no partition table found, so it cannot be shrunk safely - keeping $(gb "$cur")"
    echo "$cur"; return
  fi
  if (( want < need )); then
    warn "$slot: its partitions reach $(gb "$need") - shrink the volume inside the guest first. Keeping $(gb "$cur")"
    echo "$cur"; return
  fi
  info "$slot: resizing $(gb "$cur") -> $(gb "$want") (partitions end at $(gb "$need"))" >&2
  echo "$want"
}

# After a resize, move the backup GPT to the new end of the disk (raw only).
fix_gpt() {
  local path=$1 head=$2
  [[ -b $path || -f $path ]] || return 0
  if is_gpt "$head" && command -v sgdisk >/dev/null; then
    sgdisk -e "$path" >/dev/null 2>&1 || warn "could not move the backup GPT on $path (the guest will repair it)"
  fi
}

# Import into a volume allocated by us: thick (every block written, -S 0)
# and/or resized (--disk-size).
import_alloc() {
  local slot=$1 bus=$2 src=$3 opts=$4 cur=$5 tgt vol path head sparse=()
  head=$(mktemp)
  qemu-img dd -f vmdk -O raw bs=1M count=1 if="$src" of="$head" 2>/dev/null || : > "$head"
  tgt=$(target_size "$slot" "$cur" "$head")
  vol=$(alloc_disk "$bus" "$tgt" "$FORMAT" "$opts")
  if (( DRY_RUN )); then rm -f "$head"; return; fi
  path=$(pvesm path "$vol")
  (( PREALLOC )) && sparse=(-S 0)
  if (( tgt < cur )); then
    # raw view of only the first $tgt bytes of the source
    qemu-img convert -p -n "${sparse[@]}" --image-opts "driver=raw,size=$tgt,file.driver=vmdk,file.file.filename=$src" -O "$FORMAT" "$path"
  else
    qemu-img convert -p -n "${sparse[@]}" -f vmdk -O "$FORMAT" "$src" "$path"
  fi
  echo
  if (( tgt != cur )) && [[ $FORMAT == raw ]]; then fix_gpt "$path" "$head"; fi
  rm -f "$head"
}

# Prints "sectors<TAB>url" when descriptor $1 has a single flat extent that is
# not on this host but listed in ESXI_URLS (and no snapshot parent).
stream_source() {
  local desc=$1 line n=0 sectors= name= url
  [[ -n $ESXI_URLS && -f $desc ]] || return 1
  (( $(stat -c %s "$desc") < 1048576 )) || return 1
  if grep -qi '^[[:space:]]*parentFileNameHint' "$desc"; then return 1; fi
  while IFS= read -r line || [[ -n $line ]]; do
    line=${line%$'\r'}
    if [[ $line =~ ^RW[[:space:]]+([0-9]+)[[:space:]]+(VMFS|FLAT)[[:space:]]+\"([^\"]+)\" ]]; then
      n=$((n+1)); sectors=${BASH_REMATCH[1]}; name=${BASH_REMATCH[3]##*/}
    elif [[ $line =~ ^(RW|RDONLY|NOACCESS)[[:space:]] ]]; then
      return 1
    fi
  done < "$desc"
  (( n == 1 )) || return 1
  [[ ! -f $(dirname "$desc")/$name ]] || return 1
  url=$(awk -F'\t' -v n="$name" '$1 == n { print $2 "\t" $3; exit }' "$ESXI_URLS")
  [[ -n ${url%$'\t'} ]] || return 1
  printf '%s\t%s\n' "$sectors" "$url"
}

# Decoder for the agent's disk stream (after decryption): "MTV2VDSK" header, then
# (u64 offset, u32 length, u32 flags, data) records, ended by a record of length 0.
# Args: target path, bytes to keep, 1 = write zeros into the gaps (thick / LVM).
AGENT_DECODER='
use strict; use warnings;
use IO::Handle;
$| = 1;
my ($path, $limit, $zero) = @ARGV;
open(my $out, "+<", $path) or die "cannot open $path: $!\n";
binmode $out; binmode STDIN;
sub rd {
  my $n = shift; my $b = "";
  while (length($b) < $n) {
    my $r = read(STDIN, $b, $n - length($b), length($b));
    die "the stream from the agent ended early (connection lost or agent stopped)\n" unless $r;
  }
  return $b;
}
# AGENT_MAP "start:len:newstart:newlen:files,..." (bytes): the partitions land at their new
# place on a disk with a new layout; data outside them (old backup GPT) is dropped. files=1:
# the volume gets a new file system, so it needs no zeros (only its boot sector comes here).
my @map = sort { $a->[2] <=> $b->[2] } grep { @$_ == 5 } map { [split /:/] } split /,/, ($ENV{AGENT_MAP} // "");
my $first = @map ? (sort { $a <=> $b } map { $_->[0] } @map)[0] : 0;
my $zeros = "\0" x 1048576;
sub fill0 {
  my ($from, $to) = @_;
  while ($from < $to) {
    my $n = $to - $from; $n = 1048576 if $n > 1048576;
    sysseek($out, $from, 0) or die "seek failed: $!\n";
    my $w = syswrite($out, $zeros, $n); die "write failed: $!\n" unless defined $w && $w == $n;
    $from += $n;
  }
}
sub fill {
  my ($from, $to) = @_;
  for my $m (@map) {
    next unless $m->[4];
    my ($a, $b) = ($m->[2] + 8192, $m->[2] + $m->[3]);
    if ($from < $b && $to > $a) { fill0($from, $a) if $from < $a; $from = $b; }
  }
  fill0($from, $to) if $from < $to;
}
my $pos = 0;
my $unsynced = 0;
sub put {
  my ($o, $d) = @_;
  return if $o >= $limit || length($d) == 0;
  $d = substr($d, 0, $limit - $o) if $o + length($d) > $limit;
  fill($pos, $o) if $zero && $o > $pos;
  sysseek($out, $o, 0) or die "seek failed: $!\n";
  my $w = syswrite($out, $d); die "write failed: $!\n" unless defined $w && $w == length($d);
  $pos = $o + length($d) if $o + length($d) > $pos;
  # flush every GiB: the progress shows what is on the disk, and a slow disk cannot pile up
  # gigabytes of pending writes (the host has 20% of its RAM for them)
  $unsynced += length($d);
  if ($unsynced >= 1073741824) { $out->sync; $unsynced = 0; }
}
my $h = rd(24);
die "not a Mtaalam V2V disk stream (wrong pairing key?)\n" unless substr($h, 0, 8) eq "MTV2VDSK";
my $size = unpack("Q<", substr($h, 16, 8));
my ($next, $got) = (time + 2, 0);
while (1) {
  my ($off, $len, $flags) = unpack("Q< V V", rd(16));
  last if $len == 0;
  my $d = rd($len);
  $got += $len;
  if (!@map) { put($off, $d); }
  else {
    my $end = $off + $len;
    put($off, substr($d, 0, ($end < $first ? $end : $first) - $off)) if $off < $first;   # MBR / GPT
    for my $m (@map) {
      my ($s, $l, $ns, $nl) = @$m;
      my $e = $s + ($l < $nl ? $l : $nl);
      my $a = $off > $s ? $off : $s;
      my $b = $end < $e ? $end : $e;
      put($ns + $a - $s, substr($d, $a - $off, $b - $a)) if $a < $b;
    }
  }
  if (time >= $next) { printf "transferred %d of %d bytes (%.2f%%) received %d\n", $off, $size, 100 * $off / $size, $got; $next = time + 2; }
}
fill($pos, $limit) if $zero && $pos < $limit;
printf "transferred %d of %d bytes (100.00%%) received %d\n", $size, $size, $got;
close($out) or die "closing $path failed: $!\n";
'

# Volume-based resizing (like VMware Converter): partition table work for resize_volumes.
#   plan  PATH ALLOC OFFSET=BYTES...  reads the table, prints "disk STYLE FINAL" and
#                                    "part IDX START LEN NSTART NLEN resize|keep" (bytes)
#   move  PATH < plan                moves partitions to NSTART (overlap-safe order/direction)
#   write PATH FINAL < plan          rewrites the GPT (both copies) or MBR for the new layout
VOL_TOOL='
use strict; use warnings;
use Compress::Zlib qw(crc32);
$| = 1;
my $MiB = 1048576;
sub up { my ($v, $a) = @_; return int(($v + $a - 1) / $a) * $a; }
sub gb { return sprintf("%.1f GiB", $_[0] / 1073741824); }
sub rd_at {
  my ($fh, $off, $len) = @_; my $b = "";
  sysseek($fh, $off, 0) or die "seek failed: $!\n";
  while (length($b) < $len) {
    my $r = sysread($fh, $b, $len - length($b), length($b));
    die "read failed: $!\n" unless defined $r; last unless $r;
  }
  return $b;
}
sub wr_at {
  my ($fh, $off, $d) = @_;
  sysseek($fh, $off, 0) or die "seek failed: $!\n";
  my $w = syswrite($fh, $d); die "write failed: $!\n" unless defined $w && $w == length($d);
}
sub table {
  my $fh = shift; my @p;
  my $d = rd_at($fh, 0, 1024);
  die "no partition table found\n" unless length($d) == 1024 && substr($d, 510, 2) eq "\x55\xAA";
  if (substr($d, 512, 8) eq "EFI PART") {
    my ($lba, $count, $size) = unpack("Q< V V", substr($d, 512 + 72, 16));
    my $e = rd_at($fh, $lba * 512, $count * $size);
    for my $i (0 .. $count - 1) {
      my $x = substr($e, $i * $size, $size);
      next if substr($x, 0, 16) eq "\0" x 16;
      my ($first, $last) = unpack("Q< Q<", substr($x, 32, 16));
      push @p, { idx => $i, start => $first * 512, len => ($last - $first + 1) * 512, type => 0 };
    }
    return ("gpt", @p);
  }
  for my $i (0 .. 3) {
    my ($type, $start, $n) = unpack("x4 C x3 V V", substr($d, 446 + 16 * $i, 16));
    next unless $type;
    die "the disk has logical drives (extended partition)\n" if $type == 0x05 || $type == 0x0f || $type == 0x85;
    die "it is a dynamic disk\n" if $type == 0x42;
    die "its GPT header is damaged\n" if $type == 0xee;
    push @p, { idx => $i, start => $start * 512, len => $n * 512, type => $type };
  }
  return ("mbr", @p);
}
sub read_plan {
  my @p;
  while (my $l = <STDIN>) {
    my @f = split " ", $l;
    push @p, { idx => $f[1], start => $f[2], len => $f[3], nstart => $f[4], nlen => $f[5] } if $f[0] eq "part";
  }
  return @p;
}
my $cmd = shift @ARGV;
my $path = shift @ARGV;
if ($cmd eq "plan") {
  my ($alloc, @specs) = @ARGV;
  open(my $fh, "<", $path) or die "cannot open $path: $!\n"; binmode $fh;
  my ($style, @p) = table($fh);
  @p = sort { $a->{start} <=> $b->{start} } @p;
  for my $s (@specs) {
    $s =~ /^(\d+)=(\d+)$/ or die "bad volume size \"$s\"\n";
    my ($off, $want) = ($1, $2);
    my ($q) = grep { $_->{start} == $off } @p;
    die "no partition starts at byte $off\n" unless $q;
    unless ($ENV{VOL_ANYFS}) {
      my $sig = substr(rd_at($fh, $off, 512) . ("\0" x 512), 3, 8);
      if ($sig ne "NTFS    ") {
        die "the boot sector of the volume at " . gb($off) . " did not arrive (empty)\n" if $sig eq "\0" x 8;
        die "the volume at " . gb($off) . " is BitLocker-encrypted on the disk (agent older than 1.4.1 - update it)\n" if $sig eq "-FVE-FS-";
        (my $show = $sig) =~ s/[^\x20-\x7e]/./g;
        die "the volume at " . gb($off) . " is not NTFS (its boot sector says \"$show\")\n";
      }
    }
    $q->{want} = up($want, $MiB);
  }
  my ($end, $changed) = (0, 0);
  for my $q (@p) {
    $q->{nstart} = $changed ? up($end, $MiB) : $q->{start};
    $q->{nlen} = defined $q->{want} ? $q->{want} : $q->{len};
    # Linux finds its file systems by UUID, Windows its MBR volumes by position
    if ($style eq "mbr" && !$ENV{VOL_ANYFS} && $q->{nstart} != $q->{start} && $q->{type} != 0x27) {
      die "on an MBR disk Windows finds its volumes by their position - only the last volume can be resized, " .
          "or a volume followed by a recovery partition\n";
    }
    $changed = 1 if $q->{nlen} != $q->{len} || $q->{nstart} != $q->{start};
    $end = $q->{nstart} + $q->{nlen};
  }
  my $final = up($end + ($style eq "gpt" ? 33 * 512 : 0), 4 * $MiB);
  die "the new layout needs " . gb($final) . " but the disk was created with " . gb($alloc) . "\n" if $final > $alloc;
  print "disk $style $final\n";
  printf "part %d %d %d %d %d %s\n", $_->{idx}, $_->{start}, $_->{len}, $_->{nstart}, $_->{nlen},
    defined $_->{want} && $_->{want} != $_->{len} ? "resize" : "keep" for @p;
  exit 0;
}
open(my $fh, "+<", $path) or die "cannot open $path: $!\n"; binmode $fh;
my @p = read_plan();
if ($cmd eq "move") {
  my @todo = grep { $_->{nstart} != $_->{start} } @p;
  # bytes that still hold data at the old place (a shrunk volume fits in its new size)
  $_->{copy} = $_->{len} < $_->{nlen} ? $_->{len} : $_->{nlen} for @p;
  while (@todo) {
    my $pick;
    for my $q (@todo) {
      my ($lo, $hi) = ($q->{nstart}, $q->{nstart} + $q->{copy});
      my $clash = grep { $_ != $q && $lo < $_->{start} + $_->{copy} && $_->{start} < $hi } @todo;
      if (!$clash) { $pick = $q; last; }
    }
    die "cannot find a safe order to move the partitions\n" unless $pick;
    my ($src, $dst, $len) = ($pick->{start}, $pick->{nstart}, $pick->{copy});
    printf "moving partition %d (%s) from %s to %s\n", $pick->{idx} + 1, gb($len), gb($src), gb($dst);
    my ($done, $next, $chunk) = (0, time + 10, 4 * $MiB);
    while ($done < $len) {
      my $n = $len - $done; $n = $chunk if $n > $chunk;
      my $o = $dst < $src ? $done : $len - $done - $n;
      my $d = rd_at($fh, $src + $o, $n);
      wr_at($fh, $dst + $o, $d) unless rd_at($fh, $dst + $o, $n) eq $d;
      $done += $n;
      if (time >= $next) { printf "  moved %s of %s\n", gb($done), gb($len); $next = time + 10; }
    }
    @todo = grep { $_ != $pick } @todo;
  }
  exit 0;
}
if ($cmd eq "write") {
  my $final = shift @ARGV;
  my $mbr = rd_at($fh, 0, 512);
  my $last = int($final / 512) - 1;
  if (substr(rd_at($fh, 512, 8), 0, 8) eq "EFI PART") {
    my $h = rd_at($fh, 512, 512);
    my $hsize = unpack("V", substr($h, 12, 4));
    my $oldbackup = unpack("Q<", substr($h, 32, 8));
    my ($lba, $count, $esize) = unpack("Q< V V", substr($h, 72, 16));
    my $e = rd_at($fh, $lba * 512, $count * $esize);
    for my $q (@p) {
      substr($e, $q->{idx} * $esize + 32, 16) = pack("Q< Q<", $q->{nstart} / 512, ($q->{nstart} + $q->{nlen}) / 512 - 1);
    }
    my $esec = up($count * $esize, 512) / 512;
    my $bent = $last - $esec;
    for my $q (@p) { die "partition " . ($q->{idx} + 1) . " does not fit on the disk\n" if ($q->{nstart} + $q->{nlen}) / 512 - 1 >= $bent; }
    substr($h, 32, 8) = pack("Q<", $last);
    substr($h, 48, 8) = pack("Q<", $bent - 1);
    substr($h, 88, 4) = pack("V", crc32($e));
    my $bk = $h;
    substr($bk, 24, 8) = pack("Q<", $last);
    substr($bk, 32, 8) = pack("Q<", 1);
    substr($bk, 72, 8) = pack("Q<", $bent);
    for my $x (\$h, \$bk) { substr($$x, 16, 4) = "\0" x 4; substr($$x, 16, 4) = pack("V", crc32(substr($$x, 0, $hsize))); }
    wr_at($fh, $lba * 512, $e);
    wr_at($fh, 512, $h);
    wr_at($fh, $bent * 512, $e);
    wr_at($fh, $last * 512, $bk);
    # the old backup header (disk grew) must not be found again
    if ($oldbackup != $last && $oldbackup < $last && !grep { $oldbackup * 512 >= $_->{nstart} && $oldbackup * 512 < $_->{nstart} + $_->{nlen} } @p) {
      wr_at($fh, $oldbackup * 512, "\0" x 512);
    }
    if (unpack("C", substr($mbr, 450, 1)) == 0xee) {
      wr_at($fh, 446 + 12, pack("V", $last > 0xFFFFFFFF ? 0xFFFFFFFF : $last));
    }
  } else {
    for my $q (@p) {
      next if $q->{nstart} == $q->{start} && $q->{nlen} == $q->{len};
      die "the volume is too large for an MBR disk\n" if ($q->{nstart} + $q->{nlen}) / 512 > 0xFFFFFFFF;
      my $o = 446 + 16 * $q->{idx};
      substr($mbr, $o + 1, 3) = "\xFE\xFF\xFF";
      substr($mbr, $o + 5, 3) = "\xFE\xFF\xFF";
      substr($mbr, $o + 8, 8) = pack("V V", $q->{nstart} / 512, $q->{nlen} / 512);
    }
    wr_at($fh, 0, $mbr);
  }
  close($fh) or die "closing $path failed: $!\n";
  exit 0;
}
die "unknown command $cmd\n";
'

# "slot@offset=bytes" entries of --volume-size for one disk, as "offset=bytes" words.
vol_specs() {
  local k
  for k in "${!VOLSIZE[@]}"; do
    [[ ${k%@*} == "$1" ]] && printf '%s=%s ' "${k#*@}" "${VOLSIZE[$k]}"
  done
  return 0
}

# Runs ntfsresize on the bytes $2..$2+$3 of $1 (loop device); $4 = new size, empty = fill.
ntfs_resize() {
  local path=$1 off=$2 len=$3 size=${4-} loop out rc=0
  local -a sz=()
  [[ -n $size ]] && sz=(-s "$size")
  out=$(mktemp)
  loop=$(losetup -f --show -o "$off" --sizelimit "$len" "$path") || { rm -f "$out"; return 1; }
  # dry run first: it refuses before changing anything (too full, inconsistent, ...)
  ntfsresize -n -f -f -P "${sz[@]}" "$loop" >"$out" 2>&1 || rc=1
  if (( ! rc )); then ntfsresize -f -f -P "${sz[@]}" "$loop" >"$out" 2>&1 || rc=1; fi
  if (( rc )); then
    grep -iE 'error|cannot|not enough|smaller|bigger|inconsistent|hibernat|bad|refus' "$out" | head -n4 | sed 's/^/  ntfsresize: /' >&2 || true
    # name the file ntfsresize could not handle (its $FILE_NAME is readable even when its data runs are not)
    local ino name
    ino=$(grep -oE 'in inode [0-9]+' "$out" | head -n1 | grep -oE '[0-9]+' || true)
    if [[ -n $ino ]] && command -v ntfsinfo >/dev/null; then
      name=$(ntfsinfo -f -i "$ino" "$loop" 2>/dev/null | sed -n 's/^Dumping Inode [0-9]* (\(.*\))$/\1/p' | head -n1 || true)
      echo "  the file ntfsresize cannot process: ${name:-inode $ino} (a heavily fragmented file whose cluster lists ntfs-3g cannot read)" >&2
    fi
  else
    grep -E '^(Relocating|Successfully)' "$out" | sed 's/^/  /' || true
  fi
  rm -f "$out"
  losetup -d "$loop" || true
  return $rc
}

# Resizes the NTFS volumes of the new disk $path (size $alloc) as
# requested, moves the partitions behind them and rewrites the partition table.
# Sets RESIZE_FINAL to the bytes the new disk needs; when the resize is not possible the
# layout stays as it is and RESIZE_FINAL is $5 (the original disk size). Every check runs
# first; until partitions are moved, a failure just keeps the original layout.
RESIZE_FINAL=0
resize_volumes() {
  local slot=$1 path=$2 alloc=$3 specs=$4 orig=$5 plan style final idx start len nstart nlen act line
  local -a parts=()
  RESIZE_FINAL=$orig
  if ! command -v ntfsresize >/dev/null; then
    warn "$slot: ntfsresize is missing (apt install ntfs-3g) - volumes NOT resized, the disk keeps $(gb "$orig")"; return 0
  fi
  # shellcheck disable=SC2086
  if ! plan=$(perl -e "$VOL_TOOL" plan "$path" "$alloc" $specs); then
    warn "$slot: volumes NOT resized (see the reason above) - the disk keeps $(gb "$orig")"; return 0
  fi
  read -r _ style final <<<"$(head -n1 <<<"$plan")"
  mapfile -t parts < <(grep '^part ' <<<"$plan")
  info "$slot: resizing volumes ($style disk, $(gb "$orig") -> $(gb "$final"))"
  for line in "${parts[@]}"; do
    read -r _ idx start len nstart nlen act <<<"$line"
    if [[ $act == resize ]]; then echo "  partition $((idx + 1)): $(gb "$len") -> $(gb "$nlen")"; fi
  done
  # 1. shrink file systems in place (Windows' NTFS stays valid in a larger partition)
  for line in "${parts[@]}"; do
    read -r _ idx start len nstart nlen act <<<"$line"
    if [[ $act != resize ]] || (( nlen >= len )); then continue; fi
    echo "  shrinking the file system of partition $((idx + 1)) (ntfsresize; this can take a while)"
    if ! ntfs_resize "$path" "$start" "$len" "$nlen"; then
      warn "$slot: ntfsresize could not shrink partition $((idx + 1)) to $(gb "$nlen") - volumes NOT resized, the disk keeps $(gb "$orig")"
      return 0
    fi
  done
  # 2. move partitions, 3. new partition table (point of no return: errors stop the import)
  perl -e "$VOL_TOOL" move "$path" <<<"$plan" || die "$slot: moving the partitions failed - the disk is incomplete"
  perl -e "$VOL_TOOL" write "$path" "$final" <<<"$plan" || die "$slot: writing the new partition table failed"
  # 4. grow file systems into their larger partitions
  for line in "${parts[@]}"; do
    read -r _ idx start len nstart nlen act <<<"$line"
    if [[ $act != resize ]] || (( nlen <= len )); then continue; fi
    ntfs_resize "$path" "$nstart" "$nlen" ||
      warn "$slot: ntfsresize could not grow partition $((idx + 1)) - extend it in Disk Management"
  done
  sync
  RESIZE_FINAL=$final
  info "$slot: volumes resized, the new disk is $(gb "$final") (Windows checks the resized volumes once at the first boot)"
}

# Makes the Proxmox volume $1 (path $2) $3 bytes long. Prints the size it has afterwards.
shrink_volume() {
  local vol=$1 path=$2 final=$3 cur
  cur=$(blockdev --getsize64 "$path" 2>/dev/null || stat -c %s "$path")
  if (( final >= cur )); then echo "$cur"; return; fi
  case $STYPE in
    lvm|lvmthin)  lvreduce -q -f -L "$(( final / 512 ))s" "$path" >/dev/null ;;
    zfspool)      zfs set volsize="$final" "${path#/dev/zvol/}" ;;
    dir|nfs|cifs) [[ $(qemu-img info "$path" 2>/dev/null | sed -n 's/^file format: //p') == raw ]] && truncate -s "$final" "$path" ;;
    *) false ;;
  esac || { warn "$vol: $STYPE volumes cannot be made smaller here - the disk keeps $(gb "$cur") (free space at its end)"; echo "$cur"; return; }
  echo "$final"
}

# Receives one disk stream of the agent into $1 (file or block device); $2 = bytes to keep,
# $3 = 1 to write zeros into the gaps.
# A loop device with direct I/O over block device $1 (else $1 itself). Buffered writes reach an
# LVM-thin volume as 4 KiB pieces, and each one into a new 64 KiB chunk makes the pool zero the
# chunk first: a test host (HP Smart Array) took them at 1.8 MB/s with an 18 GB backlog that held the
# LVM lock for hours. Through the loop device they arrive as large writes.
dio_open() {
  local dev=$1 loop
  if [[ -b $dev ]] && loop=$(losetup -f --show --direct-io=on "$dev" 2>/dev/null); then echo "$loop"; else echo "$dev"; fi
}
dio_close() {
  [[ $1 == /dev/loop* && $1 != "$2" ]] || return 0
  blockdev --flushbufs "$1" 2>/dev/null || true
  losetup -d "$1" 2>/dev/null || true
}

agent_receive() {
  local path=$1 limit=$2 zero=$3 url=$4 key=$5 iv=$6 slot=$7 out
  out=$(dio_open "$path")
  trap 'kill $(jobs -p) 2>/dev/null; exit 1' PIPE HUP TERM INT
  if ! curl -sS --fail --noproxy '*' --connect-timeout 30 --speed-time 600 --speed-limit 1 "$url" </dev/null |
       openssl enc -d -aes-256-ctr -K "$key" -iv "$iv" |
       perl -e "$AGENT_DECODER" "$out" "$limit" "$zero"; then
    dio_close "$out" "$path"
    die "copy of $slot from the running machine failed (is TCP 8786 on the machine reachable from this host?)"
  fi
  dio_close "$out" "$path"
  trap - PIPE HUP TERM INT
}

# "url<TAB>crypt<TAB>label" of an --esxi-urls line by its exact name; empty if missing.
agent_url() {
  [[ -n $ESXI_URLS ]] || return 0
  awk -F'\t' -v n="$1" '$1 == n { print $2 "\t" $3 "\t" $4; exit }' "$ESXI_URLS"
}

# wimlib-imagex apply output -> "transferred X of TOTAL bytes (P%)" every 2 s; errors and
# warnings pass through, the other progress lines are dropped.
WIM_FILTER='
$| = 1;
my %u = (B => 1, KiB => 1024, MiB => 1048576, GiB => 1073741824, TiB => 1099511627776);
my $next = 0;
while (my $l = <STDIN>) {
  chomp $l;
  if ($l =~ /file data:\s*([\d.]+)\s*(B|KiB|MiB|GiB|TiB) of ([\d.]+)\s*(B|KiB|MiB|GiB|TiB) \((\d+)%\)/) {
    next if time < $next && $5 < 100;
    printf "transferred %.0f of %.0f bytes (%d.00%%)\n", $1 * $u{$2}, $3 * $u{$4}, $5;
    $next = time + 2;
  } elsif ($l =~ /error|warning|cannot|failed|not enough|no space/i) {
    print "  wimlib: $l\n";
  }
}
'

# Puts the Windows boot code of the source volume (first 8 KiB, file $1) on the new NTFS
# file system $2, keeping its own BPB (size, MFT position) but the source volume serial.
VBR_TOOL='
use strict; use warnings;
my ($src, $dev) = @ARGV;
open(my $s, "<", $src) or die "cannot open $src: $!\n"; binmode $s;
read($s, my $old, 8192) == 8192 or die "short boot sector copy\n";
open(my $d, "+<", $dev) or die "cannot open $dev: $!\n"; binmode $d;
sysread($d, my $new, 8192) == 8192 or die "cannot read the new boot sector\n";
exit 0 unless substr($old, 3, 8) eq "NTFS    " && substr($new, 3, 8) eq "NTFS    " && substr($old, 510, 2) eq "\x55\xAA";
my $out = substr($old, 0, 0x0B) . substr($new, 0x0B, 0x54 - 0x0B) . substr($old, 0x54);
substr($out, 0x48, 8) = substr($old, 0x48, 8);
sysseek($d, 0, 0); syswrite($d, $out) == 8192 or die "writing the boot sector failed\n";
my $total = unpack("Q<", substr($new, 0x28, 8));
my $size = sysseek($d, 0, 2);
if ($total * 512 + 512 <= $size) { sysseek($d, $total * 512, 0); syswrite($d, substr($out, 0, 512)); }
close($d) or die "closing $dev failed: $!\n";
'

# dd status=progress -> a log line per minute; dd errors pass through. $ARGV[0] = total bytes.
DD_FILTER='
$| = 1;
my $next = time + 60;
while (my $l = <STDIN>) {
  chomp $l;
  next unless length $l;
  if ($l =~ /^(\d+) bytes/) {
    next if time < $next;
    printf "  written %.1f of %.1f GiB\n", $1 / 1073741824, $ARGV[0] / 1073741824;
    $next = time + 60;
  } elsif ($l !~ /records (in|out)/) {
    print "  dd: $l\n";
  }
}
'

# Free memory left to the host and its VMs while a volume is built in memory.
RAM_RESERVE=$(( 16 * 1024 * 1024 * 1024 ))

# The memory folder of this run. Named by process: the watchdog and later runs find it from that.
ram_dir() { echo "/run/mtv2v-ram.$$"; }

ram_release() {
  local d
  d=$(ram_dir)
  [[ -d $d ]] || return 0
  umount -l "$d" 2>/dev/null || true
  rmdir "$d" 2>/dev/null || true
  return 0
}

# Mounts a memory file system for a volume of $1 bytes; fails if the host cannot spare it. The
# space other conversions have not filled yet counts as taken, and folders of finished runs go.
ram_image() {
  local len=$1 dir d avail reserved=0 room
  dir=$(ram_dir)
  (
    flock 9
    for d in /run/mtv2v-ram.*; do
      [[ -d $d ]] || continue
      kill -0 "${d##*.}" 2>/dev/null && continue
      umount -l "$d" 2>/dev/null || true
      rmdir "$d" 2>/dev/null || true
    done
    # multiplied here, not in awk: mawk (the Debian default) prints large results as 1.32078e+11
    avail=$(awk '/^MemAvailable:/ { print $2; exit }' /proc/meminfo)
    avail=$(( ${avail:-0} * 1024 ))
    for d in /run/mtv2v-ram.*; do
      [[ -d $d ]] || continue
      room=$(df --output=avail -B1 "$d" 2>/dev/null | tail -n1) || continue
      reserved=$(( reserved + ${room:-0} ))
    done
    (( ${avail:-0} - reserved >= len + RAM_RESERVE )) || exit 1
    mkdir -m 700 "$dir" && mount -t tmpfs -o "size=$(( len + 1048576 )),mode=700" mtv2v-ram "$dir"
  ) 9>/run/mtv2v-ramlock || { rmdir "$dir" 2>/dev/null || true; return 1; }
  return 0
}

# Limits the share of the dirty-page budget that block device $1 may use (percent; 100 = none).
bdi_limit() {
  local d
  [[ -b $1 ]] || return 0
  d=$(basename "$(readlink -f "$1")")
  if [[ -w /sys/block/$d/bdi/max_ratio ]]; then echo "$2" > "/sys/block/$d/bdi/max_ratio" || true; fi
  return 0
}

# Creates partition $5 (bytes $7..$7+$8 of disk $2) as a new NTFS file system with the source
# volume's cluster size and geometry, then writes the files of the source volume into it
# (pipable WIM from the agent -> wimlib). $3 = file with the source boot sectors, $6 = old start.
ntfs_from_files() {
  local slot=$1 path=$2 head=$3 name=$4 idx=$5 start=$6 nstart=$7 nlen=$8 zero=$9 loop vbr u c label tag key iv
  local bps spc spt heads cluster rc=0
  IFS=$'\t' read -r u c label <<<"$(agent_url "$name@$start")"
  IFS=: read -r tag key iv <<<"$c"
  vbr=$(mktemp /var/tmp/mtv2v-vbr.XXXXXX)
  dd if="$head" of="$vbr" bs=512 skip=$(( start / 512 )) count=16 status=none
  read -r bps spc spt heads < <(perl -e 'binmode STDIN; read(STDIN, my $b, 512) == 512 or exit 1;
    my ($bps, $spc) = unpack("x11 v C", $b); my ($spt, $h) = unpack("x24 v v", $b);
    $spc = 1 << (256 - $spc) if $spc > 128; print "$bps $spc $spt $h\n"' < "$vbr") || true
  [[ ${bps:-0} =~ ^(512|4096)$ ]] || bps=512
  cluster=$(( bps * ${spc:-8} ))
  (( cluster >= 512 && cluster <= 2097152 )) || cluster=4096
  local -a geo=()
  if (( ${heads:-0} > 0 && heads <= 255 && ${spt:-0} > 0 && spt <= 63 )); then geo=(-H "$heads" -S "$spt"); fi
  [[ -n $label && $label != - ]] || label=
  # wimlib + ntfs-3g write tiny pieces all over the volume. On HDD RAID with a zeroing thin pool
  # that ran at 0.1-0.5 MB/s (a Windows server test: 72 GiB took 43 hours), so the volume is built in
  # memory when it fits and then written to the disk once, front to back, in large blocks.
  local img= err hdr skipped
  if ram_image "$nlen"; then
    img=$(ram_dir)/volume.ntfs
    truncate -s "$nlen" "$img"
    loop=$(losetup -f --show "$img") || { ram_release; rm -f "$vbr"; die "$slot: losetup failed"; }
    echo "  partition $((idx + 1)): building the volume in the memory of this host (at most $(gb "$nlen")), then writing it to the disk in one pass"
  else
    warn "$slot: not enough free memory on this host to build partition $((idx + 1)) in memory (needs $(gb $(( nlen + RAM_RESERVE )))) - writing the files straight to the disk, which is much slower on hard disks"
    loop=$(losetup -f --show -o "$nstart" --sizelimit "$nlen" "$path") || { rm -f "$vbr"; die "$slot: losetup failed"; }
    # buffered, so small writes merge, but with a cap: a stopped run must not leave a huge flush behind
    bdi_limit "$loop" 5
    bdi_limit "$path" 5
  fi
  echo "  partition $((idx + 1)): new NTFS file system of $(gb "$nlen") (cluster size $cluster)"
  if ! mkntfs -Q -F -q -s "$bps" -c "$cluster" -p $(( nstart / 512 )) "${geo[@]}" ${label:+-L "$label"} "$loop" >/dev/null; then
    losetup -d "$loop"; ram_release; rm -f "$vbr"; die "$slot: mkntfs failed on partition $((idx + 1))"
  fi
  info "$slot: copying the files of partition $((idx + 1))${label:+ ($label)} from the running machine (wimlib, compressed and encrypted)"
  echo "  wimlib first scans every file of the volume; nothing is transferred during the scan (about 1-2 hours for 500,000 files)"
  err=$(mktemp /var/tmp/mtv2v-err.XXXXXX)
  hdr=$(mktemp /var/tmp/mtv2v-hdr.XXXXXX)
  set +e
  # no low-speed abort after 10 min: the scan sends nothing (a server with 561,000 files took
  # 97 min); a dead connection is still noticed through TCP keepalive
  curl -sS --fail --noproxy '*' --connect-timeout 30 --keepalive-time 60 --speed-time 14400 --speed-limit 1 -D "$hdr" "$u" </dev/null 2>>"$err" |
    openssl enc -d -aes-256-ctr -K "$key" -iv "$iv" 2>>"$err" |
    wimlib-imagex apply - 1 "$loop" 2>&1 | tr '\r' '\n' | perl -e "$WIM_FILTER"
  local -a st=("${PIPESTATUS[@]}")
  set -e
  # wimlib checks every file and stops reading as soon as it has them all, without the end of
  # the stream (tables only), so curl (23) and openssl then fail on a closed pipe even after a
  # complete copy: only wimlib's own exit code says whether the copy worked.
  if (( st[2] )); then
    sed 's/^/  /' "$err" >&2 || true
    rm -f "$err" "$hdr" "$vbr"
    losetup -d "$loop" || true
    ram_release
    if (( st[0] && st[0] != 23 )); then die "$slot: the file copy of partition $((idx + 1)) from the running machine failed (curl code ${st[0]}; see C:\\Windows\\MtaalamV2V\\agent.log on the machine)"; fi
    # 58 = WIMLIB_ERR_SET_REPARSE_DATA: ntfs-3g refused a special file (seen on a WSL 1 device node)
    if (( st[2] == 58 )); then die "$slot: partition $((idx + 1)) holds a special file that cannot be copied file by file (named in the wimlib line above; usually a WSL 1 Linux distribution). Convert again with Data to copy > Maintain size for this volume: it is copied block by block with everything intact, and a thin disk only uses the space in use."; fi
    if (( st[2] > 128 )); then die "$slot: wimlib was killed by signal $(( st[2] - 128 )) while writing the files of partition $((idx + 1)) (see: journalctl -k)"; fi
    die "$slot: writing the files of partition $((idx + 1)) failed (wimlib code ${st[2]}) - if the files do not fit into $(gb "$nlen"), choose a larger size"
  fi
  rm -f "$err"
  # the agent reports the WSL 1 devices, pipes and sockets it left out (ntfs-3g cannot create them)
  skipped=$(tr -d '\r' < "$hdr" | awk 'tolower($1) == "x-mtv2v-special-files:" { print $2; exit }')
  rm -f "$hdr"
  if [[ ${skipped:-} =~ ^[0-9]+$ ]]; then
    warn "$slot: partition $((idx + 1)): $skipped special files were left out (WSL 1 devices, pipes and sockets; they hold no data). A WSL 1 Linux distribution on this volume keeps its files but loses their Linux owners and permissions, so it may not work in the new VM. Windows itself is not affected."
  fi
  perl -e "$VBR_TOOL" "$vbr" "$loop" || warn "$slot: the boot code of partition $((idx + 1)) could not be copied (only matters when Windows boots from this volume without a System Reserved partition)"
  rm -f "$vbr"
  if [[ -n $img ]]; then
    blockdev --flushbufs "$loop" || true
    losetup -d "$loop" || true
    echo "  partition $((idx + 1)): writing the finished volume to the disk in one pass"
    local conv=notrunc,fsync flags=seek_bytes
    if (( ! zero )); then conv=$conv,sparse; fi
    if [[ -b $path ]]; then flags=direct,$flags; fi
    set +e
    dd if="$img" of="$path" bs=4M seek="$nstart" oflag="$flags" conv="$conv" status=progress 2>&1 | tr '\r' '\n' | perl -e "$DD_FILTER" "$nlen"
    local -a dst=("${PIPESTATUS[@]}")
    set -e
    ram_release
    (( ! dst[0] )) || die "$slot: writing partition $((idx + 1)) to the disk failed (dd code ${dst[0]})"
  else
    sync
    losetup -d "$loop" || true
    bdi_limit "$path" 100
  fi
}

# Volume-based copy of a running Windows machine (agent 1.4+): the disk is created at the size
# of the new layout right away. The partition table and the partitions that keep their size
# come block by block (moved to their new place); every resized NTFS volume is created new at
# its size and receives its files - nothing is shrunk afterwards and the source is not changed.
stream_agent_files() {
  local slot=$1 bus=$2 bytes=$3 name=$4 opts=$5 specs=$6 head plan style final vol path dev zero=1
  local line idx start len nstart nlen act map= files=() u c tag key iv c2 missing
  for c2 in wimlib-imagex mkntfs; do command -v "$c2" >/dev/null || missing+=" $c2"; done
  [[ -z ${missing-} ]] || die "missing on this host:$missing (apt install wimtools ntfs-3g)"
  head=$(mktemp /var/tmp/mtv2v-head.XXXXXX)
  truncate -s "$bytes" "$head"
  IFS=$'\t' read -r u c _ <<<"$(agent_url "$name#head")"
  IFS=: read -r tag key iv <<<"$c"
  echo "reading the partition table of $slot from the running machine"
  agent_receive "$head" "$bytes" 0 "$u" "$key" "$iv" "$slot" >/dev/null
  # shellcheck disable=SC2086
  if ! plan=$(perl -e "$VOL_TOOL" plan "$head" $(( 1 << 50 )) $specs); then
    rm -f "$head"; die "$slot: the volumes cannot be resized (see the reason above) - nothing was copied"
  fi
  read -r _ style final <<<"$(head -n1 <<<"$plan")"
  # the converter left every volume with a file copy stream out of the block stream
  info "$slot: volume-based copy ($style disk, $(gb "$bytes") -> $(gb "$final"))"
  while read -r _ idx start len nstart nlen act; do
    if [[ -n $(agent_url "$name@$start") ]]; then
      files+=("$idx:$start:$nstart:$nlen")
      map+="$start:$len:$nstart:$nlen:1,"
      echo "  partition $((idx + 1)): $(gb "$len") -> $(gb "$nlen"), copied file by file"
    elif [[ $act == resize ]]; then
      rm -f "$head"; die "$slot: no file copy stream for the volume at byte $start"
    else
      map+="$start:$len:$nstart:$nlen:0,"
      echo "  partition $((idx + 1)): $(gb "$len"), copied block by block"
    fi
  done < <(grep '^part ' <<<"$plan")

  vol=$(alloc_disk "$bus" "$final" raw "$opts")
  if (( DRY_RUN )); then rm -f "$head"; return; fi
  path=$(pvesm path "$vol")
  [[ -b $path || -f $path ]] || { rm -f "$head"; die "cannot write to $vol directly ($path)"; }
  dev=$(blockdev --getsize64 "$path" 2>/dev/null || stat -c %s "$path")
  if (( ! PREALLOC )); then
    case $STYPE in dir|nfs|cifs|lvmthin|zfspool) zero=0 ;; esac
  fi
  echo "copying the partition table and the partitions that keep their size into $vol ($(gb "$dev"))"
  IFS=$'\t' read -r u c _ <<<"$(agent_url "$name")"
  IFS=: read -r tag key iv <<<"$c"
  export AGENT_MAP=$map
  agent_receive "$path" "$dev" "$zero" "$u" "$key" "$iv" "$slot"
  unset AGENT_MAP
  perl -e "$VOL_TOOL" write "$path" "$dev" <<<"$plan" || { rm -f "$head"; die "$slot: writing the new partition table failed"; }
  local f
  for f in "${files[@]}"; do
    IFS=: read -r idx start nstart nlen <<<"$f"
    ntfs_from_files "$slot" "$path" "$head" "$name" "$idx" "$start" "$nstart" "$nlen" "$zero"
  done
  rm -f "$head"
  info "$slot: volumes resized, the new disk is $(gb "$dev") and holds all partitions"
}

# Copies a disk of a running machine from the Mtaalam V2V Agent (hot clone) into a
# new raw volume. The converter already checked a smaller size against the guest's
# partitions, so the requested size is used as given. With --volume-size (VMware
# Converter's volume-based copy) the volumes are resized after the copy and the
# volume is cut behind the last partition.
stream_agent() {
  local slot=$1 bus=$2 sectors=$3 url=$4 crypt=$5 opts=$6 bytes tgt vol path zero=1 tag key iv specs sbytes
  bytes=$(( sectors * 512 ))
  tgt=$bytes
  specs=$(vol_specs "$slot")
  if [[ -z $specs && -n ${DISKSIZE[$slot]-} ]] && (( ${DISKSIZE[$slot]} > 0 )); then
    tgt=$(( (${DISKSIZE[$slot]} + 1048575) / 1048576 * 1048576 ))
    info "$slot: size $(gb "$bytes") -> $(gb "$tgt")"
  fi
  IFS=: read -r tag key iv <<<"$crypt"
  [[ $tag == mtv2v && ${#key} == 64 && ${#iv} == 32 ]] || die "bad stream key for $slot"
  command -v openssl >/dev/null || die "openssl is missing on this host (apt install openssl)"
  command -v curl >/dev/null || die "curl is missing on this host (apt install curl)"

  local name
  name=$(awk -F'\t' -v u="$url" '$2 == u { print $1; exit }' "$ESXI_URLS")
  if [[ -n $specs && -n $name && -n $(agent_url "$name#head") ]]; then
    stream_agent_files "$slot" "$bus" "$bytes" "$name" "$opts" "$specs"
    return
  fi
  if [[ -n $specs ]]; then
    # older agents: copy at the original layout, resize the volumes on the new disk with
    # ntfsresize, then cut the disk behind the last partition
    sbytes=$bytes   # a volume that grows needs room behind the old end
    if [[ -n ${DISKSIZE[$slot]-} ]] && (( ${DISKSIZE[$slot]} > bytes )); then
      sbytes=$(( (${DISKSIZE[$slot]} + 4194303) / 4194304 * 4194304 ))
    fi
    vol=$(alloc_disk "$bus" "$sbytes" raw "$opts")
    if (( DRY_RUN )); then return; fi
    path=$(pvesm path "$vol")
    [[ -b $path || -f $path ]] || die "cannot write to $vol directly ($path)"
    if (( ! PREALLOC )); then
      case $STYPE in dir|nfs|cifs|lvmthin|zfspool) zero=0 ;; esac
    fi
    echo "copying from the running machine into $vol ($(gb "$sbytes"); it is made smaller after the copy)"
    agent_receive "$path" "$bytes" "$zero" "$url" "$key" "$iv" "$slot"
    resize_volumes "$slot" "$path" "$sbytes" "$specs" "$bytes"
    tgt=$(shrink_volume "$vol" "$path" "$RESIZE_FINAL")
    if (( tgt != sbytes )); then
      RESCAN=1
      info "$slot: $vol is now $(gb "$tgt") and holds all partitions"
    fi
    return
  fi

  vol=$(alloc_disk "$bus" "$tgt" raw "$opts")
  if (( DRY_RUN )); then return; fi
  path=$(pvesm path "$vol")
  [[ -b $path || -f $path ]] || die "cannot write to $vol directly ($path)"
  if (( ! PREALLOC )); then
    case $STYPE in dir|nfs|cifs|lvmthin|zfspool) zero=0 ;; esac
  fi
  echo "copying from the running machine into $vol ($(gb "$tgt"))"
  agent_receive "$path" "$tgt" "$zero" "$url" "$key" "$iv" "$slot"
  if (( tgt != bytes )); then
    fix_gpt "$path" "$path"
  fi
}

# Streams a flat extent from the ESXi datastore (HTTPS) straight into a new
# raw volume: no copy of the VMware files is needed on this host.
stream_disk() {
  local slot=$1 bus=$2 sectors=$3 url=$4 opts=$5 bytes tgt copy vol path pid rc n rcfile head conv=notrunc
  bytes=$(( sectors * 512 ))
  head=$(mktemp)
  if [[ -n ${DISKSIZE[$slot]-} ]]; then
    curl -sSk -K "$ESXI_AUTH" -r 0-1048575 "$url" </dev/null 2>/dev/null | head -c 1048576 > "$head" || true
  fi
  tgt=$(target_size "$slot" "$bytes" "$head")
  copy=$(( tgt < bytes ? tgt : bytes ))
  vol=$(alloc_disk "$bus" "$tgt" raw "$opts")
  if (( DRY_RUN )); then rm -f "$head"; return; fi
  path=$(pvesm path "$vol")
  [[ -b $path || -f $path ]] || die "cannot write to $vol directly ($path)"
  # Skipping zero blocks is only safe where a new volume reads as zeros
  # (thick LVM can hold old data), and only wanted for thin disks.
  if (( ! PREALLOC )); then
    case $STYPE in dir|nfs|cifs|lvmthin|zfspool) conv=notrunc,sparse ;; esac
  fi
  echo "streaming from ESXi into $vol ($(gb "$copy"))"
  # ESXi serves one HTTP stream slowly; several range requests in parallel are much faster.
  local segs=1 code
  code=$(curl -sk -K "$ESXI_AUTH" -r 0-0 -o /dev/null -w '%{http_code}' "$url" </dev/null 2>/dev/null || true)
  if (( copy >= 2147483648 )) && [[ $code == 206 ]]; then segs=4; echo "downloading in $segs parallel parts"; fi
  trap 'kill $(jobs -p) 2>/dev/null; pkill -f -- "$url"; exit 1' PIPE HUP TERM INT
  if ! stream_ranges "$url" "$path" "$copy" "$segs" "$conv"; then
    (( segs > 1 )) || die "download from ESXi into $vol failed"
    warn "parallel download failed - retrying as one stream"
    stream_ranges "$url" "$path" "$copy" 1 "$conv" || die "download from ESXi into $vol failed"
  fi
  trap - PIPE HUP TERM INT
  if (( tgt != bytes )); then fix_gpt "$path" "$head"; fi
  rm -f "$head"
}

# Copies the first $3 bytes of $1 into $2 with $4 parallel range requests
# (one plain stream for $4 = 1), printing progress. Returns non-zero on failure.
stream_ranges() {
  local url=$1 path=$2 copy=$3 segs=$4 conv=$5 bs=4194304 per i a b total alive d n rc=0
  local -a pids=() sizes=()
  per=$(( (copy / segs / bs + 1) * bs ))
  for (( i = 0; i < segs; i++ )); do
    a=$(( i * per )); (( a < copy )) || break
    b=$(( a + per - 1 )); (( b < copy )) || b=$(( copy - 1 ))
    if (( segs == 1 )); then
      # curl exits 23/141 when "head" stops reading (shrunk disk): not an error
      { { rc=0; curl -sSk --fail -K "$ESXI_AUTH" "$url" </dev/null || rc=$?; [[ $rc == 0 || $rc == 23 || $rc == 141 ]]; } |
          head -c "$copy" | dd of="$path" bs=$bs iflag=fullblock conv=$conv status=none; } &
    else
      { curl -sSk --fail -K "$ESXI_AUTH" -r "$a-$b" "$url" </dev/null |
          dd of="$path" bs=$bs seek=$(( a / bs )) iflag=fullblock conv=$conv status=none; } &
    fi
    pids+=($!); sizes+=($(( b - a + 1 )))
  done
  while :; do
    sleep 2
    alive=0; total=0
    for i in "${!pids[@]}"; do
      if kill -0 "${pids[i]}" 2>/dev/null; then
        alive=1
        d=${pids[i]}
        [[ $(cat "/proc/$d/comm" 2>/dev/null || true) == dd ]] || d=$(pgrep -P "$d" -x dd | head -n1 || true)
        n=$(awk '/^rchar:/ { print $2 }' "/proc/${d:-0}/io" 2>/dev/null || true)
        total=$(( total + ${n:-0} ))
      else
        total=$(( total + sizes[i] ))
      fi
    done
    echo "transferred $total of $copy bytes ($(awk -v a="$total" -v b="$copy" 'BEGIN { printf "%.2f", 100 * a / b }')%)"
    (( alive )) || break
  done
  for i in "${!pids[@]}"; do wait "${pids[i]}" || rc=1; done
  return $rc
}

# ---------------------------------------------------------------------------
# Running Linux machine (file-level copy, like VMware Converter's Linux source).
# The spec file (from the converter's probe of the machine) has tab-separated lines;
# "-" marks an empty field:
#   disk  NAME SIZE PTTYPE REMOVABLE
#   part  DISK NAME NUMBER START SIZE TYPE UUID LABEL MOUNT USED PARAMS
#   whole DISK TYPE UUID LABEL MOUNT USED PARAMS          (file system on the whole disk)
#   vg    VG EXTENT  /  pv NAME VG  /  lv VG LV SIZE ATTR TYPE UUID LABEL MOUNT USED PARAMS
#   nic   NAME MAC IPS  /  swapfile PATH SIZE  /  order DISK...  (+ host, os, kernel, ...)
# Partition tables and small boot partitions are copied as they are; file systems,
# swap and LVM are created again with the same UUIDs, labels and features; the files
# are copied with rsync. The copy then gets VirtIO drivers in its initramfs, an EFI
# fallback loader and the network interface names of a Proxmox VM.

lspec() { awk -F'\t' -v k="$1" '$1 == k' "$LINUX_SPEC"; }
lval()  { awk -F'\t' -v k="$1" '$1 == k { print $2; exit }' "$LINUX_SPEC"; }
dash()  { [[ $1 == - ]] && echo "" || echo "$1"; }
lssh()  { ssh -i "$LINUX_KEY" -p "$LINUX_PORT" -o BatchMode=yes -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null \
              -o LogLevel=ERROR -o ServerAliveInterval=30 "$LINUX_FROM" "$@"; }

# New partition layout of Linux disk $1 (vmx slot $2, source disk $3) for its --volume-size
# entries: sets L_PLAN[$1] and L_FINAL[$1] (the size the Proxmox disk is created with).
linux_plan() {
  local i=$1 slot=$2 d=$3 specs size pt head plan style final line idx start len nstart nlen act
  specs=$(vol_specs "$slot")
  [[ -n $specs ]] || return 0
  [[ -n $d ]] || die "$slot: unknown source disk"
  read -r size pt < <(lspec disk | awk -F'\t' -v d="$d" '$2 == d { print $3, $4 }')
  [[ $pt == gpt || $pt == dos ]] || die "$slot ($d): only disks with a partition table can get new volume sizes"
  head=$(mktemp /var/tmp/mtv2v-head.XXXXXX)
  truncate -s "$size" "$head"
  lssh "dd if=/dev/$d bs=1M count=1 2>/dev/null" </dev/null | dd of="$head" bs=1M count=1 conv=notrunc iflag=fullblock status=none ||
    { rm -f "$head"; die "$slot: cannot read the partition table of $d"; }
  # shellcheck disable=SC2086
  if ! plan=$(VOL_ANYFS=1 perl -e "$VOL_TOOL" plan "$head" $(( 1 << 50 )) $specs); then
    rm -f "$head"; die "$slot ($d): the volumes cannot be resized (see the reason above) - nothing was changed"
  fi
  rm -f "$head"
  read -r _ style final <<<"$(head -n1 <<<"$plan")"
  L_PLAN[$i]=$plan
  L_FINAL[$i]=$final
  info "$slot ($d): new partition sizes, the Proxmox disk is created with $(gb "$final") instead of $(gb "$size")"
  while read -r _ idx start len nstart nlen act; do
    if [[ $act == resize ]]; then echo "  partition $((idx + 1)): $(gb "$len") -> $(gb "$nlen")"; fi
  done < <(grep '^part ' <<<"$plan")
  return 0
}

linux_check_host() {
  local c missing=()
  for c in ssh rsync losetup mke2fs mkfs.xfs mkswap pvcreate perl gzip chroot; do command -v "$c" >/dev/null || missing+=("$c"); done
  if (( ${#missing[@]} )); then
    DEBIAN_FRONTEND=noninteractive apt-get install -y -q rsync xfsprogs e2fsprogs >/dev/null 2>&1 || true
    missing=(); for c in ssh rsync losetup mke2fs mkfs.xfs mkswap pvcreate perl gzip chroot; do command -v "$c" >/dev/null || missing+=("$c"); done
    (( ${#missing[@]} == 0 )) || die "missing on this Proxmox host: ${missing[*]}"
  fi
  lssh true </dev/null || die "this Proxmox host cannot log in to ${LINUX_FROM#*@} (port $LINUX_PORT) with the temporary key"
}

# rsync --info=progress2 output -> "transferred X of TOTAL bytes (P%)" every 2 s; other
# lines pass through. The last line "##RC n" is rsync's exit code (24 = files vanished
# while copying, normal on a running machine).
RSYNC_FILTER='
$| = 1;
my ($base, $total) = @ARGV;
my ($buf, $next, $rc, $vanished) = ("", 0, 1, 0);
while (sysread(STDIN, my $chunk, 65536)) {
  $buf .= $chunk;
  while ($buf =~ s/^([^\r\n]*)[\r\n]//) {
    my $l = $1;
    if ($l =~ /^\s*([\d,.]+)\s+\d+%/) {
      (my $n = $1) =~ s/[,.]//g;
      my $done = $base + $n;
      if (time >= $next) { printf "transferred %d of %d bytes (%.2f%%)\n", $done, $total, $total > 0 ? 100 * $done / $total : 0; $next = time + 2; }
    } elsif ($l =~ /^##RC (\d+)$/) { $rc = $1; }
    elsif ($l =~ /vanished/) { $vanished++; }
    elsif ($l =~ /\S/) { print "  $l\n"; }
  }
}
print "  $vanished file(s) disappeared while copying (normal on a running machine)\n" if $vanished;
exit($rc == 24 ? 0 : $rc);
'

linux_cleanup() {
  set +e
  sync
  local i
  for (( i = ${#L_MOUNTS[@]} - 1; i >= 0; i-- )); do umount "${L_MOUNTS[i]}" 2>/dev/null || umount -l "${L_MOUNTS[i]}" 2>/dev/null; done
  for i in "${L_VGS[@]}"; do vgchange -an -q "$i" >/dev/null 2>&1; done
  for i in "${L_LOOPS[@]}"; do losetup -d "$i" 2>/dev/null; done
  [[ -n ${L_ROOT:-} ]] && rmdir "$L_ROOT" 2>/dev/null
  return 0
}

lmount() { mkdir -p "$2"; mount "${@:3}" "$1" "$2"; L_MOUNTS+=("$2"); }

# file system / swap for one volume: key (sda1, sdb, vg/lv) TYPE UUID LABEL MOUNT PARAMS SIZE
linux_mkfs() {
  local key=$1 type=$2 uuid=$3 label=$4 mount=$5 params=$6 size=$7 target=${L_DEV[$1]} kv f
  case $type in
    LVM2_member) return 0 ;;
    swap)
      mkswap ${uuid:+-U "$uuid"} ${label:+-L "$label"} "$target" >/dev/null
      echo "  $key: swap" ;;
    ext2|ext3|ext4)
      if [[ -z $mount ]]; then L_RAW+=("$key"); return 0; fi
      local feat= bs= isz= cnt=
      for kv in $params; do case $kv in features=*) feat=${kv#*=} ;; bs=*) bs=${kv#*=} ;; isize=*) isz=${kv#*=} ;; icount=*) cnt=${kv#*=} ;; esac; done
      feat=$(tr ',' '\n' <<<"$feat" | grep -vxE 'needs_recovery|orphan_present|recover|journal_dev|snapshot_bitmap|' | paste -sd, - || true)
      # a smaller volume cannot hold the source's inode count: mke2fs picks one for its size
      if (( size > 0 )) && (( $(blockdev --getsize64 "$target" 2>/dev/null || echo "$size") < size )); then cnt=; fi
      mke2fs -q -F -t "$type" -O "none${feat:+,$feat}" ${bs:+-b "$bs"} ${isz:+-I "$isz"} ${cnt:+-N "$cnt"} \
        ${uuid:+-U "$uuid"} ${label:+-L "$label"} "$target" >/dev/null
      L_FS+=("$mount"$'\t'"$key"$'\t'"$type")
      echo "  $key: $type for $mount" ;;
    xfs)
      if [[ -z $mount ]]; then L_RAW+=("$key"); return 0; fi
      local -A x=()
      for kv in $params; do x[${kv%%=*}]=${kv#*=}; done
      local meta="crc=${x[crc]:-1}" inode="size=${x[isize]:-512}"
      if [[ ${x[crc]:-1} == 1 ]]; then
        meta+=",finobt=${x[finobt]:-0},rmapbt=${x[rmapbt]:-0},reflink=${x[reflink]:-0},bigtime=${x[bigtime]:-0},inobtcount=${x[inobtcount]:-0}"
        inode+=",sparse=${x[sparse]:-0}"
        [[ ${x[nrext64]:-0} == 1 ]] && inode+=",nrext64=1"
      fi
      mkfs.xfs -q -f -m "$meta${uuid:+,uuid=$uuid}" -i "$inode" -n "ftype=${x[ftype]:-1}" ${label:+-L "$label"} "$target" >/dev/null
      L_FS+=("$mount"$'\t'"$key"$'\t'"xfs")
      echo "  $key: xfs for $mount" ;;
    vfat|"")
      if [[ -n $type ]] || (( size <= 134217728 )); then L_RAW+=("$key"); L_RAWMNT[$key]=$mount
      else warn "$key has no file system and is $(gb "$size") - left empty"; fi ;;
    *)
      if [[ -z $mount ]]; then L_RAW+=("$key"); warn "$key ($type) is copied block by block"
      else die "$key ($type, mounted on $mount) cannot be copied"; fi ;;
  esac
}

linux_fill() {
  local v
  L_PATHS=()
  for v in "${LINUX_VOLS[@]}"; do L_PATHS+=("$(pvesm path "$v")"); done
  read -r -a L_DISKS <<<"$(lval order)"
  (( ${#L_DISKS[@]} == ${#L_PATHS[@]} )) || die "the machine has ${#L_DISKS[@]} disk(s) but ${#L_PATHS[@]} were created"
  # own subshell (its EXIT trap unmounts and detaches everything); not run as "( ) || ..."
  # because bash ignores set -e inside the left side of ||
  local rc=0
  set +e
  ( linux_copy )
  rc=$?
  set -e
  (( rc == 0 )) || die "copying the running Linux machine failed (see above) - remove the partial VM with: qm destroy $VMID --purge"
}

linux_copy() {
  set -euo pipefail
  declare -gA L_DEV=() L_RAWMNT=()
  L_LOOPS=() L_VGS=() L_MOUNTS=() L_RAW=() L_FS=()
  L_ROOT=/run/mtaalam-v2v-root-$VMID
  trap linux_cleanup EXIT
  local i d p size pt first head loop k num line
  local f1 f2 f3 f4 f5 f6 f7 f8 f9 f10 f11

  info "copying the partition tables"
  for i in "${!L_DISKS[@]}"; do
    d=${L_DISKS[i]}; p=${L_PATHS[i]}
    read -r size pt < <(lspec disk | awk -F'\t' -v d="$d" '$2 == d { print $3, $4 }')
    if [[ $pt != none && $pt != - ]]; then
      first=$(lspec part | awk -F'\t' -v d="$d" '$2 == d { print $5 }' | sort -n | head -n1)
      head=1048576
      if [[ -n $first ]] && (( first > 0 && first < head )); then head=$first; fi
      # boot code, partition table and the gap GRUB embeds itself in
      lssh "dd if=/dev/$d bs=$head count=1 2>/dev/null" </dev/null | dd of="$p" bs="$head" count=1 conv=notrunc iflag=fullblock status=none
      if [[ -n ${L_PLAN[$i]-} ]]; then
        # new sizes: partitions move up behind a changed one, both GPT copies are rewritten
        perl -e "$VOL_TOOL" write "$p" "$(blockdev --getsize64 "$p" 2>/dev/null || stat -c %s "$p")" <<<"${L_PLAN[$i]}" ||
          die "writing the new partition table of $d failed"
        echo "  $d: partition table with the new volume sizes"
      elif [[ $pt == gpt ]]; then
        lssh "dd if=/dev/$d bs=512 skip=$(( size / 512 - 33 )) count=33 2>/dev/null" </dev/null |
          dd of="$p" bs=512 seek=$(( size / 512 - 33 )) count=33 conv=notrunc iflag=fullblock status=none
      fi
    fi
    loop=$(losetup -f --show -P "$p")
    L_LOOPS+=("$loop")
    L_DEV[$d]=$loop
    while IFS=$'\t' read -r _ f1 f2 f3 _; do
      [[ $f1 == "$d" ]] && L_DEV[$f2]=${loop}p$f3
    done < <(lspec part)
    echo "  $d -> $loop ($pt)"
  done
  udevadm settle 2>/dev/null || sleep 2
  for k in "${!L_DEV[@]}"; do
    for i in 1 2 3 4 5; do [[ -b ${L_DEV[$k]} ]] && break; sleep 1; done
    [[ -b ${L_DEV[$k]} ]] || die "partition $k did not appear as ${L_DEV[$k]}"
  done

  # LVM with the same volume group / logical volume names (fstab and the boot loader use them)
  local vg e pv pvg lv sz attr
  while IFS=$'\t' read -r _ vg e; do
    if vgs "$vg" >/dev/null 2>&1; then die "this Proxmox host has a volume group called $vg itself, so it cannot build the copy's $vg"; fi
    local -a pvs=()
    while IFS=$'\t' read -r _ pv pvg; do
      [[ $pvg == "$vg" ]] || continue
      [[ -n ${L_DEV[$pv]-} ]] || die "LVM physical volume $pv of $vg is not on a copied disk"
      pvs+=("${L_DEV[$pv]}")
    done < <(lspec pv)
    (( ${#pvs[@]} )) || die "volume group $vg has no physical volume on the copied disks"
    pvcreate -ff -y -q "${pvs[@]}" >/dev/null
    vgcreate -q -s "$(( e / 1024 ))k" "$vg" "${pvs[@]}" >/dev/null
    L_VGS+=("$vg")
    # A smaller physical volume (new volume size): the LVs with an ext/XFS file system share
    # what is left after the others, each at least its used space + 3% + 256 MiB.
    local free sizes
    free=$(vgs --noheadings --nosuffix --units b -o vg_free "$vg" | tr -d ' ')
    sizes=$(lspec lv | awk -F'\t' -v vg="$vg" -v free="$free" -v e="$e" '
      $2 != vg { next }
      { n++; name[n] = $3; size[n] = $4; total += $4
        if ($6 ~ /^(ext[234]|xfs)$/ && $9 != "-" && $10 ~ /^[0-9]+$/) {
          need[n] = $10 * 1.03 + 268435456; if ($6 == "xfs" && need[n] < 314572800) need[n] = 314572800
          if (need[n] > size[n]) need[n] = size[n]; fs[n] = 1; sneed += need[n]; spare += size[n] - need[n]
        } else fixed += $4 }
      END {
        if (total <= free) { for (i = 1; i <= n; i++) printf "%s %.0f\n", name[i], size[i]; exit }
        slack = free - fixed - sneed
        if (slack < 0) { printf "ERROR %.0f\n", fixed + sneed; exit }
        for (i = 1; i <= n; i++) {
          s = size[i]
          if (fs[i]) { s = need[i] + (spare > 0 ? slack * (size[i] - need[i]) / spare : 0); if (s > size[i]) s = size[i] }
          printf "%s %.0f\n", name[i], int(s / e) * e
        }
      }')
    if [[ $sizes == ERROR* ]]; then
      die "the LVM volumes of $vg need at least $(gb "${sizes#ERROR }") but its new physical volume has $(gb "$free") - choose a larger size"
    fi
    while IFS=$'\t' read -r _ f1 lv sz attr _; do
      [[ $f1 == "$vg" ]] || continue
      local want
      want=$(awk -v l="$lv" '$1 == l { print $2; exit }' <<<"$sizes")
      want=${want:-$sz}
      if (( want < sz )); then echo "  $vg/$lv: $(gb "$sz") -> $(gb "$want")"; fi
      lvcreate -q -y -W y -n "$lv" -L "$(( want / 1024 ))k" "$vg" >/dev/null 2>&1 ||
        { warn "$vg/$lv: not enough room for $(gb "$want") - using the rest of $vg"; lvcreate -q -y -W y -n "$lv" -l 100%FREE "$vg" >/dev/null; }
      L_DEV[$vg/$lv]=/dev/$vg/$lv
    done < <(lspec lv)
    echo "  LVM volume group $vg recreated"
  done < <(lspec vg)

  info "creating the file systems"
  while IFS=$'\t' read -r _ f1 f2 f3 f4 f5 f6 f7 f8 f9 f10 f11; do   # part: disk name num start size TYPE UUID LABEL MOUNT USED PARAMS
    linux_mkfs "$f2" "$(dash "$f6")" "$(dash "$f7")" "$(dash "$f8")" "$(dash "$f9")" "$(dash "$f11")" "$f5"
  done < <(lspec part)
  while IFS=$'\t' read -r _ f1 f2 f3 f4 f5 f6 f7; do                 # whole disk
    linux_mkfs "$f1" "$(dash "$f2")" "$(dash "$f3")" "$(dash "$f4")" "$(dash "$f5")" "$(dash "$f7")" 0
  done < <(lspec whole)
  while IFS=$'\t' read -r _ f1 f2 f3 f4 f5 f6 f7 f8 f9 f10; do       # LVM
    linux_mkfs "$f1/$f2" "$(dash "$f5")" "$(dash "$f6")" "$(dash "$f7")" "$(dash "$f8")" "$(dash "$f10")" "$f3"
  done < <(lspec lv)

  # mount in path order (parents first) and copy each file system
  mkdir -p "$L_ROOT"
  local total=0 base=0 mnt key type u rc=0 acl=(-A -X) used
  [[ $(lval rsyncnoacl) == 1 ]] && acl=()
  [[ $(lval rsyncnoxattr) == 1 ]] && acl=(-A)
  [[ $(lval rsyncnoacl) == 1 && $(lval rsyncnoxattr) != 1 ]] && acl=(-X)
  (( ${#L_FS[@]} )) || die "no ext2/3/4 or XFS file system found to copy"
  mapfile -t L_FS < <(printf '%s\n' "${L_FS[@]}" | LC_ALL=C sort)
  for line in "${L_FS[@]}"; do
    IFS=$'\t' read -r mnt key type <<<"$line"
    lmount "${L_DEV[$key]}" "$L_ROOT$mnt" -t "$type"
  done
  used=$(awk -F'\t' '($1 == "part" && $10 != "-" && $7 ~ /^(ext[234]|xfs)$/) { s += $11 }
                     ($1 == "whole" && $6 != "-" && $3 ~ /^(ext[234]|xfs)$/) { s += $7 }
                     ($1 == "lv" && $9 != "-" && $6 ~ /^(ext[234]|xfs)$/) { s += $10 } END { printf "%.0f", s }' "$LINUX_SPEC")
  total=${used:-0}
  info "copying the files ($(gb "$total") in use) - the machine keeps running"
  local ssh_cmd="ssh -i $LINUX_KEY -p $LINUX_PORT -o BatchMode=yes -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -o ServerAliveInterval=30"
  for line in "${L_FS[@]}"; do
    IFS=$'\t' read -r mnt key type <<<"$line"
    local -a excl=()
    while IFS=$'\t' read -r _ f1 _; do
      if [[ $mnt == / ]]; then excl+=(--exclude="$f1")
      elif [[ $f1 == "$mnt"/* ]]; then excl+=(--exclude="${f1#$mnt}"); fi
    done < <(lspec swapfile)
    echo "  $mnt"
    rc=0
    { rsync -aHSx --numeric-ids "${acl[@]}" --info=progress2 "${excl[@]}" -e "$ssh_cmd" \
        "$LINUX_FROM:${mnt%/}/" "$L_ROOT${mnt%/}/" </dev/null 2>&1; echo "##RC $?"; } |
      perl -e "$RSYNC_FILTER" "$base" "$total" || rc=$?
    if (( rc == 23 )); then warn "some files on $mnt could not be copied (rsync code 23, see above)"
    elif (( rc )); then die "copying $mnt failed (rsync code $rc)"; fi
    u=$(df -PB1 "$L_ROOT$mnt" | awk 'NR == 2 { print $3 }'); base=$(( base + ${u:-0} ))
  done
  printf "transferred %d of %d bytes (100.00%%)\n" "$total" "$total"

  # partitions copied block by block (EFI system partition, BIOS boot, unmounted volumes)
  for key in "${L_RAW[@]}"; do
    local srcdev=/dev/$key
    echo "  copying $key block by block"
    lssh "dd if=$srcdev bs=4M 2>/dev/null | gzip -1" </dev/null | gzip -dc | dd of="${L_DEV[$key]}" bs=4M iflag=fullblock conv=notrunc status=none ||
      die "copying $key failed"
    if [[ -n ${L_RAWMNT[$key]-} ]] && [[ -d $L_ROOT${L_RAWMNT[$key]} ]]; then lmount "${L_DEV[$key]}" "$L_ROOT${L_RAWMNT[$key]}"; fi
  done

  # swap files are made again instead of copied
  while IFS=$'\t' read -r _ f1 f2; do
    [[ -d $L_ROOT$(dirname "$f1") ]] || continue
    fallocate -l "$f2" "$L_ROOT$f1" 2>/dev/null || dd if=/dev/zero of="$L_ROOT$f1" bs=1M count=$(( f2 / 1048576 )) status=none
    chmod 600 "$L_ROOT$f1"; mkswap -q "$L_ROOT$f1" >/dev/null 2>&1 || mkswap "$L_ROOT$f1" >/dev/null
  done < <(lspec swapfile)

  linux_boot_fixes
  info "the copy of the running Linux machine is complete"
}

# Makes the copy boot on Proxmox: EFI fallback loader, VirtIO drivers in the
# initramfs, network interface names, SELinux relabel. Failures are warnings.
linux_boot_fixes() {
  local r=$L_ROOT esp e shim grub k cmdline n name mac new f
  [[ -e $r/etc ]] || { warn "no /etc in the copied root file system - boot fixes skipped"; return 0; }
  # 1. EFI: a VM starts \EFI\BOOT\BOOTX64.EFI until boot entries exist (Debian only installs EFI/debian)
  if [[ $(lval firmware) == efi ]]; then
    for esp in /boot/efi /efi /boot; do [[ -d $r$esp/EFI ]] && break; esp=; done
    if [[ -n $esp ]]; then
      e=$r$esp/EFI
      if [[ -z $(find "$e" -maxdepth 2 -ipath '*/boot/bootx64.efi' 2>/dev/null | head -n1) ]]; then
        shim=$(find "$e" -mindepth 2 -maxdepth 2 -iname shimx64.efi | head -n1)
        grub=$(find "$e" -mindepth 2 -maxdepth 2 -iname grubx64.efi | head -n1)
        mkdir -p "$e/BOOT"
        if [[ -n $shim ]]; then cp "$shim" "$e/BOOT/BOOTX64.EFI"; [[ -n $grub ]] && cp "$grub" "$e/BOOT/grubx64.efi"
        elif [[ -n $grub ]]; then cp "$grub" "$e/BOOT/BOOTX64.EFI"; fi
        [[ -n $grub && -f $(dirname "$grub")/grub.cfg ]] && cp "$(dirname "$grub")/grub.cfg" "$e/BOOT/grub.cfg"
        [[ -f $e/BOOT/BOOTX64.EFI ]] && echo "  EFI fallback loader added (EFI/BOOT/BOOTX64.EFI)"
      fi
    else
      warn "EFI system partition not found in the copy - if the VM stops in the EFI shell, add a boot entry in the OVMF menu"
    fi
  fi
  # 2. initramfs with the VirtIO drivers (RHEL-like systems only include the drivers of the old hardware)
  for f in dev proc sys; do lmount "/$f" "$r/$f" --bind; done
  lmount tmpfs "$r/run" -t tmpfs
  local -a kernels=()
  mapfile -t kernels < <({ lval kernel; chroot "$r" sh -c 'ls /lib/modules 2>/dev/null' | sort -V | tail -n1; } | awk 'NF && !seen[$0]++')
  if chroot "$r" sh -c 'command -v update-initramfs' >/dev/null 2>&1; then
    f=$r/etc/initramfs-tools/modules
    # storage drivers only: virtio_net must load from the real root so the .link file below names the card
    sed -i '/^virtio_net$/d' "$f" 2>/dev/null || true
    for k in virtio_pci virtio_scsi virtio_blk; do grep -qx "$k" "$f" 2>/dev/null || echo "$k" >> "$f"; done
    for k in "${kernels[@]}"; do
      if chroot "$r" update-initramfs -u -k "$k" >/dev/null 2>&1; then echo "  initramfs $k: VirtIO drivers added"
      else warn "update-initramfs failed for $k - if the VM does not boot, set its disk to SATA in Proxmox"; fi
    done
  elif chroot "$r" sh -c 'command -v dracut' >/dev/null 2>&1; then
    for k in "${kernels[@]}"; do
      chroot "$r" test -d "/lib/modules/$k" || continue
      if chroot "$r" dracut -f -q --no-hostonly --omit-drivers virtio_net --kver "$k" >/dev/null 2>&1; then echo "  initramfs $k: rebuilt with all drivers (VirtIO)"
      else warn "dracut failed for $k - if the VM does not boot, set its disk to SATA in Proxmox"; fi
    done
  else
    warn "neither update-initramfs nor dracut found in the copy - the initramfs keeps the old drivers"
  fi
  # 3. network: the predictable name follows the PCI slot, and on Proxmox that depends on the
  # machine type and version (ens18 or enp6s18), so guessing it left VMs without network.
  # Instead the card keeps its old name, pinned to the MAC address of the new VM; the network
  # settings of the guest stay valid as they are, whatever tool wrote them.
  cmdline=$(lval cmdline)
  if [[ $cmdline != *net.ifnames=0* ]]; then
    n=-1
    while IFS=$'\t' read -r _ name mac _; do
      n=$(( n + 1 ))
      [[ $name == en* ]] || continue
      new=$(qm config "$VMID" </dev/null | sed -n "s/^net$n: .*=\([0-9A-Fa-f:]\{17\}\).*/\1/p" | head -n1) || new=
      new=${new,,}
      if [[ -z $new ]]; then
        warn "network: VM $VMID has no net$n for the card $name - its network settings are not adapted"
        continue
      fi
      mkdir -p "$r/etc/systemd/network"
      printf '[Match]\nMACAddress=%s\n\n[Link]\nName=%s\n' "$new" "$name" > "$r/etc/systemd/network/10-mtaalam-v2v-$name.link"
      # settings that select the card by its old MAC address (netplan match:, HWADDR=, ...)
      if [[ -n $mac && ${mac,,} != "$new" ]]; then
        for f in "$r"/etc/netplan/*.yaml "$r"/etc/cloud/cloud.cfg.d/*.cfg "$r"/etc/network/interfaces \
                 "$r"/etc/network/interfaces.d/* "$r"/etc/sysconfig/network-scripts/ifcfg-* \
                 "$r"/etc/sysconfig/network/ifcfg-* "$r"/etc/NetworkManager/system-connections/* \
                 "$r"/etc/systemd/network/*.network; do
          if [[ -f $f ]] && grep -qiF -- "$mac" "$f"; then perl -pi -e "s/\\Q$mac\\E/$new/gi" "$f"; fi
        done
      fi
      echo "  network: $name keeps its name on the new card (MAC address $new)"
    done < <(lspec nic)
  fi
  # 4. SELinux labels are copied with the files; a relabel at the first boot makes sure
  if [[ $(lval selinux) == 1 ]]; then touch "$r/.autorelabel"; echo "  SELinux relabels the file systems at the first boot"; fi
  return 0
}

# Copies a VM from another Proxmox VE host: vzdump runs there as the forced command of
# a temporary key (snapshot mode - a running VM keeps running) and qmrestore reads the
# stream here.
REST_FROM= REST_PORT=22 REST_KEY= REST_SRC= REST_TOTAL=0 REST_ZSTD=0
restore_remote() {
  [[ -n $REST_FROM && -f $REST_KEY && -n $REST_SRC ]] || die "restore needs --from, --key and --source-vmid"
  check_other_runs "$VMID"
  VMID=${VMID:-$(pvesh get /cluster/nextid)}
  if qm config "$VMID" >/dev/null 2>&1; then die "VMID $VMID already exists"; fi
  pvesm status --storage "$STORAGE" >/dev/null 2>&1 || die "storage '$STORAGE' not found"
  local -a dec=(cat) uniq=()
  (( REST_ZSTD )) && dec=(zstd -dcq)
  (( NEW_MAC )) && uniq=(--unique 1)
  info "copying VM $REST_SRC from ${REST_FROM#*@} into VM $VMID on $STORAGE (vzdump stream)"
  trap 'warn "copy failed - remove the partial VM with: qm destroy '"$VMID"' --purge"' ERR
  ssh -i "$REST_KEY" -p "$REST_PORT" -o BatchMode=yes -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null \
      -o LogLevel=ERROR -o ServerAliveInterval=30 "$REST_FROM" vzdump </dev/null 2> >(grep -vE '^INFO: +[0-9]+% ' >&2) |
    "${dec[@]}" | qmrestore - "$VMID" --storage "$STORAGE" "${uniq[@]}" 2>&1 |
    perl -ne 'BEGIN { $| = 1; $t = shift @ARGV }
      if (/^progress (\d+)% \(read (\d+) bytes/) { printf "transferred %d of %d bytes (%d.00%%)\n", $2, $t > 0 ? $t : $2, $1 }
      else { print "  $_" }' "$REST_TOTAL" ||
    die "the copy from ${REST_FROM#*@} failed (see above)"
  qm config "$VMID" >/dev/null 2>&1 || die "qmrestore did not create VM $VMID"
  local key val line name
  [[ -n $NAME ]] && { name=$(sed -E 's/[^A-Za-z0-9.-]+/-/g; s/^-+//; s/-+$//' <<<"$NAME"); run qm set "$VMID" --name "${name:-imported-vm}" >/dev/null; }
  [[ -n $CORES ]] && run qm set "$VMID" --sockets 1 --cores "$CORES" >/dev/null
  [[ -n $MEMORY ]] && run qm set "$VMID" --memory "$MEMORY" >/dev/null
  while IFS= read -r line; do
    key=${line%%:*}; val=${line#*: }
    case $key in
      net[0-9]*)
        [[ -n $BRIDGE ]] && run qm set "$VMID" --"$key" "$(sed -E "s/(^|,)bridge=[^,]*/\\1bridge=$BRIDGE/" <<<"$val")" >/dev/null ;;
      ide[0-9]*|sata[0-9]*|scsi[0-9]*)
        # an ISO of the old host that is not here would stop the VM from starting
        if [[ $val == *media=cdrom* && ${val%%,*} != none && ${val%%,*} != cdrom ]] && ! pvesm path "${val%%,*}" >/dev/null 2>&1; then
          run qm set "$VMID" --"$key" none,media=cdrom >/dev/null
        fi ;;
      hostpci[0-9]*|usb[0-9]*)
        run qm set "$VMID" --delete "$key" >/dev/null; warn "$key (passthrough device of the old host) removed" ;;
    esac
  done < <(qm config "$VMID")
  trap - ERR
  if (( TEMPLATE )); then run qm template "$VMID"; info "VM $VMID converted to a template (clone it to deploy new VMs)"
  elif (( START )); then run qm start "$VMID"; fi
  echo
  info "VM $VMID imported."
}

# Cuts every disk of a stopped VM behind its last partition (after Windows shrank its
# volumes in the copy): the GPT backup moves to the new end, then the volume shrinks.
trim_disks() {
  local vmid=$1 conf=$2 line key vol path stype cur plan final style got
  while IFS= read -r line; do
    [[ $line =~ ^((scsi|sata|virtio|ide)[0-9]+):\ ([^,]+) ]] || continue
    key=${BASH_REMATCH[1]}; vol=${BASH_REMATCH[3]}
    [[ $key == "$DUMMY_SLOT" || $line == *media=cdrom* || $vol == none ]] && continue
    path=$(pvesm path "$vol" 2>/dev/null) || continue
    [[ -b $path || -f $path ]] || continue
    stype=$(pvesm status --storage "${vol%%:*}" 2>/dev/null | awk 'NR==2{print $2}')
    STYPE=$stype
    cur=$(blockdev --getsize64 "$path" 2>/dev/null || stat -c %s "$path")
    if ! plan=$(perl -e "$VOL_TOOL" plan "$path" "$cur" 2>/dev/null); then
      echo "$key: no partition table the converter can read - left as it is"; continue
    fi
    read -r _ style final <<<"$(head -n1 <<<"$plan")"
    if (( final + 8 * 1048576 >= cur )); then
      echo "$key: $(gb "$cur"), its partitions fill it - nothing to cut"; continue
    fi
    info "$key: the partitions end at $(gb "$final"); cutting $vol from $(gb "$cur")"
    if (( DRY_RUN )); then continue; fi
    perl -e "$VOL_TOOL" write "$path" "$final" <<<"$plan" || die "$key: writing the partition table failed"
    sync
    got=$(shrink_volume "$vol" "$path" "$final")
    [[ $got == "$final" ]] && info "$key: $vol is now $(gb "$final")"
  done < <(grep -E '^(scsi|sata|virtio|ide)[0-9]+: ' "$conf")
  qm rescan --vmid "$vmid" >/dev/null 2>&1 || true
}

finalize() {
  local vmid=$1 conf=/etc/pve/qemu-server/$1.conf
  [[ -f $conf ]] || die "VM $vmid not found on this node"
  [[ $(qm status "$vmid") == "status: stopped" ]] || die "shut down VM $vmid first"
  if grep -q '^\[' "$conf"; then die "VM $vmid has snapshots; delete them before finalizing"; fi

  if grep -q "^$DUMMY_SLOT:" "$conf"; then
    run qm disk unlink "$vmid" --idlist "$DUMMY_SLOT" --force
  fi
  if (( SHRINK_DISKS )); then trim_disks "$vmid" "$conf"; fi
  if ! grep -qE '^(sata[0-9]+:|net[0-9]+: e1000e=)' "$conf"; then
    info "VM $vmid already uses VirtIO"
    return
  fi
  if (( DRY_RUN )); then
    echo "(dry-run) would rewrite sataN -> scsiN (iothread=1) and e1000e -> virtio in $conf"
    return
  fi

  local backup=/root/$vmid.conf.pre-finalize tmp
  cp "$conf" "$backup"
  tmp=$(mktemp)
  sed -E \
    -e 's/^sata([0-9]+): (.*)$/scsi\1: \2,iothread=1/' \
    -e 's/^(net[0-9]+): e1000e=/\1: virtio=/' \
    -e '/^boot:/ s/sata([0-9]+)/scsi\1/g' "$conf" > "$tmp"
  cat "$tmp" > "$conf"   # write in place: /etc/pve is a cluster filesystem
  rm -f "$tmp"
  info "VM $vmid now uses VirtIO SCSI + VirtIO NIC (backup: $backup)"
  echo "If Windows fails to boot (INACCESSIBLE_BOOT_DEVICE), restore with: cp $backup $conf"
}

main() {
  [[ $EUID -eq 0 ]] || die "run as root on the Proxmox node"
  local cmd=${1:-}; shift || true
  case $cmd in
    import)
      [[ $# -ge 1 ]] || { usage; exit 1; }
      local vmx_path=$1; shift
      while (( $# )); do
        case $1 in
          --vmid)         VMID=$2; shift ;;
          --name)         NAME=$2; shift ;;
          --cores)        CORES=$2; shift ;;
          --memory)       MEMORY=$2; shift ;;
          --storage)      STORAGE=$2; shift ;;
          --bridge)       BRIDGE=$2; shift ;;
          --cpu)          CPU=$2; shift ;;
          --format)       FORMAT=$2; shift ;;
          --preallocate)  PREALLOC=1 ;;
          --esxi-urls)    ESXI_URLS=$2; shift ;;
          --esxi-auth)    ESXI_AUTH=$2; shift ;;
          --disk-size)    [[ $2 =~ ^([a-z]+[0-9]+:[0-9]+)=([0-9]+)$ ]] || die "--disk-size wants slot=bytes, e.g. scsi0:0=64424509440"
                          DISKSIZE[${BASH_REMATCH[1]}]=${BASH_REMATCH[2]}; shift ;;
          --volume-size)  [[ $2 =~ ^([a-z]+[0-9]+:[0-9]+)@([0-9]+)=([0-9]+)$ ]] || die "--volume-size wants slot@offset=bytes, e.g. scsi0:0@135266304=85899345920"
                          VOLSIZE[${BASH_REMATCH[1]}@${BASH_REMATCH[2]}]=${BASH_REMATCH[3]}; shift ;;
          --virtio-iso)   VIRTIO_ISO=$2; shift ;;
          --virtio-ready) VIRTIO_READY=1 ;;
          --new-mac)      NEW_MAC=1 ;;
          --start)        START=1 ;;
          --template)     TEMPLATE=1 ;;
          --dry-run)      DRY_RUN=1 ;;
          --linux-spec)   LINUX_SPEC=$2; shift ;;
          --linux-key)    LINUX_KEY=$2; shift ;;
          --linux-from)   LINUX_FROM=$2; shift ;;
          --linux-port)   LINUX_PORT=$2; shift ;;
          *) die "unknown option: $1" ;;
        esac
        shift
      done
      import_vm "$vmx_path"
      ;;
    restore)
      while (( $# )); do
        case $1 in
          --from)         REST_FROM=$2; shift ;;
          --port)         REST_PORT=$2; shift ;;
          --key)          REST_KEY=$2; shift ;;
          --source-vmid)  REST_SRC=$2; shift ;;
          --total)        REST_TOTAL=$2; shift ;;
          --zstd)         REST_ZSTD=1 ;;
          --vmid)         VMID=$2; shift ;;
          --name)         NAME=$2; shift ;;
          --cores)        CORES=$2; shift ;;
          --memory)       MEMORY=$2; shift ;;
          --storage)      STORAGE=$2; shift ;;
          --bridge)       BRIDGE=$2; shift ;;
          --new-mac)      NEW_MAC=1 ;;
          --start)        START=1 ;;
          --template)     TEMPLATE=1 ;;
          *) die "unknown option: $1" ;;
        esac
        shift
      done
      restore_remote
      ;;
    finalize)
      [[ $# -ge 1 ]] || { usage; exit 1; }
      local fvmid=$1; shift
      while (( $# )); do
        case $1 in
          --dry-run)      DRY_RUN=1 ;;
          --shrink-disks) SHRINK_DISKS=1 ;;
          *) die "unknown option: $1" ;;
        esac
        shift
      done
      finalize "$fvmid"
      ;;
    -h|--help|help|"") usage ;;
    *) usage; exit 1 ;;
  esac
}

# A conversion runs in its own process group, watched: when the converter's SSH session ends
# (Cancel, app closed, network lost) the whole group is stopped - curl, the decoder, wimlib.
# An abandoned run used to keep writing into a VM disk for hours and held the LVM lock, so
# every later disk creation (and the Proxmox status display) waited behind it.
supervise() {
  local p c sshd=
  p=$PPID
  while [[ -n $p ]] && (( p > 1 )); do
    c=$(cat "/proc/$p/comm" 2>/dev/null) || break
    if [[ $c == sshd ]]; then sshd=$p; break; fi
    p=$(ps -o ppid= -p "$p" 2>/dev/null | tr -d ' ') || break
  done
  [[ -n $sshd ]] || return 0
  local pg=$$
  ( exec </dev/null >/dev/null 2>&1
    while sleep 10; do
      kill -0 "$pg" 2>/dev/null || exit 0
      if ! kill -0 "$sshd" 2>/dev/null; then
        logger -t mtaalam-v2v "SSH session gone - stopping conversion process group $pg" 2>/dev/null || true
        trap '' TERM   # this watchdog is in the group too and still has to clean up
        kill -TERM -- "-$pg" 2>/dev/null; sleep 15
        umount -l "/run/mtv2v-ram.$pg" 2>/dev/null || true
        rmdir "/run/mtv2v-ram.$pg" 2>/dev/null || true
        kill -KILL -- "-$pg" 2>/dev/null; exit 0
      fi
    done ) &
  WATCHDOG=$!
  return 0
}

# Any exit with an error that did not come through die() names the command that failed - a
# conversion must never end without a message.
on_exit() {
  local rc=$1 cmd=$2
  if [[ -n ${WATCHDOG-} ]]; then kill "$WATCHDOG" 2>/dev/null || true; fi
  ram_release
  if (( rc != 0 )) && [[ -z ${DIED-} ]]; then
    echo "error: vmware2pve.sh stopped unexpectedly (exit code $rc) at: $cmd" >&2
  fi
}

# PIDs of other conversions into VM ID $1 still running on this host (left over from a cancelled
# or interrupted run, possibly of an older converter).
other_runs() {
  local id=$1 pid args mine
  local g
  mine=$(ps -o pgid= -p $$ 2>/dev/null | tr -d ' ') || return 0
  while read -r pid args; do
    if [[ $pid == "$$" || $pid == "$PPID" || $args == setsid* ]]; then continue; fi
    if [[ " $args " != *" --vmid $id "* ]]; then continue; fi
    g=$(ps -o pgid= -p "$pid" 2>/dev/null | tr -d ' ') || g=
    # gone already (a short-lived subshell of this run), or part of this run
    if [[ -z $g || $g == "$mine" ]]; then continue; fi
    echo "$pid"
  done < <(pgrep -af 'vmware2pve\.sh (import|restore) ' 2>/dev/null || true)
  return 0
}

check_other_runs() {
  local id=$1 pids p list=
  pids=$(ps -eo pid=,etimes=,comm= | awk '$3 ~ /^(lvcreate|lvremove|lvchange|lvextend|lvreduce|lvrename|vgchange)$/ && $2 > 120 { printf " %s (%s, %ds)", $1, $3, $2 }') || pids=
  [[ -z $pids ]] || die "LVM on this host is blocked: commands have been waiting for more than 2 minutes -$pids. Creating the VM disk would hang as well. Usually a disk is still being written by an earlier, interrupted copy (ps -eo pid,stat,etime,args | grep -E 'vmware2pve|perl' shows it; state D = waiting for the disk). Wait until those commands have finished, then retry."
  [[ -n $id ]] || return 0
  pids=$(other_runs "$id") || pids=
  [[ -n $pids ]] || return 0
  local t
  for p in $pids; do
    t=$(ps -o etime= -p "$p" 2>/dev/null | tr -d ' ') || continue   # ended meanwhile
    list+=" $p (running $t)"
  done
  [[ -n $list ]] || return 0
  die "another conversion into VM $id is still running on this host: PID$list. It is left over from a cancelled or interrupted run and still writes into that VM's disk. Stop it with:  pkill -f 'vmware2pve.sh (import|restore) .*--vmid $id( |\$)'  - processes that wait for the disk end when their writes are done (watch: ps -eo pid,stat,etime,args | grep vmware2pve). Then remove VM $id and retry."
}

WATCHDOG= DIED=
trap 'on_exit $? "$BASH_COMMAND"' EXIT
if [[ ${1:-} == import || ${1:-} == restore ]] && [[ ${MTV2V_GROUP-} != 1 ]] && command -v setsid >/dev/null; then
  export MTV2V_GROUP=1
  exec setsid -w bash "$0" "$@"
fi
if [[ ${MTV2V_GROUP-} == 1 ]]; then supervise || true; fi
main "$@"
