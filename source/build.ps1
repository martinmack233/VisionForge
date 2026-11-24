param([string]$DotNet = 'dotnet', [switch]$Publish, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
$previousDotnetRoot = $env:DOTNET_ROOT
$previousPath = $env:PATH
try {
    $resolvedDotnet = (Get-Command $DotNet -ErrorAction Stop).Source
    $env:DOTNET_ROOT = Split-Path $resolvedDotnet -Parent
    $env:PATH = $env:DOTNET_ROOT + ';' + $env:PATH
    $sdk = & $DotNet --version
    if ($LASTEXITCODE -ne 0 -or $sdk -notlike '10.*') { throw '需要 global.json 指定的 .NET 10 SDK；可通过 -DotNet 指定已有 dotnet.exe。' }
    $restoreArguments = if ($Publish) { @('restore','VisionPlatform.sln','-r','win-x64') } else { @('restore','VisionPlatform.sln') }
    & $DotNet @restoreArguments; if ($LASTEXITCODE -ne 0) { throw '依赖还原失败。' }
    & (Join-Path $PSScriptRoot 'tools/write-build-identity.ps1') -ProjectRoot $PSScriptRoot
    & $DotNet build VisionPlatform.sln -c Release --no-restore; if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
    if (-not $SkipTests) { & $DotNet test VisionPlatform.sln -c Release --no-build --no-restore; if ($LASTEXITCODE -ne 0) { throw '回归失败，未发布。' } }
    if ($Publish) {
        [xml]$version = Get-Content -LiteralPath build/Version.props
        $name = 'VisionForge-' + $version.Project.PropertyGroup.VisionProductVersion + '-win-x64'
        $destination = Join-Path $PSScriptRoot ('artifacts/' + $name)
        if (Test-Path -LiteralPath $destination) { throw "发布目录已存在，拒绝覆盖：$destination" }
        foreach ($project in @('VisionPlatform.Editor.Wpf','VisionPlatform.Console','VisionPlatform.RuntimeHost','VisionPlatform.BoqWorker')) {
            $output = if ($project -eq 'VisionPlatform.BoqWorker') { Join-Path $destination 'CameraAdapters/Boq' } else { $destination }
            & $DotNet publish "src/$project/$project.csproj" -c Release -r win-x64 --self-contained true --no-restore -o $output
            if ($LASTEXITCODE -ne 0) { throw "发布失败：$project" }
        }
        Copy-Item -LiteralPath build/source-manifest.json -Destination (Join-Path $destination 'source-manifest.json')
        Write-Output "Publish binaries: $destination; use tools/package-visionforge.py to stage resources and create the verified ZIP."
    }
}
finally { $env:DOTNET_ROOT = $previousDotnetRoot; $env:PATH = $previousPath; Pop-Location }
