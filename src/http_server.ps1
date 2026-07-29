# http_server.ps1
$configPath = "C:\ProgramData\AutoSleep\settings.json"
$editorPath = "C:\ProgramData\AutoSleep\editor.html"
$logPath = "C:\ProgramData\AutoSleep\http_server.log"

Remove-Item $logPath -Force -ErrorAction SilentlyContinue
function Write-Log {
    param($msg)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$timestamp] $msg"
    Add-Content -Path $logPath -Value $line
    Write-Host $line
}

Write-Log "Server starting..."

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add('http://localhost:56790/')
try {
    $listener.Start()
    Write-Log "Server started. Listening on http://localhost:56790/"
} catch {
    Write-Log "ERROR starting listener: $($_.Exception.Message)"
    exit 1
}

function Send-Response {
    param($response, $statusCode, $body, $contentType = 'application/json')
    $response.StatusCode = $statusCode
    $response.ContentType = $contentType
    $buffer = [System.Text.Encoding]::UTF8.GetBytes($body)
    $response.ContentLength64 = $buffer.Length
    $response.OutputStream.Write($buffer, 0, $buffer.Length)
    $response.Close()
}

try {
    while ($listener.IsListening) {
        try {
            $context = $listener.GetContext()
        } catch {
            Write-Log "GetContext exception: $($_.Exception.Message)"
            if (-not $listener.IsListening) { break }
            continue
        }

        $request = $context.Request
        $response = $context.Response
        $path = $request.Url.AbsolutePath
        Write-Log "Request: $path"

        if ($path -eq '/favicon.ico') {
            Send-Response -response $response -statusCode 204 -body ''
            continue
        }

        if ($path -eq '/shutdown') {
            Write-Log "Shutdown request received."
            Send-Response -response $response -statusCode 200 -body '{"status":"shutting down"}'
            break
        }

        if ($path -eq '/config.js') {
            Write-Log "  -> Generating config.js"
            try {
                $config = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
                $treeJson = $config.CustomLogicTree | ConvertTo-Json -Compress -Depth 10
                if ($null -eq $config.CustomLogicTree) { $treeJson = "null" }

                $configJsContent = @"
window.__AUTOSLEEP_CONFIG = {
    EnableGpuCheck: $($config.EnableGpuCheck.ToString().ToLower()),
    EnableNetworkCheck: $($config.EnableNetworkCheck.ToString().ToLower()),
    EnableDiskCheck: $($config.EnableDiskCheck.ToString().ToLower()),
    EnableUserActivity: $($config.EnableUserActivity.ToString().ToLower()),
    EnableProcessCheck: $($config.EnableProcessCheck.ToString().ToLower()),
    EnableTimeWindow: $($config.EnableTimeWindow.ToString().ToLower()),
    CustomLogicEnabled: $($config.CustomLogicEnabled.ToString().ToLower()),
    CustomLogicTree: $treeJson,
    CpuThreshold: $($config.CpuThreshold),
    GpuThreshold: $($config.GpuThreshold),
    DiskThresholdKBps: $($config.DiskThresholdKBps),
    NetworkThresholdKBps: $($config.NetworkThresholdKBps),
    TimeWindowStart: $($config.TimeWindowStart),
    TimeWindowEnd: $($config.TimeWindowEnd)
};
"@
                Send-Response -response $response -statusCode 200 -body $configJsContent -contentType 'application/javascript; charset=utf-8'
                Write-Log "  -> config.js served"
            } catch {
                Write-Log "  -> ERROR: $($_.Exception.Message)"
                $fallback = "window.__AUTOSLEEP_CONFIG = { EnableGpuCheck: true, EnableNetworkCheck: true, EnableDiskCheck: true, EnableUserActivity: true, EnableProcessCheck: true, EnableTimeWindow: true, CustomLogicEnabled: false, CustomLogicTree: null, CpuThreshold: 30, GpuThreshold: 30, DiskThresholdKBps: 10240, NetworkThresholdKBps: 1024, TimeWindowStart: 2, TimeWindowEnd: 7 };"
                Send-Response -response $response -statusCode 200 -body $fallback -contentType 'application/javascript; charset=utf-8'
                Write-Log "  -> Fallback config.js served"
            }
            continue
        }

        if ($path -eq '/' -or $path -eq '/editor.html') {
            Write-Log "  -> Serving editor.html"
            if (Test-Path $editorPath) {
                try {
                    $html = Get-Content $editorPath -Raw -Encoding UTF8
                    $html = $html -replace 'src="config.js"', 'src="/config.js"'
                    Send-Response -response $response -statusCode 200 -body $html -contentType 'text/html; charset=utf-8'
                    Write-Log "  -> editor.html served"
                } catch {
                    Write-Log "  -> Error reading editor.html: $($_.Exception.Message)"
                    Send-Response -response $response -statusCode 500 -body '{"status":"error","message":"read error"}'
                }
            } else {
                Send-Response -response $response -statusCode 404 -body '{"status":"error","message":"editor.html not found"}'
                Write-Log "  -> editor.html not found"
            }
            continue
        }

        if ($request.HttpMethod -eq 'POST' -and $path -eq '/save') {
            Write-Log "  -> POST /save received"
            $reader = New-Object System.IO.StreamReader($request.InputStream, [System.Text.Encoding]::UTF8)
            $json = $reader.ReadToEnd()
            $reader.Close()
            Write-Log "  -> JSON length: $($json.Length)"

            try {
                $parsed = $json | ConvertFrom-Json
                Write-Log "  -> JSON parsed successfully"

                $config = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
                Write-Log "  -> Config loaded"

                $config | Add-Member -MemberType NoteProperty -Name "CustomLogicTree" -Value $null -Force
                $config | Add-Member -MemberType NoteProperty -Name "CustomLogicEnabled" -Value $false -Force
                $config.CustomLogicTree = $parsed
                $config.CustomLogicEnabled = $true
                Write-Log "  -> Assigned CustomLogicTree"

                $config | ConvertTo-Json -Depth 100 | Set-Content -Path $configPath -Encoding UTF8
                Write-Log "  -> Config written"

                $verify = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($verify.CustomLogicTree -eq $null) {
                    throw "CustomLogicTree is null after write"
                }
                Write-Log "  -> Verification passed"

                $responseText = '{"status":"success"}'
                Write-Log "  -> Success"
            } catch {
                Write-Log "  -> ERROR: $($_.Exception.Message)"
                $errorObj = @{ status = "error"; message = $_.Exception.Message }
                $responseText = $errorObj | ConvertTo-Json -Compress
                $response.StatusCode = 500
            }

            $buffer = [System.Text.Encoding]::UTF8.GetBytes($responseText)
            $response.ContentType = 'application/json'
            $response.ContentLength64 = $buffer.Length
            $response.OutputStream.Write($buffer, 0, $buffer.Length)
            $response.Close()
            continue
        }

        Write-Log "  -> 404 Not Found"
        Send-Response -response $response -statusCode 404 -body '{"status":"error","message":"Not found"}'
    }
} catch {
    Write-Log "Unhandled exception: $_"
} finally {
    if ($listener -and $listener.IsListening) {
        $listener.Stop()
        $listener.Close()
        Write-Log "Listener stopped and closed."
    }
}
Write-Log "Server stopped"