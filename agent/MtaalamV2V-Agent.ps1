<#
  Mtaalam V2V sizing agent (Windows) - Core Mtaalam Technologies, mtaalamtech.com

  Run inside a VM while it is live (and ideally busy). It measures CPU and
  memory use over a sample period, plus disk space used and where each disk's
  last partition ends, and writes <hostname>.v2v.json for the Mtaalam V2V
  Converter (Sizing agent > Load agent report). Nothing is changed in the VM.

  Usage:  powershell -ExecutionPolicy Bypass -File MtaalamV2V-Agent.ps1 [-Minutes 5] [-OutDir C:\temp]
  Works on Windows Server 2008 R2 / Windows 7 and newer (PowerShell 2.0+).
#>
param(
    [double]$Minutes = 5,
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $OutDir) { $OutDir = $here }
$inv = [Globalization.CultureInfo]::InvariantCulture

function Js([string]$s) {
    '"' + ($s -replace '\\', '\\' -replace '"', '\"' -replace "`r", '' -replace "`n", ' ') + '"'
}
function Num($v) { ([double]$v).ToString('0.##', $inv) }
function Gb($b) { ([double]$b / 1GB).ToString('0.0', $inv) + ' GB' }

$os = Get-WmiObject Win32_OperatingSystem
$cs = Get-WmiObject Win32_ComputerSystem
$cpus = [int]$cs.NumberOfLogicalProcessors
$memTotalMb = [math]::Round([double]$cs.TotalPhysicalMemory / 1MB)
Write-Host ''
Write-Host "Mtaalam V2V sizing agent - $($env:COMPUTERNAME)" -ForegroundColor Cyan
Write-Host "$($os.Caption), $cpus vCPU, $memTotalMb MB RAM"
Write-Host "Sampling CPU and memory for $Minutes minute(s). Keep the VM's normal workload running..."

# --- CPU + memory, sampled every 5 s --------------------------------------
$samples = [math]::Max(1, [int]($Minutes * 60 / 5))
$cpuSum = 0.0; $cpuPeak = 0.0; $memPeak = 0.0; $n = 0
for ($i = 0; $i -lt $samples; $i++) {
    Start-Sleep -Seconds 5
    $cpu = [double](Get-WmiObject Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime
    $avail = [double](Get-WmiObject Win32_PerfFormattedData_PerfOS_Memory).AvailableMBytes
    $used = $memTotalMb - $avail
    $cpuSum += $cpu; $n++
    if ($cpu -gt $cpuPeak) { $cpuPeak = $cpu }
    if ($used -gt $memPeak) { $memPeak = $used }
    Write-Progress -Activity 'Measuring load' -Status ("CPU {0:0}%  (peak {1:0}%)   memory used {2:0} MB  (peak {3:0} MB)" -f $cpu, $cpuPeak, $used, $memPeak) `
        -PercentComplete (100 * ($i + 1) / $samples)
}
Write-Progress -Activity 'Measuring load' -Completed
$cpuAvg = $cpuSum / [math]::Max($n, 1)

# --- disks: size, space used, end of the last partition ---------------------
$disks = @()
if (Get-Command Get-Disk -ErrorAction SilentlyContinue) {
    foreach ($d in Get-Disk | Where-Object { $_.BusType -ne 'USB' -and $_.BusType -ne 'File Backed Virtual' }) {
        $end = 0; $used = 0
        foreach ($p in Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue) {
            $pEnd = [int64]$p.Offset + [int64]$p.Size
            if ($pEnd -gt $end) { $end = $pEnd }
            $v = $null
            try { $v = $p | Get-Volume -ErrorAction Stop } catch { }
            if ($v -and $v.Size -gt 0) { $used += [int64]$v.Size - [int64]$v.SizeRemaining }
            else { $used += [int64]$p.Size }   # ESP, MSR, recovery, unformatted
        }
        $disks += New-Object PSObject -Property @{ Name = "Disk $($d.Number)"; Size = [int64]$d.Size; Used = $used; End = $end }
    }
} else {
    # Windows 7 / 2008 R2: WMI (disk sizes are rounded to whole cylinders)
    foreach ($d in Get-WmiObject Win32_DiskDrive | Where-Object { $_.InterfaceType -ne 'USB' }) {
        $end = 0; $used = 0
        foreach ($p in Get-WmiObject -Query "ASSOCIATORS OF {Win32_DiskDrive.DeviceID='$($d.DeviceID.Replace('\','\\'))'} WHERE AssocClass=Win32_DiskDriveToDiskPartition") {
            $pEnd = [int64]$p.StartingOffset + [int64]$p.Size
            if ($pEnd -gt $end) { $end = $pEnd }
            $ld = Get-WmiObject -Query "ASSOCIATORS OF {Win32_DiskPartition.DeviceID='$($p.DeviceID)'} WHERE AssocClass=Win32_LogicalDiskToPartition"
            if ($ld) { foreach ($l in @($ld)) { $used += [int64]$l.Size - [int64]$l.FreeSpace } }
            else { $used += [int64]$p.Size }
        }
        $disks += New-Object PSObject -Property @{ Name = "Disk $($d.Index)"; Size = [int64]$d.Size; Used = $used; End = $end }
    }
}

$macs = @(Get-WmiObject Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True' |
          Where-Object { $_.MACAddress } | ForEach-Object { $_.MACAddress.ToLower().Replace('-', ':') })

# --- report -------------------------------------------------------------------
$diskJson = @()
foreach ($d in $disks) {
    # +1 MiB keeps room for the backup GPT behind the last partition
    $end = 0
    if ($d.End -gt 0) { $end = [int64]$d.End + 1MB }
    $diskJson += '{"name":' + (Js $d.Name) + ',"size":' + $d.Size + ',"used":' + $d.Used + ',"part_end":' + $end + '}'
}
$json = '{"agent":"mtaalam-v2v","version":1,' +
    '"hostname":' + (Js $env:COMPUTERNAME) + ',"os":' + (Js $os.Caption) + ',' +
    '"time":' + (Js (Get-Date).ToString('yyyy-MM-dd HH:mm')) + ',"seconds":' + ($n * 5) + ',' +
    '"cpus":' + $cpus + ',"cpu_avg":' + (Num $cpuAvg) + ',"cpu_peak":' + (Num $cpuPeak) + ',' +
    '"mem_total_mb":' + $memTotalMb + ',"mem_used_peak_mb":' + [int64]$memPeak + ',' +
    '"macs":[' + (($macs | ForEach-Object { Js $_ }) -join ',') + '],' +
    '"disks":[' + ($diskJson -join ',') + ']}'
$file = Join-Path $OutDir ($env:COMPUTERNAME + '.v2v.json')
[IO.File]::WriteAllText($file, $json, (New-Object Text.UTF8Encoding($false)))

Write-Host ''
Write-Host ('CPU     average {0:0}%, peak {1:0}% of {2} vCPU' -f $cpuAvg, $cpuPeak, $cpus)
Write-Host ('Memory  peak {0:0} MB used of {1} MB' -f $memPeak, $memTotalMb)
foreach ($d in $disks) {
    Write-Host ('{0}  {1}, {2} used, last partition ends at {3}' -f $d.Name, (Gb $d.Size), (Gb $d.Used), (Gb $d.End))
}
Write-Host ''
Write-Host "Report saved: $file" -ForegroundColor Green
Write-Host 'Copy it to the PC running Mtaalam V2V Converter and use Sizing agent > Load agent report(s).'
