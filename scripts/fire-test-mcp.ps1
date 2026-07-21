param(
    [string]$BaseUrl = "http://127.0.0.1:5087",
    [string]$RepositoryId = "skynet-steam-emulator",
    [string]$RootPath = "C:\SERVER\SKYNET Steam Emulator",
    [string]$ExpectedSolutionPath = "C:\SERVER\SKYNET Steam Emulator\SKYNET server\SKYNET server.csproj",
    [string]$ReportPath = ""
)

$ErrorActionPreference = "Stop"

$script:Checks = New-Object System.Collections.Generic.List[object]

function New-JsonRpcBody {
    param([string]$Method, [object]$Params = @{}, [int]$Id = 1)
    return @{
        jsonrpc = "2.0"
        id = $Id
        method = $Method
        params = $Params
    } | ConvertTo-Json -Depth 80 -Compress
}

function Invoke-Timed {
    param([scriptblock]$Block)
    $result = $null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $result = & $Block
        $sw.Stop()
        return [pscustomobject]@{ Ok = $true; Result = $result; Error = $null; Ms = [math]::Round($sw.Elapsed.TotalMilliseconds, 1) }
    }
    catch {
        $sw.Stop()
        return [pscustomobject]@{ Ok = $false; Result = $null; Error = $_.Exception.Message; Ms = [math]::Round($sw.Elapsed.TotalMilliseconds, 1) }
    }
}

function Invoke-McpMethod {
    param([string]$Method, [object]$Params = @{}, [int]$Id = 1)
    $body = New-JsonRpcBody -Method $Method -Params $Params -Id $Id
    return Invoke-RestMethod -Method Post -Uri "$BaseUrl/mcp" -ContentType "application/json" -Body $body
}

function Invoke-McpTool {
    param([string]$Name, [object]$Arguments = @{}, [int]$Id = 1)
    $response = Invoke-McpMethod -Method "tools/call" -Params @{ name = $Name; arguments = $Arguments } -Id $Id
    if ($response.result.structuredContent) { return $response.result.structuredContent }
    $content = @($response.result.content)
    if ($content.Count -gt 0) {
        $text = [string]$content[0].text
        try { return $text | ConvertFrom-Json } catch { return $response.result }
    }
    return $response.result
}

function Add-Check {
    param(
        [string]$Name,
        [string]$Category,
        [bool]$Passed,
        [double]$Ms,
        [string]$Details,
        [string]$Severity = "medium",
        [object]$Data = $null
    )
    $script:Checks.Add([pscustomobject]@{
        name = $Name
        category = $Category
        passed = $Passed
        severity = if ($Passed) { "none" } else { $Severity }
        ms = $Ms
        details = $Details
        data = $Data
    }) | Out-Null
}

function Test-Call {
    param(
        [string]$Name,
        [string]$Category,
        [scriptblock]$Block,
        [scriptblock]$Assert,
        [string]$Severity = "medium"
    )
    $timed = Invoke-Timed -Block $Block
    if (-not $timed.Ok) {
        Add-Check -Name $Name -Category $Category -Passed $false -Ms $timed.Ms -Details $timed.Error -Severity $Severity
        return $null
    }
    try {
        $assertion = & $Assert $timed.Result $timed.Ms
        Add-Check -Name $Name -Category $Category -Passed ([bool]$assertion.Passed) -Ms $timed.Ms -Details ([string]$assertion.Details) -Severity $Severity -Data $assertion.Data
    }
    catch {
        Add-Check -Name $Name -Category $Category -Passed $false -Ms $timed.Ms -Details ("Assert failed: " + $_.Exception.Message) -Severity $Severity -Data $timed.Result
    }
    return $timed.Result
}

function Assert-Result {
    param([bool]$Passed, [string]$Details, [object]$Data = $null)
    return [pscustomobject]@{ Passed = $Passed; Details = $Details; Data = $Data }
}

$health = Test-Call -Name "HTTP health" -Category "transport" -Severity "critical" -Block {
    Invoke-RestMethod -Uri "$BaseUrl/health"
} -Assert {
    param($r)
    Assert-Result -Passed ($r.status -eq "ok") -Details "status=$($r.status)"
}

$initialize = Test-Call -Name "MCP initialize" -Category "transport" -Severity "critical" -Block {
    Invoke-McpMethod -Method "initialize" -Params @{
        protocolVersion = "2024-11-05"
        capabilities = @{}
        clientInfo = @{ name = "map-repo-fire-test"; version = "1" }
    }
} -Assert {
    param($r)
    $version = $r.result.protocolVersion
    Assert-Result -Passed ($version -eq "2024-11-05") -Details "protocolVersion=$version"
}

$toolsList = Test-Call -Name "MCP tools/list schema" -Category "contract" -Severity "critical" -Block {
    Invoke-McpMethod -Method "tools/list" -Params @{} -Id 2
} -Assert {
    param($r)
    $required = @("open_repository","list_repositories","repository_status","repo_overview","search_symbols","get_source","file_outline","batch","find_callers","find_callees","get_graph")
    $names = @($r.result.tools | ForEach-Object { $_.name })
    $missing = @($required | Where-Object { $names -notcontains $_ })
    $badSchemas = @($r.result.tools | Where-Object { -not $_.inputSchema -or $_.inputSchema.type -ne "object" -or -not $_.inputSchema.properties })
    Assert-Result -Passed ($missing.Count -eq 0 -and $badSchemas.Count -eq 0) -Details "tools=$($names.Count), missing=$($missing -join ', '), badSchemas=$($badSchemas.Count)" -Data @{ tools = $names }
}

$open = Test-Call -Name "open_repository is non-blocking" -Category "lifecycle" -Severity "high" -Block {
    Invoke-McpTool -Name "open_repository" -Arguments @{
        id = $RepositoryId
        rootPath = $RootPath
        solutionPath = $ExpectedSolutionPath
        reindex = $false
    } -Id 3
} -Assert {
    param($r, $ms)
    Assert-Result -Passed ($r.repositoryId -eq $RepositoryId -and $ms -lt 2000) -Details "repositoryId=$($r.repositoryId), watcherActive=$($r.watcherActive)"
}

$status = Test-Call -Name "repository_status fast and clean" -Category "lifecycle" -Severity "high" -Block {
    Invoke-McpTool -Name "repository_status" -Arguments @{ repositoryId = $RepositoryId } -Id 4
} -Assert {
    param($r)
    $diagCount = @($r.diagnostics).Count
    Assert-Result -Passed ($r.symbols -gt 0 -and $r.relationships -gt 0 -and -not $r.indexing -and $diagCount -eq 0) -Details "symbols=$($r.symbols), relationships=$($r.relationships), indexing=$($r.indexing), diagnostics=$diagCount" -Data $r
}

$listRepos = Test-Call -Name "list_repositories compact discovery" -Category "tokens" -Severity "high" -Block {
    Invoke-McpTool -Name "list_repositories" -Arguments @{} -Id 5
} -Assert {
    param($r, $ms)
    $json = $r | ConvertTo-Json -Depth 80 -Compress
    $bytes = [Text.Encoding]::UTF8.GetByteCount($json)
    Assert-Result -Passed ($bytes -lt 50000 -and $ms -lt 2000) -Details "bytes=$bytes, repos=$(@($r).Count)" -Data @{ bytes = $bytes; repositories = @($r).Count }
}

$overview = Test-Call -Name "repo_overview latency and isolation" -Category "overview" -Severity "critical" -Block {
    Invoke-McpTool -Name "repo_overview" -Arguments @{ repositoryId = $RepositoryId } -Id 6
} -Assert {
    param($r, $ms)
    $json = $r | ConvertTo-Json -Depth 80 -Compress
    $hasExternal = $json -match "\.\./Ts\.NET Runtime" -or $json -match [regex]::Escape("C:\SERVER\Ts.NET Runtime")
    Assert-Result -Passed (-not $hasExternal -and $ms -lt 3000) -Details "ms=$ms, hasExternal=$hasExternal, symbols=$($r.symbols)" -Data @{ hasExternal = $hasExternal; symbols = $r.symbols; relationships = $r.relationships }
}

$externalFiles = Test-Call -Name "no files outside repository root" -Category "isolation" -Severity "critical" -Block {
    Invoke-McpTool -Name "list_files" -Arguments @{ repositoryId = $RepositoryId; contains = "../"; limit = 50 } -Id 7
} -Assert {
    param($r)
    $external = @($r.items | Where-Object { $_.filePath -like "../*" -or $_.filePath -match "^[A-Za-z]:" })
    Assert-Result -Passed ($external.Count -eq 0) -Details "externalFiles=$($external.Count)" -Data $external
}

$searchCreate = Test-Call -Name "search_symbols finds overloaded/same-name methods" -Category "accuracy" -Severity "critical" -Block {
    Invoke-McpTool -Name "search_symbols" -Arguments @{ repositoryId = $RepositoryId; query = "CreateDbContext"; limit = 20 } -Id 8
} -Assert {
    param($r)
    $items = @($r.items)
    $lines = @($items | ForEach-Object { $_.symbol.startLine })
    $hasLine8 = $lines -contains 8
    $hasLine19 = $lines -contains 19
    Assert-Result -Passed ($hasLine8 -and $hasLine19) -Details "count=$($items.Count), lines=$($lines -join ',')" -Data $items
}

$outlineFactory = Test-Call -Name "file_outline includes every declaration" -Category "accuracy" -Severity "critical" -Block {
    Invoke-McpTool -Name "file_outline" -Arguments @{ repositoryId = $RepositoryId; filePath = "SKYNET server/Persistence/AppDbContextFactory.cs" } -Id 9
} -Assert {
    param($r)
    $lines = @($r.symbols | ForEach-Object { $_.startLine })
    Assert-Result -Passed (($lines -contains 8) -and ($lines -contains 19)) -Details "lines=$($lines -join ',')" -Data $r.symbols
}

$searchTs = Test-Call -Name "search_symbols TypeScript generated property" -Category "accuracy" -Severity "high" -Block {
    Invoke-McpTool -Name "search_symbols" -Arguments @{ repositoryId = $RepositoryId; query = "GCToClientTeamsInfo"; limit = 5 } -Id 10
} -Assert {
    param($r)
    $hit = @($r.items)[0]
    Assert-Result -Passed ($hit.symbol.filePath -like "*generated/dota.ts" -and $hit.symbol.startLine -gt 20000) -Details "file=$($hit.symbol.filePath), line=$($hit.symbol.startLine)" -Data $hit
}

$callerTs = Test-Call -Name "find_callers returns callable owner" -Category "graph" -Severity "high" -Block {
    $hit = Invoke-McpTool -Name "search_symbols" -Arguments @{ repositoryId = $RepositoryId; query = "summarizeMatchStateHistory"; limit = 1 } -Id 11
    Invoke-McpTool -Name "find_callers" -Arguments @{ repositoryId = $RepositoryId; symbolId = $hit.items[0].symbol.id; depth = 1; limit = 20 } -Id 12
} -Assert {
    param($r)
    $callerNames = @($r.nodes | Where-Object { $_.id -ne "7862fc5eaac3a4fe8b5e1357" } | ForEach-Object {
        $displayName = if ($_.qualifiedName) { $_.qualifiedName } else { $_.name }
        "$($_.kind):$displayName"
    })
    $hasCallable = @($r.nodes | Where-Object { $_.kind -in @("function","method","Method","constructor") -and $_.name -ne "summarizeMatchStateHistory" }).Count -gt 0
    Assert-Result -Passed $hasCallable -Details "callers=$($callerNames -join '; ')" -Data $r
}

$sourceExact = Test-Call -Name "get_source exact range" -Category "source" -Severity "medium" -Block {
    Invoke-McpTool -Name "get_source" -Arguments @{ repositoryId = $RepositoryId; filePath = "SKYNET server/Persistence/AppDbContextFactory.cs"; startLine = 8; endLine = 14 } -Id 13
} -Assert {
    param($r)
    Assert-Result -Passed (-not $r.truncated -and $r.startLine -eq 8 -and $r.endLine -eq 14 -and $r.content -match "CreateDbContext") -Details "start=$($r.startLine), end=$($r.endLine), truncated=$($r.truncated)"
}

$sourceLong = Test-Call -Name "get_source EOF clamp does not mark truncated" -Category "source" -Severity "medium" -Block {
    Invoke-McpTool -Name "get_source" -Arguments @{ repositoryId = $RepositoryId; filePath = "SKYNET server/Persistence/AppDbContextFactory.cs"; startLine = 1; endLine = 1000 } -Id 14
} -Assert {
    param($r)
    Assert-Result -Passed (-not $r.truncated -and $r.endLine -eq $r.totalLines) -Details "end=$($r.endLine), total=$($r.totalLines), truncated=$($r.truncated)"
}

$invalidRange = Test-Call -Name "get_source rejects invalid range" -Category "source" -Severity "medium" -Block {
    Invoke-McpMethod -Method "tools/call" -Params @{ name = "get_source"; arguments = @{ repositoryId = $RepositoryId; filePath = "SKYNET server/Persistence/AppDbContextFactory.cs"; startLine = 30; endLine = 1 } } -Id 15
} -Assert {
    param($r)
    Assert-Result -Passed ($r.result.isError -eq $true) -Details "isError=$($r.result.isError)"
}

$pathTraversal = Test-Call -Name "get_source blocks path traversal" -Category "security" -Severity "critical" -Block {
    Invoke-McpMethod -Method "tools/call" -Params @{ name = "get_source"; arguments = @{ repositoryId = $RepositoryId; filePath = "..\..\Users\danil\.codex\config.toml"; startLine = 1; endLine = 5 } } -Id 16
} -Assert {
    param($r)
    Assert-Result -Passed ($r.result.isError -eq $true -and $r.result.content[0].text -match "escapes") -Details "isError=$($r.result.isError), message=$($r.result.content[0].text)"
}

$batch = Test-Call -Name "batch preserves per-call truncation flags" -Category "contract" -Severity "medium" -Block {
    Invoke-McpTool -Name "batch" -Arguments @{
        calls = @(
            # CreateDbContext has exactly 2 real matches in this fixture (SteamDbContextFactory +
            # DotaDbContextFactory) — limit=2 requests exactly that many, so truncated must come
            # back false (nothing hidden). truncated is backed by an actual (limit+1)th row check,
            # not a count==limit coincidence, so this also guards against that false-positive class.
            @{ tool = "search_symbols"; arguments = @{ repositoryId = $RepositoryId; query = "CreateDbContext"; limit = 2 } },
            @{ tool = "get_source"; arguments = @{ repositoryId = $RepositoryId; filePath = "SKYNET server/Persistence/AppDbContextFactory.cs"; startLine = 8; endLine = 14 } }
        )
    } -Id 17
} -Assert {
    param($r)
    $results = @($r.results)
    if ($results.Count -eq 0) {
        return Assert-Result -Passed $false -Details "batch returned no results" -Data $r
    }
    $first = $results[0].result
    Assert-Result -Passed ($first.truncated -eq $false) -Details "first.truncated=$($first.truncated)" -Data $r
}

$literal = Test-Call -Name "literal text search has explicit behavior" -Category "tokens" -Severity "medium" -Block {
    $mcp = Invoke-McpTool -Name "search_symbols" -Arguments @{ repositoryId = $RepositoryId; query = "UpdatedAtUtc"; includeTextual = $true; limit = 20 } -Id 18
    $rgCount = 0
    if (Get-Command rg -ErrorAction SilentlyContinue) {
        $rgCount = @(rg --fixed-strings --line-number --column "UpdatedAtUtc" $RootPath 2>$null).Count
    }
    [pscustomobject]@{ mcp = $mcp; rgCount = $rgCount }
} -Assert {
    param($r)
    $mcpCount = @($r.mcp.items).Count
    $hasDiagnostic = ($r.mcp | ConvertTo-Json -Depth 20 -Compress) -match "textual|literal|disabled"
    Assert-Result -Passed (($r.rgCount -eq 0) -or ($mcpCount -gt 0) -or $hasDiagnostic) -Details "mcpCount=$mcpCount, rgCount=$($r.rgCount), hasDiagnostic=$hasDiagnostic" -Data $r
}

$summary = [pscustomobject]@{
    baseUrl = $BaseUrl
    repositoryId = $RepositoryId
    rootPath = $RootPath
    generatedAt = (Get-Date).ToString("O")
    total = $script:Checks.Count
    failed = @($script:Checks | Where-Object { -not $_.passed }).Count
    critical = @($script:Checks | Where-Object { -not $_.passed -and $_.severity -eq "critical" }).Count
    checks = $script:Checks
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path (Get-Location) ("fire-test-report-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json")
}

$summary | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $ReportPath -Encoding UTF8

$script:Checks |
    Sort-Object passed, severity, category, name |
    Format-Table @{Label="Pass";Expression={ if ($_.passed) { "OK" } else { "FAIL" } }}, severity, category, ms, name, details -AutoSize

Write-Host ""
Write-Host "Report: $ReportPath"
Write-Host "Failed: $($summary.failed)/$($summary.total), Critical: $($summary.critical)"

if ($summary.critical -gt 0) { exit 2 }
if ($summary.failed -gt 0) { exit 1 }
exit 0
