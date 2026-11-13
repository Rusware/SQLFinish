# Installs (or with -Uninstall removes) SQLFinish in SQL Server Management Studio 22.10 or later.
# Close SSMS first. Keep this script next to SQLFinish.vsix.
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$extensionId = 'SQLFinish.8a8fc69c-607f-434c-a7ed-9ebc1d8255d6'
$minimum = [version]'22.10'

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw 'SQL Server Management Studio 22 was not found on this machine.' }
$ssms = & $vswhere -all -prerelease -products Microsoft.VisualStudio.Product.Ssms -format json | ConvertFrom-Json |
    Where-Object { [version]$_.installationVersion -ge $minimum } |
    Sort-Object { [version]$_.installationVersion } -Descending | Select-Object -First 1
if (-not $ssms) { throw "SQLFinish needs SQL Server Management Studio $minimum or later." }

if (Get-Process Ssms -ErrorAction SilentlyContinue) { throw 'Close SQL Server Management Studio, then run this again.' }

$installer = Join-Path $ssms.installationPath 'Common7\IDE\VSIXInstaller.exe'
$arguments = if ($Uninstall) { "/quiet /instanceIds:$($ssms.instanceId) /uninstall:$extensionId" }
             else { "/quiet /instanceIds:$($ssms.instanceId) `"$(Join-Path $PSScriptRoot 'SQLFinish.vsix')`"" }
$exit = (Start-Process $installer -ArgumentList $arguments -Wait -PassThru).ExitCode
if ($exit -ne 0) { throw "VSIXInstaller failed with exit code $exit. Its log is in $env:TEMP (newest dd_VSIXInstaller_*.log)." }
Write-Host ("SQLFinish {0} SSMS {1}." -f $(if ($Uninstall) { 'removed from' } else { 'installed into' }), $ssms.installationVersion)
