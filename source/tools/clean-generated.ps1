param([switch]$Apply, [string]$LatestDirectory = 'artifacts/VisionForge-0.6.3-preview.2-win-x64')
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath (Split-Path $PSScriptRoot -Parent)).Path
$artifacts=(Join-Path $root 'artifacts')
$latest=[IO.Path]::GetFullPath((Join-Path $root $LatestDirectory))
if(-not $latest.StartsWith($artifacts+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Latest directory is outside artifacts.'}
if(-not (Test-Path -LiteralPath (Join-Path $latest 'VisionForge.exe'))){throw 'A verified new VisionForge directory is required before cleanup.'}
$running=@(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -and $_.ExecutablePath.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)} | ForEach-Object {$_.ExecutablePath})
$candidates=[Collections.Generic.List[string]]::new()
foreach($item in Get-ChildItem -LiteralPath $artifacts -Force){
 if($item.PSIsContainer -and ($item.Name -like 'VisionStudio-*-win-x64' -or $item.Name -like 'phase5-ai-*-build')){$candidates.Add($item.FullName)}
 elseif(-not $item.PSIsContainer -and $item.Extension -eq '.zip' -and ($item.Name -like 'VisionStudio-*' -or $item.Name -like 'VisionPlatform-*')){$candidates.Add($item.FullName)}
}
foreach($base in @('src','tests','tools','third_party')){
 Get-ChildItem -LiteralPath (Join-Path $root $base) -Directory -Recurse -Force | Where-Object {$_.Name -in @('bin','obj','__pycache__','.pytest_cache') -and $_.FullName -notmatch '[\\/]\.venv[\\/]|[\\/]boq[\\/]sdk[\\/]'} | ForEach-Object {$candidates.Add($_.FullName)}
}
$chosen=@($candidates | Sort-Object Length | ForEach-Object {$_})
$accepted=[Collections.Generic.List[string]]::new();$rows=[Collections.Generic.List[object]]::new()
foreach($path in $chosen){
 if($accepted | Where-Object {$path.StartsWith($_+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)}){continue}
 $absolute=(Resolve-Path -LiteralPath $path).Path
 if(-not $absolute.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $absolute -eq $latest){throw "Unsafe cleanup target: $absolute"}
 $item=Get-Item -LiteralPath $absolute -Force
 if($running | Where-Object {$_ -eq $absolute -or $_.StartsWith($absolute+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)}){$rows.Add([pscustomobject]@{path=$absolute;state='PreservedRunning';bytes=0});continue}
 if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){$rows.Add([pscustomobject]@{path=$absolute;state='PreservedReparse';bytes=0});continue}
 $all=if($item.PSIsContainer){@(Get-ChildItem -LiteralPath $absolute -Recurse -Force)}else{@($item)}
 if($all | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}){$rows.Add([pscustomobject]@{path=$absolute;state='PreservedReparseChild';bytes=0});continue}
 if($item.PSIsContainer -and ($all | Where-Object {-not $_.PSIsContainer -and $_.Extension.ToLowerInvariant() -in @('.pt','.onnx','.db','.sqlite','.sqlite3')})){$rows.Add([pscustomobject]@{path=$absolute;state='PreservedPotentialUserData';bytes=0});continue}
 $bytes=($all | Where-Object {-not $_.PSIsContainer} | Measure-Object Length -Sum).Sum
 # Keep small historical verification/identity evidence required by existing reports; remove old executables and duplicated resources.
 if($item.PSIsContainer -and $item.Name -eq 'VisionStudio-0.6.3-preview.1-win-x64'){
  foreach($child in Get-ChildItem -LiteralPath $absolute -Force){
   if($child.Name -in @('verification','component-manifest.json','source-manifest.json','file-sha256.json')){continue}
   $resolvedChild=[IO.Path]::GetFullPath($child.FullName)
   if(-not $resolvedChild.StartsWith($absolute+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Historical evidence cleanup boundary failed.'}
   if($Apply){Remove-Item -LiteralPath $resolvedChild -Recurse -Force}
  }
  $remaining=if($Apply){(Get-ChildItem -LiteralPath $absolute -File -Recurse -Force | Measure-Object Length -Sum).Sum}else{0}
  $rows.Add([pscustomobject]@{path=$absolute;state=$(if($Apply){'RemovedOldRuntimeRetainedEvidence'}else{'PlannedEvidenceOnly'});bytes=([long]$bytes-[long]$remaining)})
 }else{
  if($Apply){Remove-Item -LiteralPath $absolute -Recurse -Force}
  $rows.Add([pscustomobject]@{path=$absolute;state=$(if($Apply){'Removed'}else{'Planned'});bytes=[long]$bytes})
 }
 $accepted.Add($absolute)
}
$record=[ordered]@{Applied=[bool]$Apply;Root=$root;Latest=$latest;FreedBytes=[long](($rows | Where-Object {$_.state -in @('Removed','RemovedOldRuntimeRetainedEvidence')} | Measure-Object bytes -Sum).Sum);Entries=$rows;UserDataDirectoriesTouched=$false;GitHistoryChanged=$false}
$out=Join-Path $root 'test-results/visionforge-20261118/cleanup-result.json';$record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $out -Encoding utf8
$record | ConvertTo-Json -Depth 3
