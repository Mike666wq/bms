param([string]$Version='1.2.2', [string]$BuildDirectory='.testing\stage2')
$ErrorActionPreference = 'Stop'
$demoRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$buildRoot = [IO.Path]::GetFullPath((Join-Path $demoRoot $BuildDirectory))
if (-not $buildRoot.StartsWith($demoRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '构建目录必须位于当前项目内' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw '版本号格式无效' }
$exePath = Join-Path $buildRoot 'BmsRealtimeDemo.exe'
$resultPath = Join-Path $buildRoot 'self-test-result.txt'
if (-not (Test-Path -LiteralPath $resultPath)) { throw '请先运行 run-self-test.ps1' }
$result = Get-Content -LiteralPath $resultPath -Raw
if (-not $result.TrimStart([char]0xFEFF).StartsWith('PASS:') -or (Get-Item -LiteralPath $resultPath).LastWriteTimeUtc -lt (Get-Item -LiteralPath $exePath).LastWriteTimeUtc) { throw '当前构建没有新鲜的 PASS 测试结果' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).FileVersion -ne ($Version+'.0')) { throw '包版本与 EXE 版本不一致' }
$releaseRoot = Join-Path $demoRoot 'release'
$publishedZip = Join-Path $releaseRoot ('BmsRealtimeDemo-Windows-'+$Version+'.zip')
if (Test-Path -LiteralPath $publishedZip) { throw '此版本包已存在，未覆盖。请使用新的版本号。' }
$stageRoot = Join-Path $demoRoot ('.package-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$appRoot = Join-Path $stageRoot 'BmsRealtimeDemo'
$runtimeFiles = [ordered]@{
 'BmsRealtimeDemo.exe'=$exePath
 'BmsRealtimeDemo.exe.config'=(Join-Path $buildRoot 'BmsRealtimeDemo.exe.config')
 'lib/System.Data.SQLite.dll'=(Join-Path $demoRoot 'lib\System.Data.SQLite.dll')
 'x64/SQLite.Interop.dll'=(Join-Path $demoRoot 'x64\SQLite.Interop.dll')
 'x86/SQLite.Interop.dll'=(Join-Path $demoRoot 'x86\SQLite.Interop.dll')
 '运行与迁移说明.md'=(Join-Path $demoRoot '运行与迁移说明.md')
}
try {
 New-Item -ItemType Directory -Path $appRoot -Force | Out-Null
 $hashes = @()
 foreach ($relative in $runtimeFiles.Keys) {
  $inputFile = $runtimeFiles[$relative]
  if (-not (Test-Path -LiteralPath $inputFile -PathType Leaf)) { throw ('缺少运行依赖：' + $relative) }
  $outputFile = Join-Path $appRoot $relative
  New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($outputFile)) -Force | Out-Null
  Copy-Item -LiteralPath $inputFile -Destination $outputFile
  $hashes += [ordered]@{ path=$relative; sha256=(Get-FileHash -LiteralPath $outputFile -Algorithm SHA256).Hash; bytes=(Get-Item -LiteralPath $outputFile).Length }
 }
 $manifest = [ordered]@{ app='BmsRealtimeDemo'; version=$Version; built_at=[DateTimeOffset]::Now.ToString('o'); platform='Windows x86/x64'; signature='unsigned'; validation=$result.Trim(); files=$hashes }
 [IO.File]::WriteAllText((Join-Path $appRoot 'package-manifest.json'),($manifest | ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($false)))
 $stagedZip = Join-Path $stageRoot 'package.zip'
 Compress-Archive -LiteralPath $appRoot -DestinationPath $stagedZip
 New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
 [IO.File]::Move($stagedZip,$publishedZip)
 Write-Output ('运行包：' + $publishedZip)
 Get-FileHash -LiteralPath $publishedZip -Algorithm SHA256
}
finally {
 $checkedStage = [IO.Path]::GetFullPath($stageRoot)
 if (-not $checkedStage.StartsWith($demoRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '打包临时目录越界' }
 if (Test-Path -LiteralPath $checkedStage) { Remove-Item -LiteralPath $checkedStage -Recurse -Force }
}