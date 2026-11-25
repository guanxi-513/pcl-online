param([string]$Root = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$encoding = [Text.UTF8Encoding]::new($false)
$replacements = [ordered]@{
    'PCL\' = 'CPT\'
    'PCL.ini' = 'CPT.ini'
    '.pclignore' = '.cptignore'
    'PCL2 Skin.zip' = 'CPT Skin.zip'
    '"PCL""' = '"CPT""'
}
$changed = 0
Get-ChildItem -LiteralPath (Join-Path $Root 'Plain Craft Launcher 2') -Recurse -File | Where-Object {
    $_.Extension -in '.vb','.xaml' -and $_.FullName -notmatch '\\(obj|bin)\\' -and $_.Name -ne 'ModDataPaths.vb'
} | ForEach-Object {
    $before = [IO.File]::ReadAllText($_.FullName)
    $after = $before
    foreach ($key in $replacements.Keys) { $after = $after.Replace($key, $replacements[$key]) }
    if ($after -cne $before) { [IO.File]::WriteAllText($_.FullName,$after,$encoding); $changed++ }
}
Write-Output "Isolated CPT file paths in $changed source files."
$remote = [ordered]@{ '.pcl-' = '.cpt-'; 'pcl-console' = 'cpt-console'; 'pcl-installer' = 'cpt-installer' }
$files = @(
    (Join-Path $Root 'PCLCS/Server/ServerManager.cs'),
    (Join-Path $Root 'PCLCS/Server/Scripts/agent.py'),
    (Join-Path $Root 'PCLCS/Server/Scripts/bootstrap.ps1'),
    (Join-Path $Root 'tests/test_server_agent.py'),
    (Join-Path $Root 'tests/server-pack-smoke.ps1')
)
foreach ($file in $files) {
    $before = [IO.File]::ReadAllText($file)
    $after = $before
    foreach ($key in $remote.Keys) { $after = $after.Replace($key,$remote[$key]) }
    if ($after -cne $before) { [IO.File]::WriteAllText($file,$after,$encoding) }
}
