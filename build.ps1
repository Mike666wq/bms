$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$testRoot = Join-Path $PSScriptRoot '.testing\stage2'
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$outputExe = Join-Path $testRoot 'BmsRealtimeDemo.exe'
$manifestPath = Join-Path $PSScriptRoot 'BmsRealtimeDemo.manifest'
# 共享基座源码：保存在仓库内 shared/ 目录，确保单仓 clone / 独立构建完全自洽。
$sharedSources = @('shared\AcmeBase.cs', 'shared\AcmeCloud.cs')
foreach ($shared in $sharedSources) {
    if (-not (Test-Path -LiteralPath $shared)) {
        throw ('共享源码缺失：' + $shared + '。请检查 shared/ 目录完整性。')
    }
}

# Verify each generated copy still matches its declared @source-sha256.
# Line endings are normalized before hashing: a Windows checkout may convert
# LF to CRLF, and hashing raw bytes would then report a false "copy was modified".
$sha256 = [System.Security.Cryptography.SHA256]::Create()
foreach ($shared in $sharedSources) {
    $full = Join-Path $PSScriptRoot $shared
    $text = [System.IO.File]::ReadAllText($full, [System.Text.Encoding]::UTF8)
    $declared = [regex]::Match($text, '// @source-sha256 ([0-9a-f]{64})').Groups[1].Value
    $marker = '// @end-header'
    $at = $text.IndexOf($marker)
    if (-not $declared -or $at -lt 0) {
        throw ('共享副本不是生成物（缺 @source-sha256 / @end-header）：' + $shared)
    }
    $body = $text.Substring($at + $marker.Length) -replace "`r`n", "`n"
    if ($body.StartsWith("`n")) { $body = $body.Substring(1) }
$hash = -join ($sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($body)) | ForEach-Object { $_.ToString('x2') })
    if ($hash -ne $declared) {
        throw ('共享副本被改动：' + $shared + '。声明 ' + $declared + ' / 实际 ' + $hash + '。请恢复或重跑 _base/sync-base.mjs --write')
    }
}

& $compilerPath /nologo /target:winexe /optimize+ (('/out:' + $outputExe)) (('/win32manifest:' + $manifestPath)) /reference:System.dll /reference:System.Core.dll /reference:System.Data.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.Runtime.Serialization.dll /reference:System.Security.dll /reference:lib\System.Data.SQLite.dll shared\AcmeBase.cs shared\AcmeCloud.cs Program.cs Protocol.cs Simulator.cs TrendControl.cs RealtimeData.cs SampleStore.cs SampleStore.Xlsx.cs MonthlyCatalog.cs CloudRealtime.cs CloudPage.cs CloudDiagnostics.cs CloudDiagnosticsDialog.cs StoragePage.cs StyledActionButton.cs RoundedInputHost.cs ExportNaming.cs PartitionCycleManager.cs PartitionSettingsControl.cs PartitionCycleTests.cs PartitionIntegration.cs PartitionStorage.cs LocalIntegrationTests.cs CloudTransportTests.cs ControlledCloudTransportTestHost.cs UiTheme.cs UiLayoutTests.cs ExportDestinationTests.cs
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'lib\System.Data.SQLite.dll') -Destination $testRoot -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'x64\SQLite.Interop.dll') -Destination $testRoot -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'BmsRealtimeDemo.exe.config') -Destination $testRoot -Force
$files = @('BmsRealtimeDemo.exe', 'System.Data.SQLite.dll', 'SQLite.Interop.dll', 'BmsRealtimeDemo.exe.config') | ForEach-Object {
    $path = Join-Path $testRoot $_
    $item = Get-Item -LiteralPath $path
    [pscustomobject]@{ name = $_; length = $item.Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}
$commitSha = $env:GITHUB_SHA
if (-not $commitSha) { try { $commitSha = (& git -C $PSScriptRoot rev-parse HEAD 2>$null) } catch { } }
if (-not $commitSha) { $commitSha = 'unknown' }
$buildManifest = [pscustomobject]@{ version = '1.2.7.0'; channel = 'development'; commit = $commitSha; builtUtc = [DateTime]::UtcNow.ToString('o'); signature = 'unsigned'; files = @($files) }
$buildManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $testRoot 'build-manifest.json') -Encoding UTF8
Write-Output ('Isolated development build succeeded: ' + $outputExe)
