# Mock translation backends for real-host verification.
#
# This machine cannot reach translate.googleapis.com, so Google/DeepL/Gemini modes can only be
# exercised end-to-end by pointing the plugin at a local server. This script serves all three
# request shapes on one port and appends every request to a log file, so the caller can prove
# which backend was really used.
#
# The file is deliberately pure ASCII: Windows PowerShell 5.1 reads .ps1 files as ANSI unless
# they carry a BOM, which would mangle any CJK literal in here.
#
# Usage: powershell -File mock-backends.ps1 -Port 8099 -Log C:\path\requests.log

param(
    [int]$Port = 8099,
    [string]$Log = (Join-Path ([System.IO.Path]::GetTempPath()) 'betterlyrics-mock-requests.log'),
    # Return fixed Traditional Chinese sentences instead of echoing the input. Combined with a
    # screenshot of the host lyrics window this proves the plugin's Traditional-to-Simplified
    # pass really runs inside the host: the file below is Traditional, the window shows Simplified.
    [switch]$FixedTraditional
)

$tradSentences = @(
    [string]::Concat([char]0x9019, [char]0x662F, [char]0x7E41, [char]0x9AD4, [char]0x4E2D, [char]0x6587, [char]0x7684, [char]0x7B2C, [char]0x4E00, [char]0x884C),
    [string]::Concat([char]0x9019, [char]0x662F, [char]0x7E41, [char]0x9AD4, [char]0x4E2D, [char]0x6587, [char]0x7684, [char]0x7B2C, [char]0x4E8C, [char]0x884C),
    [string]::Concat([char]0x9019, [char]0x662F, [char]0x7E41, [char]0x9AD4, [char]0x4E2D, [char]0x6587, [char]0x7684, [char]0x7B2C, [char]0x4E09, [char]0x884C)
)

$ErrorActionPreference = 'Stop'
$prefix = "http://127.0.0.1:$Port/"

# U+8AAC (traditional "to speak") replaced by U+8BF4 (its simplified form). The plugin runs a
# Traditional-to-Simplified pass over every translation, so echoing the traditional character
# back proves that pass really executed instead of being skipped.
$trad = [string][char]0x8AAC
$simp = [string][char]0x8BF4

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add($prefix)
$listener.Start()

function Write-Log([string]$line) {
    $stamp = (Get-Date).ToString('HH:mm:ss.fff')
    Add-Content -Path $Log -Value "[$stamp] $line" -Encoding UTF8
}

Write-Log "listening on $prefix"

# Mirrors LyricsAligner's contract: exactly one output line per input line, so the plugin's own
# line-count check passes. Blank input lines stay blank.
function Translate-Lines([string[]]$lines) {
    $out = @()
    $i = 0
    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { $out += '' }
        elseif ($FixedTraditional.IsPresent) {
            $out += $tradSentences[$i % $tradSentences.Count]
            $i++
        }
        else { $out += $line.Replace($trad, $simp) }
    }
    return ($out -join "`n")
}

while ($listener.IsListening) {
    try {
        $ctx = $listener.GetContext()
    } catch {
        break
    }

    $req = $ctx.Request
    $res = $ctx.Response
    $path = $req.Url.AbsolutePath
    $body = ''
    if ($req.HasEntityBody) {
        $reader = New-Object System.IO.StreamReader($req.InputStream, $req.ContentEncoding)
        $body = $reader.ReadToEnd()
        $reader.Dispose()
    }

    try {
        if ($path -eq '/translate_a/single') {
            $q = $req.QueryString['q']
            $tl = $req.QueryString['tl']
            $translated = Translate-Lines ($q -split "`n")
            # Google's shape: [ [ [translatedSegment, originalSegment], ... ], ... ]
            # Built by hand: ConvertTo-Json in PowerShell 5.1 wraps a nested array into
            # {"value":...,"Count":N} when it comes from the pipeline, and the plugin parses
            # a bare array, so the wrapped form silently yields no parts.
            $sb = New-Object System.Text.StringBuilder
            [void]$sb.Append('[[')
            $first = $true
            $segments = @($translated -split "`n")
            for ($i = 0; $i -lt $segments.Count; $i++) {
                if (-not $first) { [void]$sb.Append(',') }
                $first = $false
                $t = $segments[$i].Replace('\', '\\').Replace('"', '\"').Replace("`r", '')
                # Real gtx keeps the line break inside each segment; a bare join without them
                # would collapse the whole lyric onto one line and the aligner would then blank
                # out every following line.
                if ($i -lt $segments.Count - 1) { $t = $t + '\n' }
                [void]$sb.Append('["').Append($t).Append('",""]')
            }
            [void]$sb.Append(']]')
            $payload = $sb.ToString()
            Write-Log "GOOGLE tl=$tl lines=$(($q -split "`n").Count) -> ok"
        }
        elseif ($path -eq '/v1/chat/completions') {
            $j = $body | ConvertFrom-Json
            $user = ($j.messages | Where-Object { $_.role -eq 'user' }).content
            $sys = ($j.messages | Where-Object { $_.role -eq 'system' }).content
            $translated = Translate-Lines ($user -split "`n")
            $resp = [ordered]@{
                id      = 'mock'
                model   = $j.model
                choices = @(@{ index = 0; finish_reason = 'stop'; message = @{ role = 'assistant'; content = $translated } })
                usage   = @{ total_tokens = 10 }
            }
            $payload = $resp | ConvertTo-Json -Depth 8 -Compress
            Write-Log "CHAT model=$($j.model) temp=$($j.temperature) sysLen=$($sys.Length) lines=$(($user -split "`n").Count) auth=$($req.Headers['Authorization']) -> ok"
        }
        elseif ($path -like '*:generateContent') {
            $j = $body | ConvertFrom-Json
            $text = $j.contents[0].parts[0].text
            $onlyLyrics = ($text -split "`n`n")[-1]
            $translated = Translate-Lines ($onlyLyrics -split "`n")
            $resp = @{ candidates = @(@{ content = @{ parts = @(@{ text = $translated }) } }) }
            $payload = $resp | ConvertTo-Json -Depth 8 -Compress
            Write-Log "GEMINI path=$path key=$($req.QueryString['key']) lines=$(($onlyLyrics -split "`n").Count) -> ok"
        }
        elseif ($path -like '*deepl*') {
            $form = @{}
            foreach ($pair in ($body -split '&')) {
                $kv = $pair -split '=', 2
                if ($kv.Count -eq 2) { $form[[uri]::UnescapeDataString($kv[0])] = [uri]::UnescapeDataString($kv[1]) }
            }
            $src = $form['text']
            $translated = Translate-Lines ($src -split "`n")
            $resp = @{ translations = @(@{ detected_source_language = 'EN'; text = $translated }) }
            $payload = $resp | ConvertTo-Json -Depth 8 -Compress
            Write-Log "DEEPL target=$($form['target_lang']) auth=$($req.Headers['Authorization']) lines=$(($src -split "`n").Count) -> ok"
        }
        else {
            $res.StatusCode = 404
            $payload = '{"error":"unknown path"}'
            Write-Log "UNKNOWN $path"
        }
    } catch {
        $res.StatusCode = 500
        $payload = '{"error":"mock failure"}'
        Write-Log "ERROR $path : $($_.Exception.Message)"
    }

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
    $res.ContentType = 'application/json; charset=utf-8'
    $res.ContentLength64 = $bytes.Length
    $res.OutputStream.Write($bytes, 0, $bytes.Length)
    $res.OutputStream.Close()
}

$listener.Stop()
Write-Log 'stopped'
