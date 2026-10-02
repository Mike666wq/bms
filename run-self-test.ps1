$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1')
$testRoot = Join-Path $PSScriptRoot '.testing\stage2'
$exe = Join-Path $testRoot 'BmsRealtimeDemo.exe'
$resultPath = Join-Path $testRoot 'self-test-result.txt'
if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath -Force }
Push-Location -LiteralPath $testRoot
try {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $exe
    $startInfo.Arguments = '--self-test'
    $startInfo.WorkingDirectory = $testRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::Start($startInfo)
    try {
        if (-not $process.WaitForExit(120000)) {
            $process.Kill()
            [void]$process.WaitForExit(10000)
            throw 'Isolated self-test timed out after 120 seconds.'
        }
        $process.Refresh()
        if ($process.ExitCode -ne 0) { throw ('Self-test process exited with code ' + $process.ExitCode) }
    } finally {
        $process.Dispose()
    }
} finally { Pop-Location }
if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
    throw 'Self-test executable exited before creating the isolated result file.'
}
$result = Get-Content -LiteralPath $resultPath -Raw
Write-Output $result
if (-not $result.TrimStart([char]0xFEFF).StartsWith('PASS:', [StringComparison]::Ordinal)) { throw 'Isolated self-test reported a failure.' }
