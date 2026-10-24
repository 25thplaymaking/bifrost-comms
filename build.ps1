# Builds Bifrost Comms with Windows' own C# compiler (C# 5, .NET Framework 4.x): nothing to install.
#   build\BifrostComms.exe   the program (what players download)
#   build\CommsTests.exe     the tests: .\build\CommsTests.exe (exit code 1 when a check fails; pictures in build\renders)
#   build\icons\             the icon
# -Version 1.2.0 sets the version (default 1.0.0). Works in Windows PowerShell 5.1 and PowerShell 7.
param([string]$Version = "1.0.0")
$ErrorActionPreference = "Stop"

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.0.0" }
$root = $PSScriptRoot
$fw = Join-Path $env:SystemRoot "Microsoft.NET\Framework64\v4.0.30319"
$csc = Join-Path $fw "csc.exe"
$build = Join-Path $root "build"
$icons = Join-Path $build "icons"
New-Item -ItemType Directory -Force $build, $icons, (Join-Path $build "tools") | Out-Null

$refs = "System.dll", "System.Core.dll", "System.Drawing.dll", "System.Windows.Forms.dll", "System.Web.Extensions.dll",
        "System.IO.Compression.dll", "System.IO.Compression.FileSystem.dll" | ForEach-Object { "/reference:" + (Join-Path $fw $_) }

function Compile([string]$target, [string]$file, [string[]]$sources, [string[]]$extra = @()) {
  $cscArgs = @("/nologo", "/optimize+", "/codepage:65001", "/target:$target", "/out:$file") + $refs + $extra + $sources
  $output = & $csc @cscArgs 2>&1
  if ($LASTEXITCODE -ne 0) { $output | ForEach-Object { Write-Host $_ }; throw "Compile failed: $file" }
  Write-Host "Built $file"
}

$src = Join-Path $root "src"
$all = Get-ChildItem $src -Filter *.cs | ForEach-Object FullName
$noMain = Get-ChildItem $src -Filter *.cs | Where-Object Name -ne "Program.cs" | ForEach-Object FullName

$versionFile = Join-Path $build "Version.cs"
@"
using System.Reflection;
[assembly: AssemblyTitle("Bifrost Comms")]
[assembly: AssemblyProduct("Bifrost Comms")]
[assembly: AssemblyDescription("TeamSpeak 3 and Coalition VON setup and launcher for Arma Reforger")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
[assembly: AssemblyInformationalVersion("$Version")]
"@ | Set-Content -Path $versionFile -Encoding UTF8

$maker = Join-Path $build "tools\IconMaker.exe"
Compile "exe" $maker @((Join-Path $src "Look.cs"), (Join-Path $root "tools\IconMaker.cs"))
& $maker $icons
if ($LASTEXITCODE -ne 0) { throw "IconMaker failed" }

Compile "winexe" (Join-Path $build "BifrostComms.exe") ($all + $versionFile) @("/win32icon:" + (Join-Path $icons "comms.ico"))
Compile "exe" (Join-Path $build "CommsTests.exe") ($noMain + (Join-Path $root "tests\CommsTests.cs") + $versionFile)
$hash = (Get-FileHash (Join-Path $build "BifrostComms.exe") -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "BifrostComms.exe $Version sha256 $hash"
