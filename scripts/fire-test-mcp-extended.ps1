param(
    [string]$BaseUrl = "http://127.0.0.1:5087",
    [string]$RepositoryId = "skynet-steam-emulator",
    [string]$RootPath = "C:\SERVER\SKYNET Steam Emulator",
    [string]$SolutionPath = "C:\SERVER\SKYNET Steam Emulator\SKYNET server\SKYNET server.csproj",
    [string]$ReportPath = "D:\Install\Dev\Projects\Skills\XX\map-repo-context\map-repo-server\scripts\fire-test-extended-report-latest.json"
)

$ErrorActionPreference = "Stop"
$script:Checks = New-Object System.Collections.Generic.List[object]
$script:NextId = 1

function Measure-Block {
    param([scriptblock]$Block)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $result = & $Block
        $sw.Stop()
        [pscustomobject]@{ ok = $true; result = $result; error = $null; ms = [math]::Round($sw.Elapsed.TotalMilliseconds, 1) }
    }
    catch {
        $sw.Stop()
        [pscustomobject]@{ ok = $false; result = $null; error = $_.Exception.Message; ms = [math]::Round($sw.Elapsed.TotalMilliseconds, 1) }
    }
}

function Add-Check {
    param([string]$Name, [string]$Category, [string]$Severity, [bool]$Passed, [double]$Ms, [string]$Details, [object]$Data = $null)
    $script:Checks.Add([pscustomobject]@{
        name = $Name
        category = $Category
        severity = if ($Passed) { "none" } else { $Severity }
        passed = $Passed
        ms = $Ms
        details = $Details
        data = $Data
    }) | Out-Null
}

function Assert-Result {
    param([bool]$Passed, [string]$Details, [object]$Data = $null)
    [pscustomobject]@{ passed = $Passed; details = $Details; data = $Data }
}

function Invoke-Mcp {
    param([string]$Method, [object]$Params = @{})
    $id = $script:NextId
    $script:NextId++
    $body = @{
        jsonrpc = "2.0"
        id = $id
        method = $Method
        params = $Params
    } | ConvertTo-Json -Depth 80 -Compress
    Invoke-RestMethod -Method Post -Uri "$BaseUrl/mcp" -ContentType "application/json" -Body $body
}

function Convert-ToolResult {
    param($Response)
    if ($Response.result.structuredContent) { return $Response.result.structuredContent }
    $content = @($Response.result.content)
    if ($content.Count -eq 0) { return $Response.result }
    $text = [string]$content[0].text
    try { return $text | ConvertFrom-Json } catch { return $Response.result }
}

function Invoke-Tool {
    param([string]$Name, [object]$Arguments = @{})
    Convert-ToolResult (Invoke-Mcp -Method "tools/call" -Params @{ name = $Name; arguments = $Arguments })
}

function Run-Check {
    param([string]$Name, [string]$Category, [string]$Severity, [scriptblock]$Block, [scriptblock]$AssertionBlock)
    $timed = Measure-Block $Block
    if (-not $timed.ok) {
        Add-Check -Name $Name -Category $Category -Severity $Severity -Passed $false -Ms $timed.ms -Details $timed.error
        return $null
    }
    try {
        $assertion = & $AssertionBlock $timed.result $timed.ms
        Add-Check -Name $Name -Category $Category -Severity $Severity -Passed ([bool]$assertion.passed) -Ms $timed.ms -Details ([string]$assertion.details) -Data $assertion.data
    }
    catch {
        Add-Check -Name $Name -Category $Category -Severity $Severity -Passed $false -Ms $timed.ms -Details ("Assert failed: " + $_.Exception.Message) -Data $timed.result
    }
    $timed.result
}

function Tool-ErrorCheck {
    param([string]$Name, [object]$Arguments, [string]$Expected)
    Run-Check -Name $Name -Category "errors" -Severity "medium" -Block {
        Invoke-Mcp -Method "tools/call" -Params @{ name = "get_source"; arguments = $Arguments }
    } -Assert {
        param($r)
        $text = ""
        if ($r.result.content) { $text = [string]@($r.result.content)[0].text }
        Assert-Result -Passed ($r.result.isError -eq $true -and $text -match $Expected) -Details "isError=$($r.result.isError), text=$text"
    } | Out-Null
}

$known = @{
    createDbContext = "1fc23afac5121e1bf6166c15"
    summarize = "7862fc5eaac3a4fe8b5e1357"
}

Run-Check "health endpoint" "transport" "critical" { Invoke-RestMethod "$BaseUrl/health" } {
    param($r) Assert-Result ($r.status -eq "ok") "status=$($r.status)"
} | Out-Null

Run-Check "info endpoint" "transport" "medium" { Invoke-RestMethod "$BaseUrl/info" } {
    param($r) Assert-Result ($r.service -eq "map-repo-server") "service=$($r.service)"
} | Out-Null

Run-Check "initialize protocol" "transport" "critical" {
    Invoke-Mcp "initialize" @{ protocolVersion = "2024-11-05"; capabilities = @{}; clientInfo = @{ name = "extended-fire-test"; version = "1" } }
} {
    param($r) Assert-Result ($r.result.protocolVersion -eq "2024-11-05") "protocol=$($r.result.protocolVersion)"
} | Out-Null

Run-Check "tools/list shape and schemas" "contract" "critical" { Invoke-Mcp "tools/list" @{} } {
    param($r)
    $tools = @($r.result.tools)
    $names = @($tools | ForEach-Object { $_.name })
    $bad = @($tools | Where-Object { $_.inputSchema.type -ne "object" -or $null -eq $_.inputSchema.required -or $null -eq $_.inputSchema.additionalProperties })
    $needed = @("open_repository","list_repositories","repository_status","repo_overview","search_symbols","get_symbol","file_outline","list_files","get_source","batch","find_callers","find_callees","find_references","get_graph")
    $missing = @($needed | Where-Object { $names -notcontains $_ })
    Assert-Result ($bad.Count -eq 0 -and $missing.Count -eq 0) "tools=$($tools.Count), bad=$($bad.Count), missing=$($missing -join ',')" @{ names = $names }
} | Out-Null

Run-Check "open_repository reuses index fast" "lifecycle" "high" {
    Invoke-Tool "open_repository" @{ id = $RepositoryId; rootPath = $RootPath; solutionPath = $SolutionPath; reindex = $false }
} {
    param($r, $ms) Assert-Result ($r.repositoryId -eq $RepositoryId -and $ms -lt 2000) "repo=$($r.repositoryId), ms=$ms, watcher=$($r.watcherActive)" $r
} | Out-Null

Run-Check "repository_status compact and clean" "lifecycle" "high" { Invoke-Tool "repository_status" @{ repositoryId = $RepositoryId } } {
    param($r)
    $diagCount = @($r.diagnostics).Count
    Assert-Result ($r.symbols -gt 1000 -and $r.relationships -gt 1000 -and -not $r.indexing -and $diagCount -eq 0) "symbols=$($r.symbols), edges=$($r.relationships), diagnostics=$diagCount" $r
} | Out-Null

Run-Check "list_repositories budget" "tokens" "high" { Invoke-Tool "list_repositories" @{} } {
    param($r, $ms)
    $json = $r | ConvertTo-Json -Depth 80 -Compress
    $bytes = [Text.Encoding]::UTF8.GetByteCount($json)
    Assert-Result ($ms -lt 2000 -and $bytes -lt 20000) "repos=$(@($r).Count), bytes=$bytes, ms=$ms"
} | Out-Null

Run-Check "repo_overview budget and isolation" "overview" "critical" { Invoke-Tool "repo_overview" @{ repositoryId = $RepositoryId } } {
    param($r, $ms)
    $json = $r | ConvertTo-Json -Depth 100 -Compress
    $external = $json -match "\.\./" -or $json -match [regex]::Escape("C:\SERVER\Ts.NET Runtime")
    Assert-Result ($ms -lt 3000 -and -not $external -and $r.symbols -gt 1000) "symbols=$($r.symbols), ms=$ms, external=$external" $r
} | Out-Null

Run-Check "no parent-relative files indexed" "isolation" "critical" { Invoke-Tool "list_files" @{ repositoryId = $RepositoryId; contains = "../"; limit = 100 } } {
    param($r)
    $items = @($r.items)
    Assert-Result ($items.Count -eq 0) "parentRelativeFiles=$($items.Count)" $items
} | Out-Null

Run-Check "no absolute-path files indexed" "isolation" "critical" { Invoke-Tool "list_files" @{ repositoryId = $RepositoryId; contains = "C:"; limit = 100 } } {
    param($r)
    $items = @($r.items)
    Assert-Result ($items.Count -eq 0) "absoluteFiles=$($items.Count)" $items
} | Out-Null

Run-Check "search C# same-name methods" "accuracy" "critical" { Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "CreateDbContext"; limit = 20 } } {
    param($r)
    $items = @($r.items)
    $lines = @($items | ForEach-Object { $_.symbol.startLine })
    Assert-Result ($lines -contains 8 -and $lines -contains 19) "count=$($items.Count), lines=$($lines -join ',')" $items
} | Out-Null

Run-Check "search C# class" "accuracy" "high" { Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "SteamDatagramCertificateMessage"; limit = 5 } } {
    param($r)
    $hit = @($r.items)[0]
    Assert-Result ($hit.symbol.filePath -like "*.cs" -and $hit.symbol.startLine -gt 0) "file=$($hit.symbol.filePath), line=$($hit.symbol.startLine)" $hit
} | Out-Null

Run-Check "search C# method PersistenceRoundTripCheck" "accuracy" "high" { Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "PersistenceRoundTripCheck"; limit = 10 } } {
    param($r)
    $items = @($r.items)
    Assert-Result ($items.Count -gt 0 -and (@($items | Where-Object { $_.symbol.filePath -like "*.cs" }).Count -gt 0)) "count=$($items.Count)" $items
} | Out-Null

Run-Check "search C# NormalizeJson" "accuracy" "high" { Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "NormalizeJson"; limit = 10 } } {
    param($r)
    $items = @($r.items)
    Assert-Result ($items.Count -gt 0) "count=$($items.Count)" $items
} | Out-Null

Run-Check "search TS requestGuildData" "accuracy" "high" { Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "requestGuildData"; limit = 10 } } {
    param($r)
    $hit = @($r.items)[0]
    Assert-Result ($hit.symbol.filePath -like "*.ts" -and $hit.symbol.startLine -gt 0) "file=$($hit.symbol.filePath), line=$($hit.symbol.startLine)" $hit
} | Out-Null

Run-Check "search TS generated Msg property" "accuracy" "high" { Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "GCToClientTeamsInfo"; limit = 5 } } {
    param($r)
    $hit = @($r.items)[0]
    Assert-Result ($hit.symbol.filePath -like "*generated/dota.ts" -and $hit.symbol.startLine -gt 20000) "file=$($hit.symbol.filePath), line=$($hit.symbol.startLine)" $hit
} | Out-Null

Run-Check "search missing symbol fast" "performance" "medium" { Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "NoSuchSymbol_123456789"; limit = 10 } } {
    param($r, $ms)
    Assert-Result ($ms -lt 1000 -and @($r.items).Count -eq 0) "count=$(@($r.items).Count), ms=$ms"
} | Out-Null

Run-Check "search kind filter" "filters" "medium" { Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "CreateDbContext"; kind = "Method"; limit = 10 } } {
    param($r)
    $bad = @($r.items | Where-Object { $_.symbol.kind -ne "Method" })
    Assert-Result ($bad.Count -eq 0 -and @($r.items).Count -gt 0) "count=$(@($r.items).Count), bad=$($bad.Count)"
} | Out-Null

Run-Check "search pathContains filter" "filters" "medium" { Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "CreateDbContext"; pathContains = "Persistence"; limit = 10 } } {
    param($r)
    $bad = @($r.items | Where-Object { $_.symbol.filePath -notmatch "Persistence" })
    Assert-Result ($bad.Count -eq 0 -and @($r.items).Count -gt 0) "count=$(@($r.items).Count), bad=$($bad.Count)"
} | Out-Null

Run-Check "includeRelationships attaches edges" "contract" "medium" { Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "CreateDbContext"; includeRelationships = $true; limit = 1 } } {
    param($r)
    $first = @($r.items)[0]
    Assert-Result ($null -ne $first.relationships) "hasRelationships=$($null -ne $first.relationships)" $first
} | Out-Null

Run-Check "get_symbol advertised tool works" "contract" "high" {
    Invoke-Tool "get_symbol" @{ repositoryId = $RepositoryId; symbolId = $known.createDbContext }
} {
    param($r)
    Assert-Result ($r.symbol.name -eq "CreateDbContext" -or $r.name -eq "CreateDbContext") "name=$($r.symbol.name)$($r.name)" $r
} | Out-Null

Run-Check "file_outline factory completeness" "source" "critical" { Invoke-Tool "file_outline" @{ repositoryId = $RepositoryId; filePath = "SKYNET server/Persistence/AppDbContextFactory.cs" } } {
    param($r)
    $lines = @($r.symbols | ForEach-Object { $_.startLine })
    Assert-Result ($lines -contains 8 -and $lines -contains 19) "lines=$($lines -join ',')" $r.symbols
} | Out-Null

Run-Check "get_source exact range" "source" "high" { Invoke-Tool "get_source" @{ repositoryId = $RepositoryId; filePath = "SKYNET server/Persistence/AppDbContextFactory.cs"; startLine = 8; endLine = 14 } } {
    param($r)
    Assert-Result (-not $r.truncated -and $r.startLine -eq 8 -and $r.endLine -eq 14 -and $r.content -match "CreateDbContext") "start=$($r.startLine), end=$($r.endLine), truncated=$($r.truncated)" $r
} | Out-Null

Run-Check "get_source max line cap" "source" "medium" { Invoke-Tool "get_source" @{ repositoryId = $RepositoryId; filePath = "SKYNET server/GC/570/generated/dota.ts"; startLine = 1; endLine = 10000 } } {
    param($r)
    $lineCount = (@([string]$r.content -split "`n")).Count
    Assert-Result ($r.truncated -eq $true -and $lineCount -le 401) "lineCount=$lineCount, truncated=$($r.truncated)" $r
} | Out-Null

Tool-ErrorCheck "get_source rejects traversal" @{ repositoryId = $RepositoryId; filePath = "..\..\Users\danil\.codex\config.toml"; startLine = 1; endLine = 5 } "escapes"
Tool-ErrorCheck "get_source rejects invalid range" @{ repositoryId = $RepositoryId; filePath = "SKYNET server/Persistence/AppDbContextFactory.cs"; startLine = 30; endLine = 1 } "range|line|invalid|start"
Tool-ErrorCheck "get_source rejects unknown file" @{ repositoryId = $RepositoryId; filePath = "does/not/exist.cs"; startLine = 1; endLine = 5 } "not found|exist"

Run-Check "find_callers TS callable owner" "graph" "high" {
    $hit = Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "summarizeMatchStateHistory"; limit = 1 }
    Invoke-Tool "find_callers" @{ repositoryId = $RepositoryId; symbolId = $hit.items[0].symbol.id; depth = 1; limit = 20 }
} {
    param($r)
    $callable = @($r.nodes | Where-Object { $_.kind -in @("function","method","Method","constructor") -and $_.name -ne "summarizeMatchStateHistory" })
    Assert-Result ($callable.Count -gt 0) "callableCallers=$($callable.Count)" $r
} | Out-Null

Run-Check "find_callees C# includes construct dependency" "graph" "medium" {
    Invoke-Tool "find_callees" @{ repositoryId = $RepositoryId; symbolId = $known.createDbContext; depth = 1; limit = 20 }
} {
    param($r)
    $hasConstruct = @($r.edges | Where-Object { $_.kind -eq "constructs" }).Count -gt 0
    Assert-Result $hasConstruct "constructEdges=$(@($r.edges | Where-Object { $_.kind -eq 'constructs' }).Count)" $r
} | Out-Null

Run-Check "get_graph edgeKinds calls only" "graph" "medium" {
    $hit = Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "summarizeMatchStateHistory"; limit = 1 }
    Invoke-Tool "get_graph" @{ repositoryId = $RepositoryId; symbolId = $hit.items[0].symbol.id; depth = 1; limit = 50; edgeKinds = @("calls") }
} {
    param($r)
    $bad = @($r.edges | Where-Object { $_.kind -ne "calls" })
    Assert-Result ($bad.Count -eq 0 -and @($r.edges).Count -gt 0) "edges=$(@($r.edges).Count), bad=$($bad.Count)" $r
} | Out-Null

Run-Check "find_references returns reference edge" "graph" "medium" {
    $hit = Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "GCToClientTeamsInfo"; limit = 1 }
    Invoke-Tool "find_references" @{ repositoryId = $RepositoryId; symbolId = $hit.items[0].symbol.id; depth = 1; limit = 20 }
} {
    param($r)
    $refs = @($r.edges | Where-Object { $_.kind -eq "references" })
    Assert-Result ($refs.Count -gt 0) "references=$($refs.Count)" $r
} | Out-Null

Run-Check "batch success and partial failure" "batch" "medium" {
    Invoke-Tool "batch" @{
        calls = @(
            @{ tool = "search_symbols"; arguments = @{ repositoryId = $RepositoryId; query = "CreateDbContext"; limit = 2 } },
            @{ tool = "no_such_tool"; arguments = @{} },
            @{ tool = "get_source"; arguments = @{ repositoryId = $RepositoryId; filePath = "SKYNET server/Persistence/AppDbContextFactory.cs"; startLine = 8; endLine = 14 } }
        )
    }
} {
    param($r)
    $results = @($r.results)
    $okCount = @($results | Where-Object { $_.ok -eq $true }).Count
    $failCount = @($results | Where-Object { $_.ok -eq $false }).Count
    $firstTruncated = $results[0].result.truncated
    Assert-Result ($results.Count -eq 3 -and $okCount -eq 2 -and $failCount -eq 1 -and $firstTruncated -eq $false) "results=$($results.Count), ok=$okCount, failed=$failCount, firstTruncated=$firstTruncated" $r
} | Out-Null

Run-Check "literal text behavior vs rg" "tokens" "medium" {
    $mcp = Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "UpdatedAtUtc"; includeTextual = $true; limit = 20 }
    $rg = 0
    if (Get-Command rg -ErrorAction SilentlyContinue) {
        $rg = @(rg --fixed-strings --line-number --column "UpdatedAtUtc" $RootPath 2>$null).Count
    }
    [pscustomobject]@{ mcp = $mcp; rgCount = $rg }
} {
    param($r)
    $mcpCount = @($r.mcp.items).Count
    $json = $r.mcp | ConvertTo-Json -Depth 50 -Compress
    $diagnostic = $json -match "textual|literal|disabled"
    Assert-Result (($r.rgCount -eq 0) -or ($mcpCount -gt 0) -or $diagnostic) "mcpCount=$mcpCount, rgCount=$($r.rgCount), diagnostic=$diagnostic" $r
} | Out-Null

Run-Check "malformed JSON returns JSON-RPC parse error" "transport" "medium" {
    try {
        Invoke-RestMethod -Method Post -Uri "$BaseUrl/mcp" -ContentType "application/json" -Body "{bad json"
    }
    catch {
        if ($_.ErrorDetails.Message) { return ($_.ErrorDetails.Message | ConvertFrom-Json) }
        throw
    }
} {
    param($r)
    Assert-Result ($r.error.code -eq -32700) "code=$($r.error.code), message=$($r.error.message)"
} | Out-Null

Run-Check "unknown method returns JSON-RPC error" "transport" "medium" {
    Invoke-Mcp "no/such/method" @{}
} {
    param($r)
    Assert-Result ($r.error.code -eq -32601) "code=$($r.error.code), message=$($r.error.message)"
} | Out-Null

Run-Check "GET /mcp SSE handshake" "transport" "medium" {
    $output = & cmd.exe /c "curl.exe -s --max-time 2 -N -D - $BaseUrl/mcp 2>NUL & exit /b 0"
    $output -join "`n"
} {
    param($r)
    Assert-Result ($r -match "text/event-stream" -and $r -match "Mcp-Session-Id" -and $r -match "MCP stream ready") "sse=$($r -match 'text/event-stream'), session=$($r -match 'Mcp-Session-Id')"
} | Out-Null

Run-Check "POST /message rejects missing session" "transport" "medium" {
    try {
        Invoke-RestMethod -Method Post -Uri "$BaseUrl/message?sessionId=missing" -ContentType "application/json" -Body (@{ jsonrpc = "2.0"; id = 1; method = "ping" } | ConvertTo-Json -Compress)
    }
    catch {
        return $_.Exception.Response.StatusCode.value__
    }
} {
    param($r)
    Assert-Result ($r -eq 400) "status=$r"
} | Out-Null

Run-Check "response size budget for search" "tokens" "medium" {
    $r = Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "Msg"; limit = 50 }
    $json = $r | ConvertTo-Json -Depth 50 -Compress
    [pscustomobject]@{ bytes = [Text.Encoding]::UTF8.GetByteCount($json); result = $r }
} {
    param($r)
    Assert-Result ($r.bytes -lt 120000) "bytes=$($r.bytes)" $r.result
} | Out-Null

$summary = [pscustomobject]@{
    baseUrl = $BaseUrl
    repositoryId = $RepositoryId
    generatedAt = (Get-Date).ToString("O")
    total = $script:Checks.Count
    failed = @($script:Checks | Where-Object { -not $_.passed }).Count
    critical = @($script:Checks | Where-Object { -not $_.passed -and $_.severity -eq "critical" }).Count
    high = @($script:Checks | Where-Object { -not $_.passed -and $_.severity -eq "high" }).Count
    checks = $script:Checks
}

$summary | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
$script:Checks |
    Sort-Object passed, severity, category, name |
    Format-Table @{Label="Pass";Expression={ if ($_.passed) { "OK" } else { "FAIL" } }}, severity, category, ms, name, details -AutoSize

Write-Host ""
Write-Host "Report: $ReportPath"
Write-Host "Failed: $($summary.failed)/$($summary.total), Critical: $($summary.critical), High: $($summary.high)"

if ($summary.critical -gt 0) { exit 2 }
if ($summary.failed -gt 0) { exit 1 }
exit 0
