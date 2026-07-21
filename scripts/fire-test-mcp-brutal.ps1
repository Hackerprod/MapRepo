param(
    [string]$BaseUrl = "http://127.0.0.1:5087",
    [string]$RepositoryId = "skynet-steam-emulator",
    [string]$RootPath = "C:\SERVER\SKYNET Steam Emulator",
    [string]$SolutionPath = "C:\SERVER\SKYNET Steam Emulator\SKYNET server\SKYNET server.csproj",
    [string]$ReportPath = "D:\Install\Dev\Projects\Skills\XX\map-repo-context\map-repo-server\scripts\fire-test-brutal-report-latest.json"
)

$ErrorActionPreference = "Stop"
$script:Checks = New-Object System.Collections.Generic.List[object]
$script:NextId = 1

function Json-Bytes {
    param($Value)
    $json = $Value | ConvertTo-Json -Depth 100 -Compress
    [Text.Encoding]::UTF8.GetByteCount($json)
}

function Token-Estimate {
    param([int]$Bytes)
    [math]::Ceiling($Bytes / 4)
}

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
    } | ConvertTo-Json -Depth 100 -Compress
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
    Convert-ToolResult (Invoke-Mcp "tools/call" @{ name = $Name; arguments = $Arguments })
}

function Run-Check {
    param([string]$Name, [string]$Category, [string]$Severity, [scriptblock]$Block, [scriptblock]$AssertionBlock)
    $timed = Measure-Block $Block
    if (-not $timed.ok) {
        Add-Check $Name $Category $Severity $false $timed.ms $timed.error
        return $null
    }
    try {
        $assertion = & $AssertionBlock $timed.result $timed.ms
        Add-Check $Name $Category $Severity ([bool]$assertion.passed) $timed.ms ([string]$assertion.details) $assertion.data
    }
    catch {
        Add-Check $Name $Category $Severity $false $timed.ms ("Assert failed: " + $_.Exception.Message) $timed.result
    }
    $timed.result
}

function Run-ToolError {
    param([string]$Name, [string]$Tool, [object]$Arguments, [string]$Pattern, [string]$Severity = "medium")
    Run-Check $Name "errors" $Severity {
        Invoke-Mcp "tools/call" @{ name = $Tool; arguments = $Arguments }
    } {
        param($r)
        $text = ""
        if ($r.result.content) { $text = [string]@($r.result.content)[0].text }
        Assert-Result ($r.result.isError -eq $true -and $text -match $Pattern) "isError=$($r.result.isError), text=$text"
    } | Out-Null
}

function Run-BudgetCheck {
    param([string]$Name, [string]$Tool, [object]$Arguments, [int]$MaxBytes, [int]$MaxMs, [string]$Severity = "medium")
    Run-Check $Name "token-budget" $Severity {
        Invoke-Tool $Tool $Arguments
    } {
        param($r, $ms)
        $bytes = Json-Bytes $r
        $tokens = Token-Estimate $bytes
        Assert-Result ($bytes -le $MaxBytes -and $ms -le $MaxMs) "bytes=$bytes (~$tokens tokens), ms=$ms, maxBytes=$MaxBytes, maxMs=$MaxMs" @{ bytes = $bytes; tokens = $tokens; result = $r }
    } | Out-Null
}

function Run-LatencySeries {
    param([string]$Name, [string]$Tool, [object]$Arguments, [int]$Runs, [int]$MaxAverageMs, [int]$MaxWorstMs)
    Run-Check $Name "latency" "medium" {
        $samples = @()
        for ($i = 0; $i -lt $Runs; $i++) {
            $t = Measure-Block { Invoke-Tool $Tool $Arguments }
            $samples += $t
        }
        $samples
    } {
        param($samples)
        $okSamples = @($samples | Where-Object { $_.ok })
        $avg = [math]::Round((($okSamples | Measure-Object -Property ms -Average).Average), 1)
        $worst = [math]::Round((($okSamples | Measure-Object -Property ms -Maximum).Maximum), 1)
        Assert-Result ($okSamples.Count -eq $Runs -and $avg -le $MaxAverageMs -and $worst -le $MaxWorstMs) "runs=$Runs, avg=$avg, worst=$worst, maxAvg=$MaxAverageMs, maxWorst=$MaxWorstMs" $samples
    } | Out-Null
}

$createSearch = $null
$summarizeSearch = $null
$gcTeamsSearch = $null

Run-Check "health" "transport" "critical" { Invoke-RestMethod "$BaseUrl/health" } {
    param($r) Assert-Result ($r.status -eq "ok") "status=$($r.status)"
} | Out-Null

Run-Check "initialize and tool manifest" "transport" "critical" {
    $init = Invoke-Mcp "initialize" @{ protocolVersion = "2024-11-05"; capabilities = @{}; clientInfo = @{ name = "brutal-fire-test"; version = "1" } }
    $tools = Invoke-Mcp "tools/list" @{}
    [pscustomobject]@{ init = $init; tools = $tools }
} {
    param($r)
    $names = @($r.tools.result.tools | ForEach-Object { $_.name })
    $required = @("open_repository","list_repositories","repository_status","repo_overview","search_symbols","get_symbol","file_outline","list_files","get_source","batch","find_callers","find_callees","find_references","get_graph")
    $missing = @($required | Where-Object { $names -notcontains $_ })
    Assert-Result ($r.init.result.protocolVersion -eq "2024-11-05" -and $missing.Count -eq 0) "tools=$($names.Count), missing=$($missing -join ',')" @{ tools = $names }
} | Out-Null

Run-Check "open repo no reindex" "lifecycle" "high" {
    Invoke-Tool "open_repository" @{ id = $RepositoryId; rootPath = $RootPath; solutionPath = $SolutionPath; reindex = $false }
} {
    param($r, $ms)
    Assert-Result ($r.repositoryId -eq $RepositoryId -and -not $r.indexing -and $ms -lt 2000) "repo=$($r.repositoryId), indexing=$($r.indexing), ms=$ms" $r
} | Out-Null

Run-Check "status clean with stats separated" "lifecycle" "high" {
    Invoke-Tool "repository_status" @{ repositoryId = $RepositoryId }
} {
    param($r)
    $diagnostics = @($r.diagnostics)
    $summary = @($r.indexSummary)
    Assert-Result ($r.symbols -gt 1000 -and $r.relationships -gt 1000 -and $diagnostics.Count -eq 0 -and $summary.Count -gt 0) "symbols=$($r.symbols), edges=$($r.relationships), diagnostics=$($diagnostics.Count), summary=$($summary.Count)" $r
} | Out-Null

Run-BudgetCheck "list_repositories default tiny" "list_repositories" @{} 10000 1000 "high"
Run-BudgetCheck "list_repositories diagnostics bounded" "list_repositories" @{ includeDiagnostics = $true } 120000 3000 "medium"
Run-BudgetCheck "repo_overview budget" "repo_overview" @{ repositoryId = $RepositoryId } 80000 3000 "high"
Run-BudgetCheck "list_files generated bounded" "list_files" @{ repositoryId = $RepositoryId; contains = "Generated"; limit = 50 } 50000 2000 "medium"
Run-BudgetCheck "common query response budget" "search_symbols" @{ repositoryId = $RepositoryId; query = "Msg"; limit = 50 } 120000 2000 "medium"
Run-BudgetCheck "huge file outline bounded" "file_outline" @{ repositoryId = $RepositoryId; filePath = "SKYNET server/GC/570/generated/dota.ts" } 200000 3000 "high"
Run-BudgetCheck "huge source bounded at 400 lines" "get_source" @{ repositoryId = $RepositoryId; filePath = "SKYNET server/GC/570/generated/dota.ts"; startLine = 1; endLine = 25000 } 80000 2000 "high"

Run-Check "workflow C# locate to source token budget" "agent-workflow" "high" {
    $s = Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "CreateDbContext"; limit = 2 }
    $src = Invoke-Tool "get_source" @{ repositoryId = $RepositoryId; filePath = $s.items[0].symbol.filePath; startLine = 6; endLine = 24 }
    [pscustomobject]@{ search = $s; source = $src }
} {
    param($r, $ms)
    $bytes = Json-Bytes $r
    $lines = @($r.search.items | ForEach-Object { $_.symbol.startLine })
    Assert-Result ($lines -contains 8 -and $lines -contains 19 -and $bytes -lt 20000 -and $ms -lt 2000) "bytes=$bytes (~$(Token-Estimate $bytes) tokens), ms=$ms, lines=$($lines -join ',')" $r
} | Out-Null

Run-Check "workflow TS locate caller source graph" "agent-workflow" "high" {
    $s = Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "summarizeMatchStateHistory"; limit = 1 }
    $callers = Invoke-Tool "find_callers" @{ repositoryId = $RepositoryId; symbolId = $s.items[0].symbol.id; depth = 1; limit = 20 }
    $src = Invoke-Tool "get_source" @{ repositoryId = $RepositoryId; filePath = $s.items[0].symbol.filePath; startLine = 145; endLine = 289 }
    [pscustomobject]@{ search = $s; callers = $callers; source = $src }
} {
    param($r, $ms)
    $bytes = Json-Bytes $r
    $callable = @($r.callers.nodes | Where-Object { $_.kind -in @("function","method","Method","constructor") -and $_.name -ne "summarizeMatchStateHistory" })
    Assert-Result ($callable.Count -gt 0 -and $bytes -lt 50000 -and $ms -lt 3000) "bytes=$bytes (~$(Token-Estimate $bytes) tokens), callers=$($callable.Count), ms=$ms" $r
} | Out-Null

Run-Check "workflow batch search source graph" "agent-workflow" "high" {
    $search = Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "summarizeMatchStateHistory"; limit = 1 }
    Invoke-Tool "batch" @{
        calls = @(
            @{ tool = "get_symbol"; arguments = @{ repositoryId = $RepositoryId; symbolId = $search.items[0].symbol.id } },
            @{ tool = "find_callers"; arguments = @{ repositoryId = $RepositoryId; symbolId = $search.items[0].symbol.id; depth = 1; limit = 20 } },
            @{ tool = "get_source"; arguments = @{ repositoryId = $RepositoryId; filePath = $search.items[0].symbol.filePath; startLine = 257; endLine = 289 } }
        )
    }
} {
    param($r, $ms)
    $bytes = Json-Bytes $r
    $ok = @($r.results | Where-Object { $_.ok -eq $true }).Count
    Assert-Result ($ok -eq 3 -and $bytes -lt 70000 -and $ms -lt 3000) "ok=$ok, bytes=$bytes (~$(Token-Estimate $bytes) tokens), ms=$ms" $r
} | Out-Null

Run-Check "workflow literal text says use textual/rg" "agent-workflow" "medium" {
    $mcp = Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "UpdatedAtUtc"; includeTextual = $true; limit = 20 }
    $rg = 0
    if (Get-Command rg -ErrorAction SilentlyContinue) {
        $rg = @(rg --fixed-strings --line-number --column "UpdatedAtUtc" $RootPath 2>$null).Count
    }
    [pscustomobject]@{ mcp = $mcp; rgCount = $rg }
} {
    param($r)
    $mcpCount = @($r.mcp.items).Count
    $json = $r.mcp | ConvertTo-Json -Depth 80 -Compress
    $explicit = $json -match "textual|literal|disabled|not indexed"
    Assert-Result (($r.rgCount -eq 0) -or ($mcpCount -gt 0) -or $explicit) "mcpCount=$mcpCount, rgCount=$($r.rgCount), explicit=$explicit" $r
} | Out-Null

$createSearch = Run-Check "search CreateDbContext exact" "accuracy" "critical" {
    Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "CreateDbContext"; limit = 20 }
} {
    param($r)
    $lines = @($r.items | ForEach-Object { $_.symbol.startLine })
    Assert-Result ($lines -contains 8 -and $lines -contains 19 -and -not $r.truncated) "count=$(@($r.items).Count), lines=$($lines -join ','), truncated=$($r.truncated)" $r
}

$summarizeSearch = Run-Check "search summarizeMatchStateHistory exact" "accuracy" "high" {
    Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "summarizeMatchStateHistory"; limit = 5 }
} {
    param($r)
    $hit = @($r.items)[0]
    Assert-Result ($hit.symbol.filePath -like "*Match.ts" -and $hit.symbol.startLine -eq 257) "file=$($hit.symbol.filePath), line=$($hit.symbol.startLine)" $hit
}

$gcTeamsSearch = Run-Check "search GCToClientTeamsInfo exact" "accuracy" "high" {
    Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "GCToClientTeamsInfo"; limit = 5 }
} {
    param($r)
    $hit = @($r.items)[0]
    Assert-Result ($hit.symbol.filePath -like "*generated/dota.ts" -and $hit.symbol.startLine -gt 20000) "file=$($hit.symbol.filePath), line=$($hit.symbol.startLine)" $hit
}

$names = @(
    @{ q = "SteamDatagramCertificateMessage"; min = 1; ext = ".cs" },
    @{ q = "PersistenceRoundTripCheck"; min = 1; ext = ".cs" },
    @{ q = "NormalizeJson"; min = 1; ext = ".cs" },
    @{ q = "requestGuildData"; min = 1; ext = ".ts" },
    @{ q = "DotaDbContextFactory"; min = 1; ext = ".cs" },
    @{ q = "SteamDbContextFactory"; min = 1; ext = ".cs" }
)
foreach ($item in $names) {
    $q = $item.q
    $ext = $item.ext
    Run-Check "search matrix $q" "accuracy" "medium" {
        Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = $q; limit = 10 }
    } {
        param($r)
        $hits = @($r.items)
        $matchingExt = @($hits | Where-Object { $_.symbol.filePath -like "*$ext" }).Count
        Assert-Result ($hits.Count -ge 1 -and $matchingExt -ge 1) "hits=$($hits.Count), matchingExt=$matchingExt" $hits
    } | Out-Null
}

Run-Check "filters kind method" "filters" "medium" {
    Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "CreateDbContext"; kind = "Method"; limit = 10 }
} {
    param($r)
    $bad = @($r.items | Where-Object { $_.symbol.kind -ne "Method" })
    Assert-Result ($bad.Count -eq 0 -and @($r.items).Count -eq 2) "items=$(@($r.items).Count), bad=$($bad.Count)" $r
} | Out-Null

Run-Check "filters path contains" "filters" "medium" {
    Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "CreateDbContext"; pathContains = "Persistence"; limit = 10 }
} {
    param($r)
    $bad = @($r.items | Where-Object { $_.symbol.filePath -notmatch "Persistence" })
    Assert-Result ($bad.Count -eq 0 -and @($r.items).Count -eq 2) "items=$(@($r.items).Count), bad=$($bad.Count)" $r
} | Out-Null

Run-Check "includeRelationships does not explode" "contract" "medium" {
    Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "CreateDbContext"; includeRelationships = $true; limit = 2 }
} {
    param($r)
    $bytes = Json-Bytes $r
    $hasRel = @($r.items | Where-Object { $null -ne $_.relationships }).Count -gt 0
    Assert-Result ($hasRel -and $bytes -lt 60000) "hasRel=$hasRel, bytes=$bytes" $r
} | Out-Null

Run-Check "get_symbol exact" "contract" "medium" {
    Invoke-Tool "get_symbol" @{ repositoryId = $RepositoryId; symbolId = $createSearch.items[0].symbol.id }
} {
    param($r)
    Assert-Result ($r.symbol.name -eq "CreateDbContext" -or $r.name -eq "CreateDbContext") "name=$($r.symbol.name)$($r.name)" $r
} | Out-Null

Run-Check "file outline full factory" "source" "critical" {
    Invoke-Tool "file_outline" @{ repositoryId = $RepositoryId; filePath = "SKYNET server/Persistence/AppDbContextFactory.cs" }
} {
    param($r)
    $lines = @($r.symbols | ForEach-Object { $_.startLine })
    Assert-Result ($lines -contains 8 -and $lines -contains 19) "lines=$($lines -join ',')" $r
} | Out-Null

Run-Check "source exact line window" "source" "high" {
    Invoke-Tool "get_source" @{ repositoryId = $RepositoryId; filePath = "SKYNET server/Persistence/AppDbContextFactory.cs"; startLine = 8; endLine = 14 }
} {
    param($r)
    Assert-Result ($r.startLine -eq 8 -and $r.endLine -eq 14 -and -not $r.truncated -and $r.content -match "SteamDbContext") "start=$($r.startLine), end=$($r.endLine), truncated=$($r.truncated)" $r
} | Out-Null

Run-Check "source eof clamp sane" "source" "medium" {
    Invoke-Tool "get_source" @{ repositoryId = $RepositoryId; filePath = "SKYNET server/Persistence/AppDbContextFactory.cs"; startLine = 1; endLine = 1000 }
} {
    param($r)
    Assert-Result ($r.endLine -eq $r.totalLines -and -not $r.truncated) "end=$($r.endLine), total=$($r.totalLines), truncated=$($r.truncated)" $r
} | Out-Null

Run-ToolError "reject traversal backslash" "get_source" @{ repositoryId = $RepositoryId; filePath = "..\..\Users\danil\.codex\config.toml"; startLine = 1; endLine = 5 } "escapes|root" "critical"
Run-ToolError "reject traversal slash" "get_source" @{ repositoryId = $RepositoryId; filePath = "../../Users/danil/.codex/config.toml"; startLine = 1; endLine = 5 } "escapes|root" "critical"
Run-ToolError "reject absolute path" "get_source" @{ repositoryId = $RepositoryId; filePath = "C:\Users\danil\.codex\config.toml"; startLine = 1; endLine = 5 } "absolute|escapes|root|relative" "critical"
Run-ToolError "reject invalid range" "get_source" @{ repositoryId = $RepositoryId; filePath = "SKYNET server/Persistence/AppDbContextFactory.cs"; startLine = 30; endLine = 1 } "startLine|range|invalid" "medium"
Run-ToolError "reject unknown repository" "repository_status" @{ repositoryId = "missing-repo-id" } "not open|not found|missing|repository" "medium"
Run-ToolError "reject unknown tool" "no_such_tool" @{} "Unknown tool" "medium"
Run-Check "batch nesting returns per-call failure" "batch" "medium" {
    Invoke-Tool "batch" @{ calls = @(@{ tool = "batch"; arguments = @{ calls = @() } }) }
} {
    param($r)
    $results = @($r.results)
    Assert-Result ($results.Count -eq 1 -and $results[0].ok -eq $false -and $results[0].error -match "batch|invalid") "results=$($results.Count), ok=$($results[0].ok), error=$($results[0].error)" $r
} | Out-Null
Run-ToolError "reject batch over max" "batch" @{ calls = @(1..11 | ForEach-Object { @{ tool = "repository_status"; arguments = @{ repositoryId = $RepositoryId } } }) } "10|max|too many" "medium"

Run-Check "find callers TS owner" "graph" "high" {
    Invoke-Tool "find_callers" @{ repositoryId = $RepositoryId; symbolId = $summarizeSearch.items[0].symbol.id; depth = 1; limit = 20 }
} {
    param($r)
    $callable = @($r.nodes | Where-Object { $_.kind -in @("function","method","Method","constructor") -and $_.name -ne "summarizeMatchStateHistory" })
    Assert-Result ($callable.Count -gt 0 -and @($r.edges).Count -gt 0) "callable=$($callable.Count), edges=$(@($r.edges).Count)" $r
} | Out-Null

Run-Check "find callees includes constructs" "graph" "medium" {
    Invoke-Tool "find_callees" @{ repositoryId = $RepositoryId; symbolId = $createSearch.items[0].symbol.id; depth = 1; limit = 20 }
} {
    param($r)
    $constructs = @($r.edges | Where-Object { $_.kind -eq "constructs" })
    Assert-Result ($constructs.Count -gt 0) "constructs=$($constructs.Count), edges=$(@($r.edges).Count)" $r
} | Out-Null

Run-Check "find references TS property" "graph" "high" {
    Invoke-Tool "find_references" @{ repositoryId = $RepositoryId; symbolId = $gcTeamsSearch.items[0].symbol.id; depth = 1; limit = 40 }
} {
    param($r)
    $refs = @($r.edges | Where-Object { $_.kind -eq "references" })
    Assert-Result ($refs.Count -gt 0) "references=$($refs.Count), edges=$(@($r.edges).Count)" $r
} | Out-Null

Run-Check "graph calls filter strict" "graph" "medium" {
    Invoke-Tool "get_graph" @{ repositoryId = $RepositoryId; symbolId = $summarizeSearch.items[0].symbol.id; depth = 1; limit = 50; edgeKinds = @("calls") }
} {
    param($r)
    $bad = @($r.edges | Where-Object { $_.kind -ne "calls" })
    Assert-Result ($bad.Count -eq 0 -and @($r.edges).Count -gt 0) "edges=$(@($r.edges).Count), bad=$($bad.Count)" $r
} | Out-Null

Run-Check "graph limit respected" "graph" "medium" {
    Invoke-Tool "get_graph" @{ repositoryId = $RepositoryId; symbolId = $summarizeSearch.items[0].symbol.id; depth = 5; limit = 5 }
} {
    param($r)
    Assert-Result (@($r.nodes).Count -le 5 -and $r.truncated -eq $true) "nodes=$(@($r.nodes).Count), truncated=$($r.truncated)" $r
} | Out-Null

Run-Check "batch ten calls bounded" "batch" "medium" {
    Invoke-Tool "batch" @{ calls = @(1..10 | ForEach-Object { @{ tool = "repository_status"; arguments = @{ repositoryId = $RepositoryId } } }) }
} {
    param($r)
    $ok = @($r.results | Where-Object { $_.ok -eq $true }).Count
    $bytes = Json-Bytes $r
    Assert-Result ($ok -eq 10 -and $bytes -lt 120000) "ok=$ok, bytes=$bytes" $r
} | Out-Null

Run-Check "batch partial failure preserves order" "batch" "medium" {
    Invoke-Tool "batch" @{ calls = @(
        @{ tool = "repository_status"; arguments = @{ repositoryId = $RepositoryId } },
        @{ tool = "no_such_tool"; arguments = @{} },
        @{ tool = "search_symbols"; arguments = @{ repositoryId = $RepositoryId; query = "CreateDbContext"; limit = 2 } }
    ) }
} {
    param($r)
    $results = @($r.results)
    Assert-Result ($results.Count -eq 3 -and $results[0].ok -eq $true -and $results[1].ok -eq $false -and $results[2].ok -eq $true) "okSeq=$($results[0].ok),$($results[1].ok),$($results[2].ok)" $r
} | Out-Null

Run-LatencySeries "latency exact search x20" "search_symbols" @{ repositoryId = $RepositoryId; query = "CreateDbContext"; limit = 2 } 20 150 500
Run-LatencySeries "latency status x20" "repository_status" @{ repositoryId = $RepositoryId } 20 100 300
Run-LatencySeries "latency negative search x10" "search_symbols" @{ repositoryId = $RepositoryId; query = "NoSuchSymbol_123456789"; limit = 10 } 10 500 1000

Run-Check "SSE handshake headers" "transport" "medium" {
    (& cmd.exe /c "curl.exe -s --max-time 2 -N -D - $BaseUrl/mcp 2>NUL & exit /b 0") -join "`n"
} {
    param($r)
    Assert-Result ($r -match "text/event-stream" -and $r -match "Mcp-Session-Id" -and $r -match "MCP stream ready") "sse=$($r -match 'text/event-stream'), session=$($r -match 'Mcp-Session-Id')" $r
} | Out-Null

Run-Check "parse error jsonrpc" "transport" "medium" {
    try { Invoke-RestMethod -Method Post -Uri "$BaseUrl/mcp" -ContentType "application/json" -Body "{bad json" }
    catch {
        if ($_.ErrorDetails.Message) { return ($_.ErrorDetails.Message | ConvertFrom-Json) }
        throw
    }
} {
    param($r)
    Assert-Result ($r.error.code -eq -32700) "code=$($r.error.code)" $r
} | Out-Null

Run-Check "unknown method jsonrpc" "transport" "medium" {
    Invoke-Mcp "not/a/method" @{}
} {
    param($r)
    Assert-Result ($r.error.code -eq -32601) "code=$($r.error.code)" $r
} | Out-Null

Run-Check "legacy message missing session rejected" "transport" "medium" {
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
