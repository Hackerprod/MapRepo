param(
    [string]$BaseUrl = "http://127.0.0.1:5087",
    [string]$RepositoryId = "skynet-steam-emulator",
    [string]$RootPath = "C:\SERVER\SKYNET Steam Emulator",
    [string]$ReportPath = "D:\Install\Dev\Projects\Skills\XX\map-repo-context\map-repo-server\scripts\fire-test-agent-pressure-report-latest.json"
)

$ErrorActionPreference = "Stop"
$script:Checks = New-Object System.Collections.Generic.List[object]
$script:NextId = 1

function Json-Bytes($Value) {
    $json = $Value | ConvertTo-Json -Depth 100 -Compress
    [Text.Encoding]::UTF8.GetByteCount($json)
}

function Token-Estimate([int]$Bytes) { [math]::Ceiling($Bytes / 4) }

function Measure-Block([scriptblock]$Block) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $result = & $Block
        $sw.Stop()
        [pscustomobject]@{ ok = $true; result = $result; error = $null; ms = [math]::Round($sw.Elapsed.TotalMilliseconds, 1) }
    } catch {
        $sw.Stop()
        [pscustomobject]@{ ok = $false; result = $null; error = $_.Exception.Message; ms = [math]::Round($sw.Elapsed.TotalMilliseconds, 1) }
    }
}

function Add-Check([string]$Name, [string]$Category, [string]$Severity, [bool]$Passed, [double]$Ms, [string]$Details, [object]$Data = $null) {
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

function Assert-Result([bool]$Passed, [string]$Details, [object]$Data = $null) {
    [pscustomobject]@{ passed = $Passed; details = $Details; data = $Data }
}

function Invoke-Mcp([string]$Method, [object]$Params = @{}) {
    $id = $script:NextId
    $script:NextId++
    $body = @{ jsonrpc = "2.0"; id = $id; method = $Method; params = $Params } | ConvertTo-Json -Depth 100 -Compress
    Invoke-RestMethod -Method Post -Uri "$BaseUrl/mcp" -ContentType "application/json" -Body $body
}

function Convert-ToolResult($Response) {
    if ($Response.result.structuredContent) { return $Response.result.structuredContent }
    $content = @($Response.result.content)
    if ($content.Count -eq 0) { return $Response.result }
    $text = [string]$content[0].text
    try { return $text | ConvertFrom-Json } catch { return $Response.result }
}

function Invoke-Tool([string]$Name, [object]$Arguments = @{}) {
    Convert-ToolResult (Invoke-Mcp "tools/call" @{ name = $Name; arguments = $Arguments })
}

function Run-Check([string]$Name, [string]$Category, [string]$Severity, [scriptblock]$Block, [scriptblock]$Assertion) {
    $timed = Measure-Block $Block
    if (-not $timed.ok) {
        Add-Check $Name $Category $Severity $false $timed.ms $timed.error
        return $null
    }
    try {
        $a = & $Assertion $timed.result $timed.ms
        Add-Check $Name $Category $Severity ([bool]$a.passed) $timed.ms ([string]$a.details) $a.data
    } catch {
        Add-Check $Name $Category $Severity $false $timed.ms ("Assert failed: " + $_.Exception.Message) $timed.result
    }
    $timed.result
}

function Run-Flow([string]$Name, [scriptblock]$Flow, [int]$MaxBytes, [int]$MaxMs, [string]$Severity = "high") {
    Run-Check $Name "agent-flow" $Severity $Flow {
        param($r, $ms)
        $bytes = Json-Bytes $r
        $tokens = Token-Estimate $bytes
        Assert-Result ($bytes -le $MaxBytes -and $ms -le $MaxMs) "bytes=$bytes (~$tokens tokens), ms=$ms, maxBytes=$MaxBytes, maxMs=$MaxMs" $r
    } | Out-Null
}

function Run-QuerySeries([string]$Name, [array]$Queries, [int]$MaxAvgMs, [int]$MaxWorstMs, [int]$MaxTotalBytes) {
    Run-Check $Name "series" "medium" {
        $items = @()
        foreach ($q in $Queries) {
            $m = Measure-Block { Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = $q; limit = 10 } }
            $bytes = if ($m.ok) { Json-Bytes $m.result } else { 0 }
            $items += [pscustomobject]@{ query = $q; ok = $m.ok; ms = $m.ms; bytes = $bytes; count = if ($m.ok) { @($m.result.items).Count } else { 0 }; error = $m.error }
        }
        $items
    } {
        param($items)
        $avg = [math]::Round((($items | Measure-Object -Property ms -Average).Average), 1)
        $worst = [math]::Round((($items | Measure-Object -Property ms -Maximum).Maximum), 1)
        $bytes = (($items | Measure-Object -Property bytes -Sum).Sum)
        $allOk = @($items | Where-Object { -not $_.ok }).Count -eq 0
        Assert-Result ($allOk -and $avg -le $MaxAvgMs -and $worst -le $MaxWorstMs -and $bytes -le $MaxTotalBytes) "queries=$($items.Count), avg=$avg, worst=$worst, bytes=$bytes" $items
    } | Out-Null
}

Run-Check "server ready" "transport" "critical" { Invoke-RestMethod "$BaseUrl/health" } {
    param($r) Assert-Result ($r.status -eq "ok") "status=$($r.status)"
} | Out-Null

$status = Run-Check "status baseline" "baseline" "critical" { Invoke-Tool "repository_status" @{ repositoryId = $RepositoryId } } {
    param($r)
    Assert-Result ($r.symbols -gt 1000 -and $r.relationships -gt 1000 -and -not $r.indexing) "symbols=$($r.symbols), edges=$($r.relationships), indexing=$($r.indexing)" $r
}

Run-Flow "flow: new agent repo discovery plus overview" {
    [pscustomobject]@{
        repos = Invoke-Tool "list_repositories" @{}
        overview = Invoke-Tool "repo_overview" @{ repositoryId = $RepositoryId }
    }
} 20000 2500 "high"

Run-Flow "flow: locate C# symbol, graph, source" {
    $s = Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "CreateDbContext"; limit = 2 }
    [pscustomobject]@{
        search = $s
        symbol = Invoke-Tool "get_symbol" @{ repositoryId = $RepositoryId; symbolId = $s.items[0].symbol.id }
        callees = Invoke-Tool "find_callees" @{ repositoryId = $RepositoryId; symbolId = $s.items[0].symbol.id; depth = 1; limit = 20 }
        source = Invoke-Tool "get_source" @{ repositoryId = $RepositoryId; filePath = $s.items[0].symbol.filePath; startLine = 6; endLine = 24 }
    }
} 30000 2500 "high"

Run-Flow "flow: locate TS message and references" {
    $s = Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "GCToClientTeamsInfo"; limit = 3 }
    [pscustomobject]@{
        search = $s
        references = Invoke-Tool "find_references" @{ repositoryId = $RepositoryId; symbolId = $s.items[0].symbol.id; depth = 1; limit = 40 }
        source = Invoke-Tool "get_source" @{ repositoryId = $RepositoryId; filePath = $s.items[0].symbol.filePath; startLine = $s.items[0].symbol.startLine; endLine = ($s.items[0].symbol.startLine + 5) }
    }
} 30000 2500 "high"

Run-Flow "flow: inspect TS module outline then bounded source" {
    [pscustomObject]@{
        outline = Invoke-Tool "file_outline" @{ repositoryId = $RepositoryId; filePath = "SKYNET server/GC/570/modules/Match.ts" }
        source = Invoke-Tool "get_source" @{ repositoryId = $RepositoryId; filePath = "SKYNET server/GC/570/modules/Match.ts"; startLine = 145; endLine = 289 }
    }
} 90000 2500 "medium"

Run-Flow "flow: batch three-step workflow" {
    $s = Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "NormalizeJson"; limit = 1 }
    Invoke-Tool "batch" @{ calls = @(
        @{ tool = "get_symbol"; arguments = @{ repositoryId = $RepositoryId; symbolId = $s.items[0].symbol.id } },
        @{ tool = "find_callers"; arguments = @{ repositoryId = $RepositoryId; symbolId = $s.items[0].symbol.id; depth = 1; limit = 20 } },
        @{ tool = "get_source"; arguments = @{ repositoryId = $RepositoryId; filePath = $s.items[0].symbol.filePath; startLine = [Math]::Max(1, $s.items[0].symbol.startLine - 5); endLine = ($s.items[0].symbol.startLine + 20) } }
    ) }
} 70000 2500 "medium"

Run-Flow "flow: generated file orientation is bounded" {
    [pscustomobject]@{
        files = Invoke-Tool "list_files" @{ repositoryId = $RepositoryId; contains = "Generated"; limit = 20 }
        outline = Invoke-Tool "file_outline" @{ repositoryId = $RepositoryId; filePath = "SKYNET server/GC/570/generated/dota.ts" }
    }
} 180000 3000 "medium"

Run-QuerySeries "series: mixed exact searches" @(
    "CreateDbContext",
    "SteamDatagramCertificateMessage",
    "PersistenceRoundTripCheck",
    "NormalizeJson",
    "requestGuildData",
    "summarizeMatchStateHistory",
    "GCToClientTeamsInfo",
    "DotaDbContextFactory",
    "SteamDbContextFactory",
    "NoSuchSymbol_0001",
    "NoSuchSymbol_0002",
    "NoSuchSymbol_0003"
) 150 600 80000

Run-Check "batch cap: rejects eleven calls" "limits" "critical" {
    Invoke-Mcp "tools/call" @{ name = "batch"; arguments = @{ calls = @(1..11 | ForEach-Object { @{ tool = "repository_status"; arguments = @{ repositoryId = $RepositoryId } } }) } }
} {
    param($r)
    $text = if ($r.result.content) { [string]@($r.result.content)[0].text } else { "" }
    Assert-Result ($r.result.isError -eq $true -and $text -match "10|max|batch") "isError=$($r.result.isError), text=$text"
} | Out-Null

Run-Check "graph expansion stays bounded" "limits" "high" {
    $s = Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "Proto"; limit = 1 }
    Invoke-Tool "get_graph" @{ repositoryId = $RepositoryId; symbolId = $s.items[0].symbol.id; depth = 5; limit = 20 }
} {
    param($r, $ms)
    $bytes = Json-Bytes $r
    Assert-Result (@($r.nodes).Count -le 20 -and $r.truncated -eq $true -and $bytes -lt 50000 -and $ms -lt 2500) "nodes=$(@($r.nodes).Count), edges=$(@($r.edges).Count), truncated=$($r.truncated), bytes=$bytes, ms=$ms" $r
} | Out-Null

Run-Check "source cap prevents whole generated file dump" "limits" "critical" {
    Invoke-Tool "get_source" @{ repositoryId = $RepositoryId; filePath = "SKYNET server/GC/570/generated/dota.ts"; startLine = 1; endLine = 999999 }
} {
    param($r)
    $lines = @([string]$r.content -split "`n").Count
    $bytes = Json-Bytes $r
    Assert-Result ($r.truncated -eq $true -and $lines -le 400 -and $bytes -lt 80000) "lines=$lines, truncated=$($r.truncated), bytes=$bytes" $r
} | Out-Null

Run-Check "literal search does not pretend symbol coverage" "token-policy" "medium" {
    $mcp = Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "UpdatedAtUtc"; includeTextual = $true; limit = 20 }
    $rg = @(rg --fixed-strings --line-number --column "UpdatedAtUtc" $RootPath 2>$null).Count
    [pscustomobject]@{ mcp = $mcp; rg = $rg }
} {
    param($r)
    $json = $r.mcp | ConvertTo-Json -Depth 50 -Compress
    $explicit = $json -match "textual|literal|disabled|not indexed"
    Assert-Result (($r.rg -eq 0) -or (@($r.mcp.items).Count -gt 0) -or $explicit) "mcp=$(@($r.mcp.items).Count), rg=$($r.rg), explicit=$explicit" $r
} | Out-Null

Run-Check "response bodies do not contain absolute local paths" "token-policy" "high" {
    $payload = [pscustomobject]@{
        repos = Invoke-Tool "list_repositories" @{}
        overview = Invoke-Tool "repo_overview" @{ repositoryId = $RepositoryId }
        search = Invoke-Tool "search_symbols" @{ repositoryId = $RepositoryId; query = "CreateDbContext"; limit = 2; includeRelationships = $true }
    }
    $json = $payload | ConvertTo-Json -Depth 100 -Compress
    [pscustomobject]@{ json = $json; bytes = [Text.Encoding]::UTF8.GetByteCount($json) }
} {
    param($r)
    $hasAbs = $r.json -match [regex]::Escape("C:\SERVER\") -or $r.json -match [regex]::Escape("D:\Install\")
    Assert-Result (-not $hasAbs) "hasAbsolutePath=$hasAbs, bytes=$($r.bytes)"
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
