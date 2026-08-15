param(
    [Parameter(Mandatory = $true)]
    [string]$ToolName,
    [string]$PayloadJson = '{}',
    [switch]$DirectWorkflow,
    [string]$McpExe = 'C:\Users\ADMIN\Downloads\03. 765T- Flow\765T FLOW\src\BIM765T.Revit.McpHost\bin\Release\net8.0\BIM765T.Revit.McpHost.exe',
    [string]$PipeName = 'BIM765T.Revit.WorkerHost',
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'

function Send-Message([System.Diagnostics.Process]$Process, [object]$Payload) {
    $json = $Payload | ConvertTo-Json -Depth 40 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $header = [Text.Encoding]::ASCII.GetBytes("Content-Length: $($bytes.Length)`r`n`r`n")
    $stream = $Process.StandardInput.BaseStream
    $stream.Write($header, 0, $header.Length)
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
}

function Read-Byte([System.IO.Stream]$Stream, [int]$TimeoutMilliseconds) {
    $buffer = New-Object byte[] 1
    $task = $Stream.ReadAsync($buffer, 0, 1)
    if (-not $task.Wait($TimeoutMilliseconds)) { throw 'Timed out waiting for MCP response.' }
    if ($task.Result -le 0) { return -1 }
    return [int]$buffer[0]
}

function Read-Line([System.IO.Stream]$Stream, [int]$TimeoutMilliseconds) {
    $bytes = New-Object Collections.Generic.List[byte]
    while ($true) {
        $value = Read-Byte $Stream $TimeoutMilliseconds
        if ($value -lt 0) { throw 'Unexpected EOF from MCP host.' }
        if ($value -eq 10) { break }
        if ($value -ne 13) { $bytes.Add([byte]$value) }
    }
    return [Text.Encoding]::ASCII.GetString($bytes.ToArray())
}

function Read-Message([System.Diagnostics.Process]$Process, [int]$TimeoutMilliseconds) {
    $headers = @()
    while ($true) {
        $line = Read-Line $Process.StandardOutput.BaseStream $TimeoutMilliseconds
        if ($line -eq '') { break }
        $headers += $line
    }
    $lengthLine = $headers | Where-Object { $_ -like 'Content-Length:*' } | Select-Object -First 1
    if (-not $lengthLine) { throw 'MCP response has no Content-Length header.' }
    $length = [int](($lengthLine -split ':', 2)[1].Trim())
    $buffer = New-Object byte[] $length
    $offset = 0
    while ($offset -lt $length) {
        $task = $Process.StandardOutput.BaseStream.ReadAsync($buffer, $offset, $length - $offset)
        if (-not $task.Wait($TimeoutMilliseconds)) { throw 'Timed out reading MCP response body.' }
        if ($task.Result -le 0) { throw 'Unexpected EOF in MCP response body.' }
        $offset += $task.Result
    }
    return ([Text.Encoding]::UTF8.GetString($buffer) | ConvertFrom-Json)
}

function Wait-Response([System.Diagnostics.Process]$Process, [int]$Id, [int]$TimeoutMilliseconds) {
    while ($true) {
        $message = Read-Message $Process $TimeoutMilliseconds
        if ($null -ne $message.id -and [int]$message.id -eq $Id) { return $message }
    }
}

if (-not (Test-Path -LiteralPath $McpExe)) { throw "MCP host not found: $McpExe" }
$payload = $PayloadJson | ConvertFrom-Json
$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $McpExe
$psi.Arguments = "--pipe `"$PipeName`""
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = [Text.Encoding]::UTF8
$process = [Diagnostics.Process]::Start($psi)
$timeout = [Math]::Max(1, $TimeoutSeconds) * 1000
try {
    Send-Message $process @{
        jsonrpc = '2.0'; id = 1; method = 'initialize'
        params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'BIM-DatViet-Acceptance'; version = '1.0' } }
    }
    $initialize = Wait-Response $process 1 $timeout
    if ($initialize.error) { throw "MCP initialize failed: $($initialize.error.message)" }
    Send-Message $process @{
        jsonrpc = '2.0'; id = 2; method = 'tools/call'
        params = if ($DirectWorkflow) {
            @{ name = $ToolName; arguments = $payload }
        }
        else {
            @{
                name = 'revit.call_tool'
                arguments = @{ tool_name = $ToolName; payload = $payload; timeout_ms = $timeout }
            }
        }
    }
    $response = Wait-Response $process 2 $timeout
    if ($response.error) { throw "MCP call failed: $($response.error.message)" }
    $response.result.structuredContent | ConvertTo-Json -Depth 60
}
finally {
    try { $process.StandardInput.Close() } catch {}
    if (-not $process.HasExited) { $process.Kill() }
    $process.Dispose()
}
