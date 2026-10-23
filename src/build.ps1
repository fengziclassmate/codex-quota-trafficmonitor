param([Parameter(Mandatory = $true)][string]$ZigPath)
$ErrorActionPreference = 'Stop'
$zigExecutable = (Resolve-Path -LiteralPath $ZigPath).Path
$zigDirectory = Split-Path -Parent $zigExecutable
$buildDirectory = Join-Path $PSScriptRoot 'work\build'
$pluginDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'plugins'
New-Item -ItemType Directory -Path $buildDirectory, $pluginDirectory -Force | Out-Null
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $buildDirectory 'zig-cache'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /r:System.Web.Extensions.dll "/out:$buildDirectory\CodexQuotaBridge.exe" "$PSScriptRoot\Bridge.cs"
if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
& $compiler /nologo /target:exe /platform:x64 /optimize+ /r:System.Web.Extensions.dll "/r:$buildDirectory\CodexQuotaBridge.exe" "/out:$buildDirectory\Tests.exe" "$PSScriptRoot\Tests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& "$buildDirectory\Tests.exe"
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
& $zigExecutable c++ -target x86_64-windows-msvc -nostdlib '-fgnuc-version=4.2.1' -isystem "$zigDirectory\lib\libc\include\any-windows-any" -fno-exceptions -fno-rtti -fno-stack-protector -ffreestanding -fno-builtin -std=c++17 -O2 -c "$PSScriptRoot\CodexQuota.cpp" -o "$buildDirectory\CodexQuota.obj"
if ($LASTEXITCODE -ne 0) { throw 'C++ compilation failed.' }
& $zigExecutable dlltool -m i386:x86-64 -d "$PSScriptRoot\kernel32.def" -l "$buildDirectory\kernel32.lib"
if ($LASTEXITCODE -ne 0) { throw 'Kernel32 import library failed.' }
& $zigExecutable dlltool -m i386:x86-64 -d "$PSScriptRoot\user32.def" -l "$buildDirectory\user32.lib"
if ($LASTEXITCODE -ne 0) { throw 'User32 import library failed.' }
& $zigExecutable c++ -target x86_64-windows-msvc -nostdlib -shared "$buildDirectory\CodexQuota.obj" "$buildDirectory\kernel32.lib" "$buildDirectory\user32.lib" '-Wl,--entry,DllMain' -o "$buildDirectory\CodexQuota.dll"
if ($LASTEXITCODE -ne 0) { throw 'DLL link failed.' }
Copy-Item -LiteralPath "$buildDirectory\CodexQuota.dll", "$buildDirectory\CodexQuotaBridge.exe" -Destination $pluginDirectory -Force
Write-Output "Built plugin: $pluginDirectory"
