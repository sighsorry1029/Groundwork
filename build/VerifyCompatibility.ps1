param(
    [Parameter(Mandatory = $true)][string] $ModDll,
    [Parameter(Mandatory = $true)][string] $GameManaged,
    [Parameter(Mandatory = $true)][string] $BepInExCore,
    [string] $CecilDll = "$BepInExCore/Mono.Cecil.dll"
)
$ErrorActionPreference = 'Stop'
[Reflection.Assembly]::Load([IO.File]::ReadAllBytes($CecilDll)) | Out-Null
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory($GameManaged)
$resolver.AddSearchDirectory($BepInExCore)
$resolver.AddSearchDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ModDll)))
$options = [Mono.Cecil.ReaderParameters]::new()
$options.AssemblyResolver = $resolver
$mod = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($ModDll, $options)
$failures = [Collections.Generic.List[string]]::new()
$references = [Collections.Generic.HashSet[string]]::new()
$targets = [Collections.Generic.HashSet[string]]::new()
function Is-GameScope($reference) {
    return $reference.Scope.Name -match '^(assembly_|UnityEngine|gui_framework|SoftReferenceableAssets|Splatform)'
}
function Find-Field($type, $name) {
    while ($type) {
        $field = $type.Fields | Where-Object Name -eq $name | Select-Object -First 1
        if ($field) { return $field }
        $type = if ($type.BaseType) { $type.BaseType.Resolve() } else { $null }
    }
}
try {
    foreach ($reference in $mod.MainModule.GetTypeReferences()) {
        if (!(Is-GameScope $reference)) { continue }
        try { if (!$reference.Resolve()) { $failures.Add("Missing type: $reference") } }
        catch { $failures.Add("Unresolved type: $reference : $_") }
    }
    foreach ($type in $mod.MainModule.GetTypes()) {
        foreach ($method in $type.Methods) {
            if (!$method.HasBody) { continue }
            foreach ($instruction in $method.Body.Instructions) {
                $member = $instruction.Operand -as [Mono.Cecil.MemberReference]
                if (!$member -or !$member.DeclaringType -or !(Is-GameScope $member.DeclaringType)) { continue }
                if ($member -isnot [Mono.Cecil.MethodReference] -and $member -isnot [Mono.Cecil.FieldReference]) { continue }
                [void]$references.Add($member.FullName)
                try {
                    $resolved = $member.Resolve()
                    if (!$resolved) { $failures.Add("Missing member: $member used by $method"); continue }
                    if ($resolved -is [Mono.Cecil.FieldDefinition] -and $resolved.IsLiteral) {
                        $failures.Add("Literal field instruction: $instruction in $method")
                    }
                    if ($resolved.IsPrivate -or $resolved.IsAssembly) {
                        $failures.Add("Direct non-public game access: $member in $method")
                    }
                }
                catch { $failures.Add("Unresolved member: $member : $_") }
            }
        }

        # Class-level patches and method-level patches (ServerSync uses both).
        $classPatches = @($type.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' -and $_.ConstructorArguments.Count -ge 2 })
        $patchSites = @()
        foreach ($attribute in $classPatches) {
            $patchSites += @{ Attribute = $attribute; Methods = @($type.Methods | Where-Object { $_.Name -in @('Prefix','Postfix','Finalizer','Transpiler') }) }
        }
        foreach ($method in $type.Methods) {
            foreach ($attribute in @($method.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' -and $_.ConstructorArguments.Count -ge 2 })) {
                $patchSites += @{ Attribute = $attribute; Methods = @($method) }
            }
        }
        foreach ($site in $patchSites) {
            $arguments = $site.Attribute.ConstructorArguments
            $targetType = $arguments[0].Value.Resolve()
            $name = [string]$arguments[1].Value
            $candidates = @($targetType.Methods | Where-Object Name -eq $name)
            if ($arguments.Count -gt 2) {
                $signature = (@($arguments[2].Value | ForEach-Object { $_.Value.FullName }) -join ',')
                $candidates = @($candidates | Where-Object { ($_.Parameters.ParameterType.FullName -join ',') -eq $signature })
            }
            if ($candidates.Count -ne 1) { $failures.Add("Patch target $($targetType.FullName)::$name has $($candidates.Count) candidates in $($type.FullName)"); continue }
            $target = $candidates[0]
            [void]$targets.Add($target.FullName)
            foreach ($patch in $site.Methods) {
                foreach ($parameter in $patch.Parameters) {
                    $parameterName = $parameter.Name
                    if ($parameterName.StartsWith('___')) {
                        if (!(Find-Field $targetType $parameterName.Substring(3))) { $failures.Add("Missing injected field $parameterName for $patch") }
                    }
                    elseif (!$parameterName.StartsWith('__') -and $patch.Name -ne 'Transpiler') {
                        if ($parameterName -notin $target.Parameters.Name) { $failures.Add("Missing patch argument $parameterName for $patch -> $target") }
                    }
                }
            }
        }
    }
    Write-Host "Checked $($references.Count) direct game/Unity member references and $($targets.Count) Harmony targets in $ModDll against $GameManaged."
    if ($failures.Count) {
        $failures | Sort-Object -Unique | ForEach-Object { Write-Host $_ }
        throw "$($failures.Count) compatibility failures."
    }
    Write-Host 'Static compatibility checks passed. This does not execute Unity or multiplayer.'
}
finally {
    $mod.Dispose()
    $resolver.Dispose()
}
