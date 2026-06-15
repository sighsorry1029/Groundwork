param ($manifestFile, $versionString)

try {
    $manifest = Get-Content -Raw -Path $manifestFile
    $manifest = $manifest -replace '"version_number":\s*"([^"]*)"', "`"version_number`": `"$versionString`""
    Set-Content -Path $manifestFile -Value $manifest -Encoding UTF8
} catch {
    Write-Error $_.Exception.Message
}
