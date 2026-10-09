param(
    [Parameter(Mandatory = $true)][string]$SourceExe
)

# Keep this script UTF-8 with BOM so Windows PowerShell 5.1 reads the Chinese shortcut name.
$ErrorActionPreference = 'Stop'

$source = (Resolve-Path -LiteralPath $SourceExe).Path
$sourceFile = Get-Item -LiteralPath $source
if ($sourceFile.Name -ne 'ResourceManager.exe' -or -not $sourceFile.VersionInfo.FileVersion) {
    throw 'SourceExe must be a built ResourceManager.exe.'
}
$installDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\ResourceManager'
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
$target = Join-Path $installDir 'ResourceManager.exe'
if ($source -ne $target) {
    $running = @(Get-CimInstance Win32_Process -Filter "Name='ResourceManager.exe'" |
        Where-Object { $_.ExecutablePath -eq $target })
    # The user authorized terminating stateless MCP hosts that lock the fixed EXE.
    # Desktop clients still use the graceful local-control workflow first.
    $desktopClients = @($running | Where-Object { $_.CommandLine -notmatch '(?:^|\s)--mcp(?:\s|$)' })
    if ($desktopClients.Count -gt 0) {
        throw 'Installation is in use by a desktop client. Gracefully stop it before replacing the EXE.'
    }
    foreach ($hostProcess in $running) {
        $process = Get-Process -Id $hostProcess.ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $process) { continue }
        $current = Get-CimInstance Win32_Process -Filter "ProcessId=$($hostProcess.ProcessId)"
        if ($current.ExecutablePath -ne $target -or $current.CommandLine -notmatch '(?:^|\s)--mcp(?:\s|$)') {
            throw 'MCP process identity changed; installation aborted.'
        }
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        if (-not $process.WaitForExit(10000)) { throw 'MCP host did not exit.' }
    }
    $staged = Join-Path $installDir 'ResourceManager.exe.installing'
    try {
        Copy-Item -LiteralPath $source -Destination $staged -Force
        $expected = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $expected) { throw 'Staged file hash mismatch.' }
        Move-Item -LiteralPath $staged -Destination $target -Force
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expected) { throw 'Installed file hash mismatch.' }
    } finally {
        if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Force }
    }
}

$desktop = [Environment]::GetFolderPath('DesktopDirectory')
$shortcutPath = Join-Path $desktop '资源管理器.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $target
$shortcut.WorkingDirectory = $installDir
$shortcut.Arguments = ''
$shortcut.IconLocation = "$target,0"
$shortcut.Description = '公司内网点对点资源管理器'
$shortcut.Save()
$verified = $shell.CreateShortcut($shortcutPath)
if ($verified.TargetPath -ne $target -or $verified.WorkingDirectory -ne $installDir) { throw 'Shortcut verification failed.' }

Write-Output "程序：$target"
Write-Output "快捷方式：$shortcutPath"
