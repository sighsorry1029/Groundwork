param(
    [string] $GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [string] $GameManaged = '',
    [string] $ModDll = "$PSScriptRoot\..\bin\Debug\Groundwork.dll"
)
$ErrorActionPreference = 'Stop'
$managed = if ($GameManaged) { $GameManaged } else { Join-Path $GamePath 'valheim_Data\Managed' }
$core = Join-Path $GamePath 'BepInEx\core'
$runtime = Join-Path $GamePath 'MonoBleedingEdge\EmbedRuntime'
$config = Join-Path $GamePath 'MonoBleedingEdge\etc'
$framework = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdk = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdk -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A .NET SDK is required.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
$directory = Join-Path ([IO.Path]::GetTempPath()) ('Groundwork-Mono-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$hostExe = Join-Path $directory 'CompatibilityMonoHost.exe'
$probe = Join-Path $directory 'CompatibilityProbe.dll'
$references = @('mscorlib.dll','System.dll','System.Core.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
& dotnet $compiler /nologo /noconfig /nostdlib+ /langversion:latest @references /target:exe /platform:x64 "/out:$hostExe" "$PSScriptRoot/CompatibilityMonoHost.cs"
if ($LASTEXITCODE) { throw 'Mono host compilation failed.' }
$gameReferences = @('assembly_valheim.dll','assembly_utils.dll','UnityEngine.CoreModule.dll','netstandard.dll') | ForEach-Object { '/reference:' + (Join-Path $managed $_) }
$harmonyReference = '/reference:' + (Join-Path $core '0Harmony.dll')
& dotnet $compiler /nologo /noconfig /nostdlib+ /langversion:latest @references @gameReferences $harmonyReference /target:library "/out:$probe" "$PSScriptRoot/CompatibilityProbe.cs" "$PSScriptRoot/HarvestSkillProbe.cs"
if ($LASTEXITCODE) { throw 'Mono probe compilation failed.' }
$isolatedMod = Join-Path $directory 'Groundwork.dll'
Copy-Item -LiteralPath $ModDll -Destination $isolatedMod
# Only this disposable test folder receives dependencies. Never copy them into the game.
Get-ChildItem -LiteralPath $core -Filter *.dll | Copy-Item -Destination $directory
$stdout = Join-Path $directory 'stdout.txt'
$stderr = Join-Path $directory 'stderr.txt'
$arguments = @($runtime,$managed,$config,$probe,$isolatedMod) | ForEach-Object { '"' + $_ + '"' }
$process = Start-Process -FilePath $hostExe -ArgumentList $arguments -WorkingDirectory $directory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
try {
    $null = $process.Handle
    if (!$process.WaitForExit(30000)) { $process.Kill(); $process.WaitForExit(); throw 'Mono probe timed out.' }
    Get-Content -LiteralPath $stdout,$stderr
    if ($process.ExitCode) { throw "Mono probe failed with exit code $($process.ExitCode)." }
}
finally {
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
    Write-Host "Test artifacts retained: $directory"
}
