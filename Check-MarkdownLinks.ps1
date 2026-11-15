# Checks local relative links (files and #anchors-ignored) in the repository's public Markdown.
# Usage: .\Check-MarkdownLinks.ps1   (exit code 1 if any link is broken)
$root = $PSScriptRoot
$files = git -C $root ls-files --cached --others --exclude-standard -- '*.md' | Where-Object { $_ -notmatch '^(internal|windows/hub/src|windows/hub/tests)/' }
$bad = 0
foreach ($f in $files) {
  $path = Join-Path $root $f
  if (-not (Test-Path -LiteralPath $path)) { continue }
  $text = Get-Content -LiteralPath $path -Raw
  $text = [regex]::Replace($text, '(?s)```.*?```', '')
  $targets = @()
  $targets += [regex]::Matches($text, '\]\(([^)\s]+(?:%20[^)\s]*)*)(?:\s+"[^"]*")?\)') | ForEach-Object { $_.Groups[1].Value }
  $targets += [regex]::Matches($text, '(?:src|href)="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
  foreach ($t in $targets) {
    if ($t -match '^(https?:|mailto:|#|/\S*\.md$)' -and $t -notmatch '^/') { continue }
    $t2 = ($t -split '#')[0]
    if (-not $t2) { continue }
    $t2 = [uri]::UnescapeDataString($t2)
    if ($t2.StartsWith('/')) { $target = Join-Path $root $t2.TrimStart('/') } else { $target = Join-Path (Split-Path $path) $t2 }
    if (-not (Test-Path -LiteralPath $target)) { "BROKEN  $f -> $t"; $bad++ }
  }
}
if ($bad) { "$bad broken link(s)"; exit 1 } else { "All local links OK ($($files.Count) files checked)" }
