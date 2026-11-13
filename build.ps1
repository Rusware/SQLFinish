# Builds dist\SQLFinish.vsix. Needs Visual Studio (any edition) for its MSBuild; no VS SDK workload required.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -prerelease -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found: install Visual Studio or its Build Tools.' }

dotnet run --project "$root\check" -c Release
if ($LASTEXITCODE -ne 0) { throw 'Engine self-check failed; not packaging.' }

New-Item -ItemType Directory -Force "$root\dist" | Out-Null
& $msbuild "$root\src\SQLFinish.csproj" -restore -t:Rebuild -p:Configuration=Release -v:minimal -nologo  # Rebuild: an incremental build can package a stale manifest version
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item "$root\install.ps1" "$root\dist\Install-SQLFinish.ps1" -Force
Copy-Item "$root\Install.cmd" "$root\dist\Install.cmd" -Force
# The one file people download: unzip, double-click Install.cmd.
Compress-Archive -Path "$root\dist\SQLFinish.vsix", "$root\dist\Install.cmd", "$root\dist\Install-SQLFinish.ps1" -DestinationPath "$root\dist\SQLFinish-Setup.zip" -Force
Write-Host "Built $root\dist\SQLFinish.vsix and $root\dist\SQLFinish-Setup.zip"
