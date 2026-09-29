param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$NewExecutable,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PreviousExecutable,
    [string]$PreviousVersion
)

$ErrorActionPreference = 'Stop'
if ($Tag -notmatch '^v(\d+\.\d+\.\d+)$') { throw 'Client release tag must be vMAJOR.MINOR.PATCH.' }
$targetVersion = [version]$Matches[1]
$newFile = (Resolve-Path -LiteralPath $NewExecutable).Path
$output = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
$tool = Join-Path $PSScriptRoot 'xdelta3.exe'
if (-not (Test-Path -LiteralPath $tool)) { throw 'Pinned xdelta3 tool is missing.' }
$expectedToolHash = '2081FB24A7B8A89D068B58E7D9353647D5FC1512A68BB6A37F71FC60CA84D943'
if ((Get-FileHash -LiteralPath $tool -Algorithm SHA256).Hash -ne $expectedToolHash) { throw 'Pinned xdelta3 tool hash mismatch.' }

$localPrevious = $PreviousExecutable -and $PreviousVersion
if (($PreviousExecutable -and -not $PreviousVersion) -or ($PreviousVersion -and -not $PreviousExecutable)) {
    throw 'PreviousExecutable and PreviousVersion must be supplied together.'
}
if ($localPrevious) {
    $oldSource = (Resolve-Path -LiteralPath $PreviousExecutable).Path
    if ($PreviousVersion -notmatch '^\d+\.\d+\.\d+$' -or [version]$PreviousVersion -ge $targetVersion) {
        throw 'PreviousVersion must be an earlier three-part version.'
    }
    $previous = [pscustomobject]@{ tag_name = "v$PreviousVersion" }
    $oldAsset = [pscustomobject]@{
        size = (Get-Item -LiteralPath $oldSource).Length
        digest = 'sha256:' + (Get-FileHash -LiteralPath $oldSource -Algorithm SHA256).Hash.ToLowerInvariant()
    }
} else {
    $headers = @{ 'User-Agent' = 'ResourceManager-Release' }
    if ($env:GH_TOKEN) { $headers['Authorization'] = "Bearer $env:GH_TOKEN" }
    $releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/FennecMomo/ResourceManager/releases?per_page=100' -Headers $headers -TimeoutSec 30
    $previous = $releases | Where-Object {
        -not $_.draft -and -not $_.prerelease -and $_.tag_name -match '^v\d+\.\d+\.\d+$' -and
        [version]($_.tag_name.Substring(1)) -lt $targetVersion
    } | Sort-Object { [version]($_.tag_name.Substring(1)) } -Descending | Select-Object -First 1
    if ($null -eq $previous) { Write-Output 'No previous official client release; publishing full EXE only.'; return }
    $oldAsset = $previous.assets | Where-Object name -eq 'ResourceManager.exe' | Select-Object -First 1
    if ($null -eq $oldAsset) { Write-Output 'Previous release has no client EXE; publishing full EXE only.'; return }
}

$temporary = Join-Path $output ('delta-stage-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $oldFile = Join-Path $temporary 'old.exe'
    $reconstructed = Join-Path $temporary 'reconstructed.exe'
    if ($localPrevious) { Copy-Item -LiteralPath $oldSource -Destination $oldFile }
    else { Invoke-WebRequest -Uri $oldAsset.browser_download_url -OutFile $oldFile -TimeoutSec 300 }
    if ((Get-Item -LiteralPath $oldFile).Length -ne $oldAsset.size) { throw 'Previous release EXE size mismatch.' }
    $oldHash = (Get-FileHash -LiteralPath $oldFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($oldAsset.digest -and $oldAsset.digest -ne "sha256:$oldHash") { throw 'Previous release EXE hash mismatch.' }
    $newHash = (Get-FileHash -LiteralPath $newFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $patchName = "ResourceManager-$($previous.tag_name)-to-$Tag.xdelta"
    $patch = Join-Path $output $patchName
    & $tool -9 -S lzma -e -f -s $oldFile $newFile $patch
    if ($LASTEXITCODE -ne 0) { throw 'Delta creation failed.' }
    $patchBytes = (Get-Item -LiteralPath $patch).Length
    $newBytes = (Get-Item -LiteralPath $newFile).Length
    if ($patchBytes -ge $newBytes * 0.6) {
        Remove-Item -LiteralPath $patch -Force
        Write-Output "Patch is too large ($patchBytes bytes); publishing full EXE only."
        return
    }
    & $tool -d -f -s $oldFile $patch $reconstructed
    if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $reconstructed -Algorithm SHA256).Hash.ToLowerInvariant() -ne $newHash) {
        throw 'Patch reconstruction differs from the official full EXE.'
    }
    $manifest = [ordered]@{
        schema = 1
        algorithm = 'xdelta3-3.2.1-lzma'
        fromVersion = $previous.tag_name.Substring(1)
        toVersion = $Tag.Substring(1)
        fromSha256 = $oldHash
        toSha256 = $newHash
        toSize = $newBytes
        patchSha256 = (Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash.ToLowerInvariant()
        patchSize = $patchBytes
        patchUrl = "https://github.com/FennecMomo/ResourceManager/releases/download/$Tag/$patchName"
    }
    $manifestPath = Join-Path $output 'ResourceManager-delta.json'
    [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Compress), [System.Text.UTF8Encoding]::new($false))
    Write-Output "Created $patchName ($patchBytes bytes) from $($previous.tag_name)."
}
finally {
    $resolvedOutput = [System.IO.Path]::GetFullPath($output)
    $resolvedTemporary = [System.IO.Path]::GetFullPath($temporary)
    if ($resolvedTemporary.StartsWith($resolvedOutput + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force -ErrorAction SilentlyContinue
    }
}
