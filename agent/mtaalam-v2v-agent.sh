#!/bin/sh
# Mtaalam V2V sizing agent (Linux) - Core Mtaalam Technologies, mtaalamtech.com
#
# Run as root inside a VM while it is live (and ideally busy). It measures CPU
# and memory use over a sample period, plus disk space used and where each
# disk's last partition ends, and writes <hostname>.v2v.json for the Mtaalam
# V2V Converter (Sizing agent > Load agent report). Nothing is changed.
#
# Usage: sh mtaalam-v2v-agent.sh [minutes] [output-dir]      (default: 5, current dir)
MINUTES=${1:-5}
OUTDIR=${2:-.}
HOST=$(hostname -s 2>/dev/null || hostname)

json() { printf '"%s"' "$(printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g')"; }
gb()   { awk -v b="$1" 'BEGIN { printf "%.1f GB", b / 1073741824 }'; }

OS=$( (. /etc/os-release 2>/dev/null && echo "$PRETTY_NAME") || uname -sr)
CPUS=$(grep -c '^processor' /proc/cpuinfo)
MEMTOTAL=$(awk '/^MemTotal:/ { print int($2 / 1024) }' /proc/meminfo)
echo
echo "Mtaalam V2V sizing agent - $HOST"
echo "$OS, $CPUS vCPU, $MEMTOTAL MB RAM"
echo "Sampling CPU and memory for $MINUTES minute(s). Keep the VM's normal workload running..."

# --- CPU + memory, sampled every 5 s ----------------------------------------
mem_used() {
  awk '/^MemTotal:/ {t=$2} /^MemAvailable:/ {a=$2} /^MemFree:/ {f=$2} /^Buffers:/ {b=$2} /^Cached:/ {c=$2}
       END { if (a == "") a = f + b + c; print int((t - a) / 1024) }' /proc/meminfo
}
cpu_times() { awk '/^cpu / { idle = $5 + $6; total = 0; for (i = 2; i <= NF; i++) total += $i; print idle, total }' /proc/stat; }

SAMPLES=$(awk -v m="$MINUTES" 'BEGIN { n = int(m * 60 / 5); print (n < 1 ? 1 : n) }')
set -- $(cpu_times); IDLE0=$1; TOTAL0=$2
CPU_SUM=0; CPU_PEAK=0; MEM_PEAK=0; I=0
while [ "$I" -lt "$SAMPLES" ]; do
  sleep 5
  set -- $(cpu_times)
  CPU=$(awk -v i0="$IDLE0" -v t0="$TOTAL0" -v i1="$1" -v t1="$2" 'BEGIN { d = t1 - t0; printf "%.1f", d > 0 ? 100 * (1 - (i1 - i0) / d) : 0 }')
  IDLE0=$1; TOTAL0=$2
  MEM=$(mem_used)
  CPU_SUM=$(awk -v a="$CPU_SUM" -v b="$CPU" 'BEGIN { print a + b }')
  CPU_PEAK=$(awk -v a="$CPU_PEAK" -v b="$CPU" 'BEGIN { print (b > a ? b : a) }')
  [ "$MEM" -gt "$MEM_PEAK" ] && MEM_PEAK=$MEM
  I=$((I + 1))
  printf '\r  %d/%d  CPU %5s%% (peak %s%%)   memory used %s MB (peak %s MB)   ' "$I" "$SAMPLES" "$CPU" "$CPU_PEAK" "$MEM" "$MEM_PEAK"
done
echo
CPU_AVG=$(awk -v s="$CPU_SUM" -v n="$SAMPLES" 'BEGIN { printf "%.1f", s / n }')

# --- disks -------------------------------------------------------------------
# Whole disks a block device lives on (through partitions, LVM, dm-crypt, RAID).
disks_of() {
  n=$1
  if [ -f "/sys/class/block/$n/partition" ]; then
    basename "$(dirname "$(readlink -f "/sys/class/block/$n")")"
  elif [ -d "/sys/class/block/$n/slaves" ] && [ -n "$(ls "/sys/class/block/$n/slaves" 2>/dev/null)" ]; then
    for s in $(ls "/sys/class/block/$n/slaves"); do disks_of "$s"; done
  elif [ -d "/sys/block/$n" ]; then
    echo "$n"
  fi
}

TMP=$(mktemp)
# used bytes per filesystem / swap, spread over the disks it lives on
{
  df -P -k -l 2>/dev/null | awk 'NR > 1 && $1 ~ /^\/dev\// { print $1, $3 * 1024 }'
  awk 'NR > 1 && $1 ~ /^\/dev\// { print $1, $3 * 1024 }' /proc/swaps 2>/dev/null
} | sort -u -k1,1 | while read -r dev used; do
  name=$(basename "$(readlink -f "$dev")")
  set -- $(disks_of "$name" | sort -u)
  [ $# -gt 0 ] || continue
  for d in "$@"; do echo "$d $((used / $#))"; done
done > "$TMP"

DISKS=""
SUMMARY=""
for sys in /sys/block/*; do
  d=$(basename "$sys")
  case $d in loop*|ram*|sr*|fd*|zram*|dm-*|md*|nbd*) continue ;; esac
  [ "$(cat "$sys/removable" 2>/dev/null)" = 1 ] && continue
  size=$(( $(cat "$sys/size") * 512 ))
  [ "$size" -gt 0 ] || continue
  end=0
  for p in "$sys"/"$d"*; do
    [ -f "$p/start" ] || continue
    e=$(( ($(cat "$p/start") + $(cat "$p/size")) * 512 ))
    [ "$e" -gt "$end" ] && end=$e
  done
  # no partition table (LVM PV / filesystem on the whole disk): cannot shrink
  if [ "$end" -eq 0 ]; then end=$size; else end=$((end + 1048576)); fi
  used=$(awk -v d="$d" '$1 == d { s += $2 } END { printf "%.0f", s }' "$TMP")
  DISKS="$DISKS${DISKS:+,}{\"name\":$(json "$d"),\"size\":$size,\"used\":$used,\"part_end\":$end}"
  SUMMARY="$SUMMARY$d  $(gb "$size"), $(gb "$used") used, partitions end at $(gb "$end")
"
done
rm -f "$TMP"

MACS=""
for n in /sys/class/net/*; do
  [ "$(basename "$n")" = lo ] && continue
  [ -e "$n/device" ] || continue          # physical / virtual NICs only, no bridges or veth
  mac=$(cat "$n/address" 2>/dev/null)
  [ -n "$mac" ] && MACS="$MACS${MACS:+,}$(json "$mac")"
done

FILE="$OUTDIR/$HOST.v2v.json"
printf '{"agent":"mtaalam-v2v","version":1,"hostname":%s,"os":%s,"time":%s,"seconds":%d,"cpus":%d,"cpu_avg":%s,"cpu_peak":%s,"mem_total_mb":%d,"mem_used_peak_mb":%d,"macs":[%s],"disks":[%s]}\n' \
  "$(json "$HOST")" "$(json "$OS")" "$(json "$(date '+%Y-%m-%d %H:%M')")" $((SAMPLES * 5)) "$CPUS" "$CPU_AVG" "$CPU_PEAK" \
  "$MEMTOTAL" "$MEM_PEAK" "$MACS" "$DISKS" > "$FILE"

echo
echo "CPU     average $CPU_AVG%, peak $CPU_PEAK% of $CPUS vCPU"
echo "Memory  peak $MEM_PEAK MB used of $MEMTOTAL MB"
printf '%s' "$SUMMARY"
echo
echo "Report saved: $FILE"
echo "Copy it to the PC running Mtaalam V2V Converter and use Sizing agent > Load agent report(s)."
