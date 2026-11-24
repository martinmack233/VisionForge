param([string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $ProjectRoot).Path
$excluded = @('bin','obj','.git','.vs','.venv','venv','__pycache__','.pytest_cache','artifacts','test-results','TestResults')
$files = [System.Collections.Generic.List[object]]::new()
foreach ($directory in @('src','tests','third_party','tools','build','resources','Examples','AIExamples','licenses','integrations','components')) {
    $path = Join-Path $root $directory
    if (-not (Test-Path -LiteralPath $path)) { continue }
    Get-ChildItem -LiteralPath $path -Recurse -File | ForEach-Object {
        $relative = [System.IO.Path]::GetRelativePath($root, $_.FullName).Replace('\','/')
        $segments = $relative.Split('/')
        # Optional vendor SDK is not a compiler input or a redistributable source resource.
        if ($relative.StartsWith('third_party/boq/sdk/', [StringComparison]::OrdinalIgnoreCase)) { return }
        if (($segments | Where-Object { $_ -in $excluded -or $_ -like 'python-service-tmp*' }).Count -gt 0) { return }
        if ($relative -in @('build/BuildIdentity.props','build/source-manifest.json') -or $_.Extension -in @('.pt','.onnx','.db','.sqlite','.sqlite3','.pyc','.exe','.dll') -or $_.Name -like '.env*') { return }
        $files.Add([ordered]@{path=$relative;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()})
    }
}
Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Name -in @('Directory.Build.props','Directory.Build.targets','global.json','VisionPlatform.sln','build.ps1','README.md') } | ForEach-Object { $files.Add([ordered]@{path=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}) }
$ordered = @($files | Sort-Object { $_.path })
$lines = ($ordered | ForEach-Object { $_.sha256 + '  ' + $_.path }) -join "`n"
$sha = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($lines))).ToLowerInvariant()
$commit = (git -C $root rev-parse HEAD).Trim()
$state = if (git -C $root status --porcelain --untracked-files=normal) { 'dirty' } else { 'clean' }
$manifest = [ordered]@{version=1;sourceCommit=$commit;sourceState=$state;sha256=$sha;files=$ordered}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'build/source-manifest.json') -Encoding utf8
@"
<Project><PropertyGroup><VisionSourceCommit>$commit</VisionSourceCommit><VisionSourceState>$state</VisionSourceState><VisionSourceHash>$sha</VisionSourceHash></PropertyGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $root 'build/BuildIdentity.props') -Encoding utf8
Write-Output "Source $state $commit SHA256=$sha ($($ordered.Count) files)"
