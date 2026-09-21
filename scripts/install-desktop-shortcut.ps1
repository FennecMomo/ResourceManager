param(
    [string]$SourceExe = (Join-Path $PSScriptRoot '..\dist\win-x64\ResourceManager.exe')
)

$ErrorActionPreference = 'Stop'

$source = (Resolve-Path -LiteralPath $SourceExe).Path
$installDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\ResourceManager'
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
$target = Join-Path $installDir 'ResourceManager.exe'
Copy-Item -LiteralPath $source -Destination $target -Force

$desktop = [Environment]::GetFolderPath('DesktopDirectory')
$shortcutPath = Join-Path $desktop '资源管理器.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $target
$shortcut.WorkingDirectory = $installDir
$shortcut.IconLocation = "$target,0"
$shortcut.Description = '公司内网点对点资源管理器'
$shortcut.Save()

Write-Output "程序：$target"
Write-Output "快捷方式：$shortcutPath"
