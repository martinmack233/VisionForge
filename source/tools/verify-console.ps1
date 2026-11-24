param([string]$Executable = 'src/VisionPlatform.Console/bin/Release/net10.0/VisionPlatform.Console.exe')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $root
try {
    $application = (Resolve-Path -LiteralPath $Executable).Path
    New-Item -ItemType Directory -Path test-results/console -Force | Out-Null
    function Invoke-Console([string]$Name, [string[]]$Arguments, [int]$Expected = 0) {
        & $application @Arguments 2>&1 | Tee-Object -FilePath "test-results/console/$Name.log" | Out-Host
        if ($LASTEXITCODE -ne $Expected) { throw "$Name returned $LASTEXITCODE, expected $Expected" }
    }
    Invoke-Console 'demo' @('demo','samples/DemoInspection')
    Invoke-Console 'reload-first' @('run','samples/DemoInspection/flow-b.json','--outputs','artifacts/console/reload-first')
    Invoke-Console 'reload-second' @('run','samples/DemoInspection/flow-b.json','--outputs','artifacts/console/reload-second')
    function Measurement([string]$Path) {
        $result = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
        $match = $result.Nodes.match.Outputs.MatchResult
        [ordered]@{
            Match = [ordered]@{ Success=$match.Success; Score=$match.Score; CenterX=$match.CenterX; CenterY=$match.CenterY; Angle=$match.AngleDegrees; Scale=$match.Scale }
            ROI = $result.Nodes.roi.Outputs.ROI
            Blobs = $result.Nodes.blob.Outputs.BlobResults
            Decision = $result.Nodes.decision.Outputs.Decision
        } | ConvertTo-Json -Depth 12 -Compress
    }
    $first = Measurement 'artifacts/console/reload-first/results.json'
    $second = Measurement 'artifacts/console/reload-second/results.json'
    if ($first -cne $second) { throw 'Independent process reload measurements differ.' }
    Invoke-Console 'shifted' @('run','samples/DemoInspection/flow-b.json','--image','samples/DemoInspection/shifted.png')
    Invoke-Console 'ng' @('run','samples/DemoInspection/flow-a.json','--image','samples/DemoInspection/ng.png') 2
    Invoke-Console 'absent' @('run','samples/DemoInspection/flow-a.json','--image','samples/DemoInspection/absent.png') 2
    Invoke-Console 'match' @('match','samples/DemoInspection/sample.png','samples/DemoInspection/template.json')
    Invoke-Console 'blur' @('blur','samples/DemoInspection/sample.png','artifacts/console/blur.png','5','1.2')
    Invoke-Console 'blur-invalid' @('blur','samples/DemoInspection/sample.png','artifacts/console/invalid.png','4','1.2') 2
    Invoke-Console 'prefix' @('run','samples/DemoInspection/flow-b.json','--to','match')
    [ordered]@{ Passed=$true; SeparateProcessMeasurementsEqual=$true; Commands=10; Measurement=($first | ConvertFrom-Json) } |
        ConvertTo-Json -Depth 14 | Out-File -Encoding utf8 test-results/console/summary.json
    Write-Host 'Console acceptance passed; independent process measurements are identical.'
}
finally { Pop-Location }
