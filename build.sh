#!/usr/bin/env bash
# Builds Bifrost Comms on Linux with Mono's C# compiler (the same C# 5 / .NET Framework 4.x program build.ps1 makes with
# Windows' own compiler). Handy for working on Linux; release builds come from build.ps1 on Windows.
#   build/BifrostComms.exe   the program
#   build/CommsTests.exe     the tests (mono build/CommsTests.exe; xvfb-run -a for the pictures)
#   build/icons/             the icon
# VERSION=1.2.1 ./build.sh sets the version (default 1.2.0).
set -euo pipefail
cd "$(dirname "$0")"
version="${VERSION:-1.2.0}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "VERSION must look like 1.0.0" >&2; exit 2; }
mkdir -p build/tools build/icons
refs=(-r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Web.Extensions.dll
      -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll -r:System.Management.dll)
opts=(-nologo -optimize+ -langversion:5 -sdk:4.5 -warnaserror+ -nowarn:618)
cat > build/Version.cs <<CS
using System.Reflection;
[assembly: AssemblyTitle("Bifrost Comms")]
[assembly: AssemblyProduct("Bifrost Comms")]
[assembly: AssemblyDescription("TeamSpeak 3 and Coalition VON setup and launcher for Arma Reforger")]
[assembly: AssemblyVersion("$version.0")]
[assembly: AssemblyFileVersion("$version.0")]
[assembly: AssemblyInformationalVersion("$version")]
CS
mcs "${opts[@]}" -target:exe -out:build/tools/IconMaker.exe "${refs[@]}" src/Look.cs tools/IconMaker.cs
mono build/tools/IconMaker.exe build/icons
mcs "${opts[@]}" -target:winexe -out:build/BifrostComms.exe -win32icon:build/icons/comms.ico "${refs[@]}" src/*.cs build/Version.cs
mcs "${opts[@]}" -target:exe -out:build/CommsTests.exe "${refs[@]}" $(ls src/*.cs | grep -v Program.cs) tests/CommsTests.cs build/Version.cs
echo "built build/BifrostComms.exe $version ($(stat -c %s build/BifrostComms.exe) bytes)"
