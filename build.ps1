$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$testRoot = Join-Path $PSScriptRoot '.testing\stage2'
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$outputExe = Join-Path $testRoot 'BmsRealtimeDemo.exe'
$manifestPath = Join-Path $PSScriptRoot 'BmsRealtimeDemo.manifest'
& $compilerPath /nologo /target:winexe /optimize+ (('/out:' + $outputExe)) (('/win32manifest:' + $manifestPath)) /reference:System.dll /reference:System.Core.dll /reference:System.Data.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.Runtime.Serialization.dll /reference:System.Security.dll /reference:lib\System.Data.SQLite.dll Program.cs Protocol.cs Simulator.cs TrendControl.cs RealtimeData.cs SampleStore.cs SampleStore.Xlsx.cs MonthlyCatalog.cs CloudRealtime.cs CloudPage.cs CloudDiagnostics.cs CloudDiagnosticsDialog.cs StoragePage.cs StyledActionButton.cs RoundedInputHost.cs ExportNaming.cs PartitionCycleManager.cs PartitionSettingsControl.cs PartitionCycleTests.cs PartitionIntegration.cs PartitionStorage.cs LocalIntegrationTests.cs CloudTransportTests.cs ControlledCloudTransportTestHost.cs UiTheme.cs UiLayoutTests.cs ExportDestinationTests.cs
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'lib\System.Data.SQLite.dll') -Destination $testRoot -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'x64\SQLite.Interop.dll') -Destination $testRoot -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'BmsRealtimeDemo.exe.config') -Destination $testRoot -Force
$files = @('BmsRealtimeDemo.exe', 'System.Data.SQLite.dll', 'SQLite.Interop.dll', 'BmsRealtimeDemo.exe.config') | ForEach-Object {
    $path = Join-Path $testRoot $_
    $item = Get-Item -LiteralPath $path
    [pscustomobject]@{ name = $_; length = $item.Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}
$buildManifest = [pscustomobject]@{ version = '1.2.5.0'; channel = 'development'; builtUtc = [DateTime]::UtcNow.ToString('o'); signature = 'unsigned'; files = @($files) }
$buildManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $testRoot 'build-manifest.json') -Encoding UTF8
Write-Output ('Isolated development build succeeded: ' + $outputExe)

