param([string]$Root = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
[Reflection.Assembly]::LoadFrom((Join-Path $Root 'PCLCS/bin/Newtonsoft.Json.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $Root 'PCLCS/bin/PCLCS.dll')) | Out-Null
$directory = Join-Path $env:TEMP ('cpt-pack-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
function Archive($path,$entries) {
    $stream = [IO.File]::Create($path)
    $zip = [IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $entries.Keys) {
            $entry = $zip.CreateEntry($name)
            $output = $entry.Open()
            try { $data = $entries[$name]; $output.Write($data,0,$data.Length) } finally { $output.Dispose() }
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
}
function Utf8($text) { [Text.Encoding]::UTF8.GetBytes($text) }
try {
    $jar = Join-Path $directory 'server.jar'
    Archive $jar @{'version.json'=(Utf8 '{"id":"1.21.1","java_version":21}')}
    $metadata = [PCLCS.Server.ServerPack]::Inspect($jar)
    if ($metadata['version'].ToString() -ne '1.21.1' -or $metadata['javaMajor'].ToString() -ne '21') { throw 'Single JAR version detection failed.' }
    $pack = Join-Path $directory 'pack.zip'
    Archive $pack @{'Pack/server.jar'=[IO.File]::ReadAllBytes($jar); 'Pack/start.bat'=(Utf8 '@java -Xms2G -Xmx6G -XX:+UseG1GC -jar server.jar nogui'); 'Pack/server.properties'=(Utf8 'server-port=25566'); 'Pack/world/level.dat'=(Utf8 'world')}
    $metadata = [PCLCS.Server.ServerPack]::Inspect($pack)
    if ($metadata['memory'].ToString() -ne '6144' -or $metadata['port'].ToString() -ne '25566' -or $metadata['prefix'].ToString() -ne 'Pack/') { throw 'Pack config detection failed.' }
    if ($metadata['sha256'].ToString().Length -ne 64) { throw 'Pack checksum missing.' }
    if ($metadata['minMemory'].ToString() -ne '2048') { throw 'Initial heap setting was not preserved.' }
    Archive $pack @{'Server/libraries/net/minecraftforge/forge/1.21.1-52.1.0/unix_args.txt'=(Utf8 '-cp libraries/example.jar'); 'Server/user_jvm_args.txt'=(Utf8 "-Xms1G`n-Xmx4G`n-XX:+UseG1GC")}
    $metadata=[PCLCS.Server.ServerPack]::Inspect($pack)
    if ($metadata['core'].ToString() -ne 'Forge' -or $metadata['loaderVersion'].ToString() -ne '52.1.0' -or $metadata['version'].ToString() -ne '1.21.1') { throw 'Forge argument-file detection failed.' }
    if ($metadata['minMemory'].ToString() -ne '1024') { throw 'JVM argument-file memory detection failed.' }
    foreach ($unsafe in '../escape','world\escape','CON.txt','.cpt-server.json') {
        Archive $pack @{$unsafe=(Utf8 'bad'); 'server.jar'=[IO.File]::ReadAllBytes($jar)}
        $rejected = $false
        try { [PCLCS.Server.ServerPack]::Inspect($pack) | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw "Unsafe pack accepted: $unsafe" }
    }
    Archive $pack @{'server.jar'=[IO.File]::ReadAllBytes($jar);'cpt-server.json'=(Utf8 '{"launchArgs":["-jar","server.jar","nogui"],"jvmArgs":["-javaagent:evil.jar"]}')}
    $rejected = $false
    try { [PCLCS.Server.ServerPack]::Inspect($pack) | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Unsafe JVM arguments accepted.' }
    Archive $pack @{'server.jar'=[IO.File]::ReadAllBytes($jar);'other.jar'=[IO.File]::ReadAllBytes($jar)}
    [PCLCS.Server.ServerPack]::Inspect($pack) | Out-Null
    Archive $pack @{'one.jar'=[IO.File]::ReadAllBytes($jar);'two.jar'=[IO.File]::ReadAllBytes($jar)}
    $rejected=$false
    try { [PCLCS.Server.ServerPack]::Inspect($pack) | Out-Null } catch { $rejected=$true }
    if (-not $rejected) { throw 'Ambiguous pack launcher accepted.' }
    Write-Output 'Server pack JAR, ZIP config detection, checksum and unsafe-path/JVM checks passed.'
} finally {
    $target = [IO.Path]::GetFullPath($directory)
    if ($target.StartsWith([IO.Path]::GetFullPath($env:TEMP) + '\',[StringComparison]::OrdinalIgnoreCase) -and (Split-Path $target -Leaf).StartsWith('cpt-pack-tests-')) { Remove-Item -LiteralPath $target -Recurse -Force }
}
