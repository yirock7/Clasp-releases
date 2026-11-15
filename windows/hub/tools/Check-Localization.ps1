<#
.SYNOPSIS
  Checks that Clasp's user-facing text is localizable (docs/LOCALIZATION-PLAN.md, phase 1). Exit code 1 on findings.

.DESCRIPTION
  Prints one finding per line as  file:line: [rule] message  (paths relative to windows\hub). Rules:

    missing-resw       an x:Uid="X" in src\AppleDeviceHub.App\**\*.xaml with no "X.<Property>" entry in
                       Strings\en-US\Resources.resw (matched by the Uid prefix before the first '.')
    unused-resw        a Resources.resw entry whose Uid prefix is not used by any x:Uid in XAML
    missing-resx       a Strings.<Name> reference in src\**\*.cs whose key is not in Core\Resources\Strings.resx
    no-comment         a Strings.resx / Resources.resw entry without a <comment>
    literal            user-facing literal text left in Views\*.xaml or App.xaml: attributes Text, Content, Header,
                       PlaceholderText, Title, Description, Label, OffContent, OnContent, CloseButtonText,
                       PrimaryButtonText, SecondaryButtonText, ToolTipService.ToolTip, AutomationProperties.Name,
                       AutomationProperties.HelpText (also via <Setter Property=... Value=...>), and the text content of
                       <TextBlock>, <Run>, <Button> and similar elements or <X.Text>/<X.Content> property elements.
                       Not flagged: markup extensions ({Binding}, {StaticResource}, {x:Bind}, ...), empty/whitespace,
                       the exact word "Clasp", and anything with no letters at all (icon glyphs such as &#xE000; or
                       , punctuation, numbers).
    redundant-literal  the same kind of literal on an element that also has an x:Uid (the resource should be the
                       only source of the text).

  Justified exceptions go into tools\localization-allowlist.txt, one entry per line:
      File.xaml:123                 (a line number)
      File.xaml:Some literal text   (the exact literal)
      Resources.resw:SomeUid.Text   (the key, for unused-resw / no-comment)
  Lines starting with # are comments.

  -Summary prints only the counts (per rule and, for literal findings, per XAML file).

.EXAMPLE
  powershell -File tools\Check-Localization.ps1
  powershell -File tools\Check-Localization.ps1 -Summary
#>
[CmdletBinding()]
param(
    [switch]$Summary
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$hubRoot = Split-Path -Parent $PSScriptRoot
$srcRoot = Join-Path $hubRoot 'src'
$appRoot = Join-Path $srcRoot 'AppleDeviceHub.App'
$resxPath = Join-Path $srcRoot 'AppleDeviceHub.Core\Resources\Strings.resx'
$reswPath = Join-Path $appRoot 'Strings\en-US\Resources.resw'
$allowPath = Join-Path $PSScriptRoot 'localization-allowlist.txt'

$findings = New-Object System.Collections.Generic.List[object]
function Rel([string]$full) { if ($full.StartsWith($hubRoot, [StringComparison]::OrdinalIgnoreCase)) { return $full.Substring($hubRoot.Length).TrimStart('\', '/') } return $full }
function Add-Finding([string]$file, [int]$line, [string]$rule, [string]$msg, [string]$key) {
    $findings.Add([pscustomobject]@{ File = (Rel $file); Line = $line; Rule = $rule; Message = $msg; Key = $key }) | Out-Null
}

# Files under $root matching $filter, skipping bin/obj directories at any depth.
function Get-SourceFiles([string]$root, [string]$filter) {
    $out = New-Object System.Collections.Generic.List[string]
    $pending = New-Object System.Collections.Generic.Stack[string]
    $pending.Push($root)
    while ($pending.Count -gt 0) {
        $dir = $pending.Pop()
        foreach ($f in [System.IO.Directory]::GetFiles($dir, $filter)) { $out.Add($f) }
        foreach ($d in [System.IO.Directory]::GetDirectories($dir)) {
            $leaf = [System.IO.Path]::GetFileName($d)
            if ($leaf -ne 'bin' -and $leaf -ne 'obj' -and $leaf -ne '.git') { $pending.Push($d) }
        }
    }
    return $out
}

# --- allowlist ---------------------------------------------------------------------------------------------------
$allow = @()
if (Test-Path -LiteralPath $allowPath) {
    foreach ($raw in [System.IO.File]::ReadAllLines($allowPath, [System.Text.Encoding]::UTF8)) {
        $t = $raw.Trim()
        if ($t.Length -eq 0 -or $t.StartsWith('#')) { continue }
        $i = $t.IndexOf(':')
        if ($i -le 0) { continue }
        $allow += [pscustomobject]@{ File = $t.Substring(0, $i).Trim(); Spec = $t.Substring($i + 1).Trim() }
    }
}
function Test-Allowed($f) {
    $leaf = [System.IO.Path]::GetFileName($f.File)
    foreach ($a in $allow) {
        if (-not ($leaf -eq $a.File -or $f.File -eq $a.File)) { continue }
        if ($a.Spec -match '^\d+$') { if ([int]$a.Spec -eq $f.Line) { return $true } }
        elseif ($a.Spec -ceq $f.Key) { return $true }
    }
    return $false
}

# --- resx / resw data entries ----------------------------------------------------------------------------------
function Read-DataEntries([string]$path) {
    $list = New-Object System.Collections.Generic.List[object]
    $r = New-Object System.Xml.XmlTextReader($path)
    $r.Namespaces = $false
    $r.WhitespaceHandling = 'None'
    try {
        $cur = $null
        while ($r.Read()) {
            $nt = [string]$r.NodeType
            if ($nt -eq 'Element') {
                if ($r.Name -eq 'data') {
                    $line = $r.LineNumber
                    $name = $r.GetAttribute('name')
                    $isBinary = ($null -ne $r.GetAttribute('type')) -or ($null -ne $r.GetAttribute('mimetype'))
                    $cur = [pscustomobject]@{ Name = $name; Line = $line; HasComment = $false; IsBinary = $isBinary }
                    $list.Add($cur)
                    if ($r.IsEmptyElement) { $cur = $null }
                } elseif ($r.Name -eq 'comment' -and $null -ne $cur) {
                    if (-not $r.IsEmptyElement) {
                        $text = $r.ReadElementContentAsString()
                        if ($text.Trim().Length -gt 0) { $cur.HasComment = $true }
                    }
                }
            } elseif ($nt -eq 'EndElement' -and $r.Name -eq 'data') { $cur = $null }
        }
    } finally { $r.Close() }
    return $list
}

# --- XAML scan -----------------------------------------------------------------------------------------------------
$userAttrs = @{}
foreach ($n in 'Text', 'Content', 'Header', 'PlaceholderText', 'Title', 'Description', 'Label', 'OffContent', 'OnContent',
    'CloseButtonText', 'PrimaryButtonText', 'SecondaryButtonText', 'ToolTipService.ToolTip',
    'AutomationProperties.Name', 'AutomationProperties.HelpText') { $userAttrs[$n] = $true }
$textParents = @{}
foreach ($n in 'TextBlock', 'Run', 'Span', 'Bold', 'Italic', 'Underline', 'Hyperlink', 'Button', 'HyperlinkButton',
    'RepeatButton', 'ToggleButton', 'CheckBox', 'RadioButton', 'ContentControl', 'ContentPresenter', 'MenuFlyoutItem',
    'ToggleMenuFlyoutItem', 'ComboBoxItem', 'ListViewItem', 'ToolTip', 'String', 'AppBarButton', 'AppBarToggleButton') { $textParents[$n] = $true }
$propElementRegex = New-Object System.Text.RegularExpressions.Regex('\.(Text|Content|Header|PlaceholderText|Title|Description|Label|OffContent|OnContent|ToolTip|HelpText)$')
$escRegex = New-Object System.Text.RegularExpressions.Regex('\\[uU][0-9A-Fa-f]{4,8}')
$letterRegex = New-Object System.Text.RegularExpressions.Regex('\p{L}')

# Returns $true when $v is text a person would read (and so must come from a resource).
function Test-UserFacing([string]$v) {
    $t = $v.Trim()
    if ($t.Length -eq 0) { return $false }
    if ($t.StartsWith('{') -and -not $t.StartsWith('{}')) { return $false }   # markup extension
    if ($t -ceq 'Clasp') { return $false }
    $stripped = $escRegex.Replace($t, '')
    if (-not $letterRegex.IsMatch($stripped)) { return $false }               # glyphs, punctuation, numbers
    return $true
}
function Get-Local([string]$qname) { $i = $qname.LastIndexOf(':'); if ($i -ge 0) { return $qname.Substring($i + 1) } return $qname }

$uids = @{}   # Uid prefix -> first "file:line"
function Scan-Xaml([string]$path, [bool]$checkLiterals) {
    $r = New-Object System.Xml.XmlTextReader($path)
    $r.Namespaces = $false
    $r.WhitespaceHandling = 'None'
    $stack = New-Object System.Collections.Generic.Stack[object]
    try {
        while ($r.Read()) {
            $nt = [string]$r.NodeType
            if ($nt -eq 'Element') {
                $qname = $r.Name
                $local = Get-Local $qname
                $line = $r.LineNumber
                $empty = $r.IsEmptyElement
                $names = New-Object System.Collections.Generic.List[string]
                $vals = New-Object System.Collections.Generic.List[string]
                $lines = New-Object System.Collections.Generic.List[int]
                while ($r.MoveToNextAttribute()) { $names.Add($r.Name); $vals.Add($r.Value); $lines.Add($r.LineNumber) }
                [void]$r.MoveToElement()

                $uid = $null
                $setterProp = $null; $setterVal = $null; $setterLine = $line
                for ($i = 0; $i -lt $names.Count; $i++) {
                    if ($names[$i] -eq 'x:Uid') {
                        $uid = $vals[$i]
                        $prefix = $uid.Split('.')[0]
                        if (-not $uids.ContainsKey($prefix)) { $uids[$prefix] = $true }
                        $uidLine = $lines[$i]
                        $script:uidUses.Add([pscustomobject]@{ Uid = $prefix; Full = $uid; File = $path; Line = $uidLine }) | Out-Null
                    }
                    if ($local -eq 'Setter') {
                        if ($names[$i] -eq 'Property') { $setterProp = $vals[$i] }
                        elseif ($names[$i] -eq 'Value') { $setterVal = $vals[$i]; $setterLine = $lines[$i] }
                    }
                }
                if ($checkLiterals) {
                    for ($i = 0; $i -lt $names.Count; $i++) {
                        if (-not $userAttrs.ContainsKey($names[$i])) { continue }
                        if (-not (Test-UserFacing $vals[$i])) { continue }
                        $lit = $vals[$i].Trim()
                        if ($null -ne $uid) {
                            Add-Finding $path $lines[$i] 'redundant-literal' ("{0}=""{1}"" beside x:Uid=""{2}"": remove the literal, the resource supplies it" -f $names[$i], $lit, $uid) $lit
                        } else {
                            Add-Finding $path $lines[$i] 'literal' ("{0}=""{1}"" on <{2}>: move to a resource (x:Uid)" -f $names[$i], $lit, $qname) $lit
                        }
                    }
                    if ($local -eq 'Setter' -and $null -ne $setterProp -and $null -ne $setterVal) {
                        $pname = $setterProp
                        if ($userAttrs.ContainsKey($pname) -and (Test-UserFacing $setterVal)) {
                            Add-Finding $path $setterLine 'literal' ("<Setter Property=""{0}"" Value=""{1}""> holds literal text: move to a resource" -f $pname, $setterVal.Trim()) $setterVal.Trim()
                        }
                    }
                }
                if (-not $empty) { $stack.Push([pscustomobject]@{ Name = $qname; Local = $local; HasUid = ($null -ne $uid) }) }
            } elseif ($nt -eq 'EndElement') {
                if ($stack.Count -gt 0) { [void]$stack.Pop() }
            } elseif ($checkLiterals -and ($nt -eq 'Text' -or $nt -eq 'CDATA') -and $stack.Count -gt 0) {
                $p = $stack.Peek()
                if ($textParents.ContainsKey($p.Local) -or $propElementRegex.IsMatch($p.Local)) {
                    if (Test-UserFacing $r.Value) {
                        $lit = $r.Value.Trim()
                        $line = $r.LineNumber
                        if ($p.HasUid) {
                            Add-Finding $path $line 'redundant-literal' ("text content ""{0}"" of <{1}> beside x:Uid: remove it, the resource supplies it" -f $lit, $p.Name) $lit
                        } else {
                            Add-Finding $path $line 'literal' ("text content ""{0}"" of <{1}>: move to a resource (x:Uid)" -f $lit, $p.Name) $lit
                        }
                    }
                }
            }
        }
    } finally { $r.Close() }
}

$script:uidUses = New-Object System.Collections.Generic.List[object]

$xamlFiles = Get-SourceFiles $appRoot '*.xaml'
$viewsDir = Join-Path $appRoot 'Views'
$appXaml = Join-Path $appRoot 'App.xaml'
foreach ($x in $xamlFiles) {
    $dir = [System.IO.Path]::GetDirectoryName($x)
    $isLiteralScope = ($dir -eq $viewsDir) -or ($x -eq $appXaml)
    Scan-Xaml $x $isLiteralScope
}

# --- resw: (a) missing, (b) unused, (d) comments ---------------------------------------------------------------------
$reswPrefixes = @{}
if (Test-Path -LiteralPath $reswPath) {
    $resw = Read-DataEntries $reswPath
    foreach ($e in $resw) {
        $prefix = $e.Name.Split('.')[0]
        $reswPrefixes[$prefix] = $true
        if (-not $e.HasComment -and -not $e.IsBinary) {
            Add-Finding $reswPath $e.Line 'no-comment' ("resw entry '{0}' has no <comment>" -f $e.Name) $e.Name
        }
        if (-not $uids.ContainsKey($prefix)) {
            Add-Finding $reswPath $e.Line 'unused-resw' ("resw entry '{0}' is not used: no x:Uid=""{1}"" in any XAML file" -f $e.Name, $prefix) $e.Name
        }
    }
} else {
    Add-Finding $reswPath 1 'missing-resw' 'Strings\en-US\Resources.resw not found' 'Resources.resw'
}
foreach ($u in $script:uidUses) {
    if (-not $reswPrefixes.ContainsKey($u.Uid)) {
        Add-Finding $u.File $u.Line 'missing-resw' ("x:Uid=""{0}"" has no '{1}.<Property>' entry in Strings\en-US\Resources.resw" -f $u.Full, $u.Uid) $u.Full
    }
}

# --- resx: (c) references, (d) comments ------------------------------------------------------------------------------
$resxKeys = @{}
if (Test-Path -LiteralPath $resxPath) {
    foreach ($e in (Read-DataEntries $resxPath)) {
        $resxKeys[$e.Name] = $true
        if (-not $e.HasComment -and -not $e.IsBinary) {
            Add-Finding $resxPath $e.Line 'no-comment' ("resx entry '{0}' has no <comment>" -f $e.Name) $e.Name
        }
    }
} else {
    Add-Finding $resxPath 1 'missing-resx' 'Core\Resources\Strings.resx not found' 'Strings.resx'
}
$refRegex = New-Object System.Text.RegularExpressions.Regex('\bStrings\.([A-Za-z_][A-Za-z0-9_]*)')
$skipMembers = @{ 'ResourceManager' = $true; 'Culture' = $true }
foreach ($cs in (Get-SourceFiles $srcRoot '*.cs')) {
    $leaf = [System.IO.Path]::GetFileName($cs)
    if ($leaf.EndsWith('.g.cs') -or $leaf.EndsWith('.g.i.cs') -or $leaf.EndsWith('.Designer.cs')) { continue }
    $text = [System.IO.File]::ReadAllText($cs)
    if ($text.IndexOf('Strings.', [StringComparison]::Ordinal) -lt 0) { continue }
    $n = 0
    foreach ($ln in $text.Split("`n")) {
        $n++
        if ($ln.IndexOf('Strings.', [StringComparison]::Ordinal) -lt 0) { continue }
        if ($ln.TrimStart().StartsWith('//')) { continue }
        foreach ($m in $refRegex.Matches($ln)) {
            $key = $m.Groups[1].Value
            if ($skipMembers.ContainsKey($key)) { continue }
            if (-not $resxKeys.ContainsKey($key)) {
                Add-Finding $cs $n 'missing-resx' ("Strings.{0} is not defined in Core\Resources\Strings.resx" -f $key) $key
            }
        }
    }
}

# --- report ----------------------------------------------------------------------------------------------------------
$remaining = New-Object System.Collections.Generic.List[object]
$allowed = 0
foreach ($f in $findings) { if (Test-Allowed $f) { $allowed++ } else { $remaining.Add($f) } }
$sorted = @($remaining | Sort-Object File, Line, Rule)

if (-not $Summary) {
    foreach ($f in $sorted) { Write-Output ("{0}:{1}: [{2}] {3}" -f $f.File, $f.Line, $f.Rule, $f.Message) }
}
Write-Output ''
Write-Output ("Checked {0} XAML file(s); {1} x:Uid use(s); {2} resw prefix(es); {3} resx key(s); {4} allowlisted." -f $xamlFiles.Count, $script:uidUses.Count, $reswPrefixes.Count, $resxKeys.Count, $allowed)
if ($sorted.Count -eq 0) {
    Write-Output 'Localization check passed: no findings.'
    exit 0
}
Write-Output ("{0} finding(s):" -f $sorted.Count)
foreach ($g in ($sorted | Group-Object Rule | Sort-Object Name)) { Write-Output ("  {0,-18} {1}" -f $g.Name, $g.Count) }
$lit = @($sorted | Where-Object { $_.Rule -eq 'literal' -or $_.Rule -eq 'redundant-literal' })
if ($lit.Count -gt 0) {
    Write-Output 'Literal findings per file:'
    foreach ($g in ($lit | Group-Object File | Sort-Object Name)) { Write-Output ("  {0,5}  {1}" -f $g.Count, $g.Name) }
}
exit 1
