# Runs the Cocoa RTF fixtures through a real RichEditBox and prints the report.
# RichEdit can't load inside a unit test, so this is the regression check for
# Buoy.Windows/Editor/RichEditConversion.cs. A window flashes open and closes.
#
#   ./script/rtf-check.ps1            # summary
#   ./script/rtf-check.ps1 -Full      # with RichEdit's view of every fixture
param([switch]$Full)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Buoy.Windows\Buoy.Windows.csproj'
$fixtures = Join-Path $root 'tests\Buoy.Core.Tests\Fixtures\cocoa'
$report = Join-Path ([IO.Path]::GetTempPath()) 'buoy-rtf-check.txt'

dotnet build $project -c Debug -v quiet -nologo | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $root 'src\Buoy.Windows\bin\Debug\net10.0-windows10.0.26100.0\win-x64\Buoy.exe'
Remove-Item $report -ErrorAction SilentlyContinue
$process = Start-Process $exe -ArgumentList '--rtf-check', "`"$fixtures`"", "`"$report`"" -PassThru
if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'RTF check timed out' }
if (-not (Test-Path $report)) { throw 'RTF check wrote no report' }

$lines = Get-Content $report -Encoding UTF8
if ($Full) { $lines } else { $lines | Where-Object { $_ -match '^(RESULT|FAIL|  expected|  actual)' } }
if ($lines[0] -ne 'RESULT PASS') { exit 1 }
