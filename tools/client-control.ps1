param(
    [ValidateSet('Status', 'Shutdown', 'Restart')][string]$Action = 'Status',
    [int]$ClientProcessId = 0,
    [string]$ExecutablePath,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
if ($TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 120) { throw 'TimeoutSeconds must be 1..120.' }

function Get-ClientProcesses {
    @(Get-Process -Name ResourceManager -ErrorAction SilentlyContinue | Where-Object {
        -not $_.HasExited -and $_.SessionId -eq (Get-Process -Id $PID).SessionId
    })
}

function Invoke-ClientCommand([int]$TargetProcessId, [string]$Command) {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.',
        "FennecMomo.ResourceManager.Control.v1.$TargetProcessId", [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(750)
        $reader = [System.IO.StreamReader]::new($pipe, [System.Text.UTF8Encoding]::new($false), $false, 1024, $true)
        $writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false), 1024, $true)
        try {
            $writer.WriteLine((@{ protocol = 1; command = $Command; processId = $TargetProcessId } | ConvertTo-Json -Compress))
            $writer.Flush()
            $read = $reader.ReadLineAsync()
            if (-not $read.Wait(5000)) { throw 'control_response_timeout' }
            if ($null -eq $read.Result) { throw 'control_connection_closed' }
            return ($read.Result | ConvertFrom-Json)
        } finally { $writer.Dispose(); $reader.Dispose() }
    } catch [System.TimeoutException] {
        throw "control_unavailable: PID $TargetProcessId has no supported local control endpoint. No window was activated and no process was killed."
    } finally { $pipe.Dispose() }
}

function Get-SelectedClient {
    $clients = Get-ClientProcesses
    if ($ClientProcessId -gt 0) { $clients = @($clients | Where-Object Id -eq $ClientProcessId) }
    if ($clients.Count -gt 1) { throw 'multiple_clients: specify -ClientProcessId; no process was changed.' }
    if ($clients.Count -eq 0) { return $null }
    return $clients[0]
}

if ($Action -eq 'Status') {
    $clients = Get-ClientProcesses
    if ($ClientProcessId -gt 0) { $clients = @($clients | Where-Object Id -eq $ClientProcessId) }
    $result = @(foreach ($client in $clients) {
        try { Invoke-ClientCommand $client.Id 'status' }
        catch { @{ success = $false; error = $_.Exception.Message; processId = $client.Id; executable = $client.Path } }
    })
    ConvertTo-Json -InputObject $result -Depth 8
    return
}

$target = $null
if ($Action -eq 'Restart') {
    if ([string]::IsNullOrWhiteSpace($ExecutablePath)) { throw 'Restart requires -ExecutablePath.' }
    $target = Get-Item -LiteralPath $ExecutablePath
    if ($target.PSIsContainer -or $target.Name -ne 'ResourceManager.exe' -or -not $target.VersionInfo.FileVersion) {
        throw 'The target must be a built ResourceManager.exe with a file version.'
    }
    $expectedVersion = ([version]$target.VersionInfo.FileVersion).ToString(3)
    if ([version]$expectedVersion -lt [version]'0.4.5') { throw 'The restart target must support local control (0.4.5 or later).' }
}
$existing = Get-SelectedClient
if ($null -ne $existing) {
    # Status must succeed before requesting shutdown. An older unsupported client is left untouched.
    $before = Invoke-ClientCommand $existing.Id 'status'
    if (-not $before.success) { throw ($before | ConvertTo-Json -Depth 8 -Compress) }
    $ack = Invoke-ClientCommand $existing.Id 'shutdown'
    if (-not $ack.success) { throw ($ack | ConvertTo-Json -Depth 8 -Compress) }
    if (-not $existing.WaitForExit($TimeoutSeconds * 1000)) { throw 'graceful_shutdown_timeout: no force termination was attempted.' }
}
if ($Action -eq 'Shutdown') {
    @{ success = $true; stopped = ($null -ne $existing) } | ConvertTo-Json
    return
}

# --background never activates an existing window and never opens storage-selection dialogs.
# The application itself relaunches through the native Explorer context when AppData is redirected.
Start-Process -FilePath $target.FullName -ArgumentList '--background' -WorkingDirectory $target.DirectoryName -WindowStyle Hidden | Out-Null
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$latest = $null
while ([DateTime]::UtcNow -lt $deadline) {
    foreach ($client in (Get-ClientProcesses)) {
        try {
            $latest = Invoke-ClientCommand $client.Id 'status'
            if ($latest.success -and $latest.data.ready -and $latest.data.executable -eq $target.FullName -and
                $latest.data.version -eq $expectedVersion) {
                $latest | ConvertTo-Json -Depth 8
                return
            }
        } catch { }
    }
    Start-Sleep -Milliseconds 250
}
throw "background_start_not_verified: expected $($target.FullName) version $expectedVersion. No foreground UI was requested."
