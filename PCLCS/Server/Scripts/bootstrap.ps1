$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$homePath = Join-Path $env:USERPROFILE '.cpt-server'
$pythonPath = Join-Path $homePath 'python\python.exe'
if (-not (Test-Path -LiteralPath $pythonPath)) {
    New-Item -ItemType Directory -Path $homePath -Force | Out-Null
    $archive = Join-Path $homePath 'python.zip'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip' -OutFile $archive
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $homePath 'python') -Force
    Remove-Item -LiteralPath $archive
}
& $pythonPath -c 'import sys; assert sys.version_info >= (3,10)'
if ($LASTEXITCODE -ne 0) { throw 'Python initialization failed.' }
Write-Output 'Environment ready.'
