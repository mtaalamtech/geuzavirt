# Builds dist\GeuzaVirt\ (exe + guest scripts) and a zip for distribution.
# Needs only the .NET Framework 4.x compiler that ships with Windows.
#   -OutName GeuzaVirt-next    builds into dist\GeuzaVirt-next instead (no zip), e.g. while
#                              the normal build is running a conversion.
param([string]$OutName = 'GeuzaVirt')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = Split-Path $here
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out  = Join-Path $repo "dist\$OutName"
$obj  = Join-Path $here "obj\$OutName"
$exe  = Join-Path $out 'GeuzaVirt.exe'
New-Item -ItemType Directory -Force $out, $obj | Out-Null

$common = @(
    '/nologo', '/target:winexe', '/optimize+', '/platform:anycpu',
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll', '/r:System.Security.dll',
    "/win32manifest:$here\app.manifest",
    "/resource:$repo\vmware2pve.sh,vmware2pve.sh",
    "/resource:$repo\agent\mtaalam-v2v-agent.sh,mtaalam-v2v-agent.sh"
)

# Guest agent service: built with the .NET 3.5 compiler (C# 3) against .NET 2.0,
# so it also runs on Windows Server 2008 R2; the .config lets it use .NET 4.x.
$csc35 = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$agentOut = Join-Path $repo 'agent\MtaalamV2VAgent.exe'
& $csc35 /nologo /target:winexe /optimize+ /platform:anycpu /r:System.dll /r:System.Management.dll /r:System.ServiceProcess.dll `
    /r:System.Windows.Forms.dll "/win32manifest:$repo\agent\src\agent.manifest" "/out:$agentOut" "$repo\agent\src\Agent.cs"
if ($LASTEXITCODE) { throw 'agent compile failed' }
Copy-Item -Force "$repo\agent\src\MtaalamV2VAgent.exe.config" (Join-Path $repo 'agent\MtaalamV2VAgent.exe.config')
$sources = Get-ChildItem "$here\src\*.cs" | ForEach-Object FullName

# Pass 1 renders the icon, pass 2 embeds it.
$iconExe = Join-Path $obj 'icon-gen.exe'
& $csc @common "/out:$iconExe" @sources
if ($LASTEXITCODE) { throw 'compile failed' }
$ico = Join-Path $obj 'app.ico'
Start-Process $iconExe -ArgumentList '--write-icon', "`"$ico`"" -Wait
if (-not (Test-Path $ico)) { throw 'icon generation failed' }

& $csc @common "/win32icon:$ico" "/out:$exe" @sources
if ($LASTEXITCODE) { throw 'compile failed' }

Copy-Item -Recurse -Force (Join-Path $repo 'guest-prep') $out
Copy-Item -Recurse -Force (Join-Path $repo 'agent') $out
Remove-Item -Recurse -Force (Join-Path $out 'agent\src')
Copy-Item -Force (Join-Path $repo 'vmware2pve.sh') $out
Copy-Item -Force (Join-Path $here 'USER-GUIDE.txt') $out
Copy-Item -Force (Join-Path $repo 'LICENSE') (Join-Path $out 'LICENSE.txt')   # GPLv3; source: github.com/mtaalamtech/geuzavirt

$version = (Select-String -Path "$here\src\Program.cs" -Pattern 'Version = "([0-9.]+)"').Matches[0].Groups[1].Value
if ($OutName -ne 'GeuzaVirt') { Write-Host "Built $exe"; return }
Get-ChildItem (Join-Path $repo 'dist') -Filter 'GeuzaVirt-*.zip' | Remove-Item
$zip = Join-Path $repo "dist\GeuzaVirt-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $out -DestinationPath $zip
Write-Host "Built $exe"
Write-Host "Package $zip"
