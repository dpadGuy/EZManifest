param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDir,
    [Parameter(Mandatory = $true)]
    [string] $DownloadUrl
)

$ErrorActionPreference = "Stop"

$dest = Join-Path $PublishDir "SteamAutoCrack.CLI"
$exe = Join-Path $dest "SteamAutoCrack.CLI.exe"
if (Test-Path -LiteralPath $exe) {
    Write-Host "SteamAutoCrack.CLI already present."
    exit 0
}

$zip = Join-Path $env:TEMP "SteamAutoCrack.CLI.zip"
$stage = Join-Path $env:TEMP ("sac_" + [Guid]::NewGuid().ToString("n"))

Write-Host "Downloading $DownloadUrl"
Invoke-WebRequest -Uri $DownloadUrl -OutFile $zip

New-Item -ItemType Directory -Force -Path $stage | Out-Null
Expand-Archive -LiteralPath $zip -DestinationPath $stage -Force

$found = Get-ChildItem -LiteralPath $stage -Recurse -Filter "SteamAutoCrack.CLI.exe" | Select-Object -First 1
if ($null -eq $found) {
    throw "SteamAutoCrack.CLI.exe was not inside the zip."
}

New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Path (Join-Path $found.DirectoryName "*") -Destination $dest -Recurse -Force
Write-Host "Staged SteamAutoCrack.CLI -> $dest"
