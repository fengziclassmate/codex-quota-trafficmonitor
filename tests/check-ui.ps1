$ErrorActionPreference = 'Stop'
$repositoryDirectory = Split-Path -Parent $PSScriptRoot
$testDirectory = Join-Path $PSScriptRoot 'work\ui'
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
python (Join-Path $PSScriptRoot 'ui_fixture.py')
if ($LASTEXITCODE -ne 0) { throw 'UI fixture generation failed.' }
$previousDataDirectory = $env:CODEX_QUOTA_DATA_DIR
try {
    $env:CODEX_QUOTA_DATA_DIR = Join-Path $PSScriptRoot 'work\ui-fixture'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $testExecutable = Join-Path $testDirectory 'UiCheck.exe'
    & $compiler /nologo /platform:x64 /r:System.Drawing.dll /r:System.Windows.Forms.dll "/out:$testExecutable" (Join-Path $PSScriptRoot 'UiCheck.cs')
    if ($LASTEXITCODE -ne 0) { throw 'UI test compilation failed.' }
    & $testExecutable (Join-Path $repositoryDirectory 'plugins\CodexQuotaBridge.exe') $testDirectory
    if ($LASTEXITCODE -ne 0) { throw 'UI rendering checks failed.' }
}
finally { $env:CODEX_QUOTA_DATA_DIR = $previousDataDirectory }
