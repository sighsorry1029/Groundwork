param ($manifestFile, $versionString)

try {
    $manifest = Get-Content -Raw -LiteralPath $manifestFile -Encoding UTF8
    $manifest = $manifest -replace '"version_number":\s*"([^"]*)"', "`"version_number`": `"$versionString`""
    [System.IO.File]::WriteAllText($manifestFile, $manifest, [System.Text.UTF8Encoding]::new($false))
} catch {
    Write-Error $_.Exception.Message
}
