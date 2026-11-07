# Collects every UI text of the app into app/i18n/messages.pot (gettext template).
# Translators (Weblate, Crowdin, Poedit…) start from this file; run it again after adding texts.
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$src = Join-Path $root 'app\src'
$out = Join-Path $root 'app\i18n\messages.pot'
$entries = [ordered]@{}

function Add([string]$id, [string]$plural, [string]$where) {
    if ($id -notmatch '\p{L}.*\p{L}' -or $id -eq 'Valheim Builder Planner') { return }
    if (-not $entries.Contains($id)) { $entries[$id] = @{ Plural = $plural; Where = @() } }
    $entries[$id].Where += $where
}

$str = '"((?:[^"\\]|\\.)*)"'
foreach ($file in Get-ChildItem $src -Filter *.cs) {
    $lines = Get-Content $file.FullName -Encoding utf8
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $l = $lines[$i]; $where = "src/$($file.Name):$($i + 1)"
        foreach ($m in [regex]::Matches($l, "Loc\.N\($str,\s*$str")) { Add $m.Groups[1].Value $m.Groups[2].Value $where }
        foreach ($m in [regex]::Matches($l, "Loc\.[TF]\($str")) { Add $m.Groups[1].Value $null $where }
        foreach ($m in [regex]::Matches($l, "UiTheme\.(?:Btn|Header)\($str")) { Add $m.Groups[1].Value $null $where }
        # Static control texts are translated automatically by Godot.
        foreach ($m in [regex]::Matches($l, "\b(?:Text|Title|PlaceholderText|DialogText|OkButtonText|CancelButtonText|TooltipText)\s*=\s*$str")) { Add $m.Groups[1].Value $null $where }
    }
    # Build step names (translated where they are shown).
    if ($file.Name -eq 'BuildSteps.cs') {
        $all = Get-Content $file.FullName -Raw -Encoding utf8
        $block = [regex]::Match($all, 'Names\s*=\s*\{([^}]*)\}').Groups[1].Value
        foreach ($m in [regex]::Matches($block, $str)) { Add $m.Groups[1].Value $null "src/BuildSteps.cs" }
    }
}

# Extraction step names, shown translated on the first-launch screen.
foreach ($file in Get-ChildItem (Join-Path $root 'shared\GameData') -Filter *.cs) {
    $lines = Get-Content $file.FullName -Encoding utf8
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($m in [regex]::Matches($lines[$i], "\bP\([^,]+,\s*$str\)")) { Add $m.Groups[1].Value $null "shared/GameData/$($file.Name):$($i + 1)" }
    }
}

$sb = [Text.StringBuilder]::new()
[void]$sb.AppendLine('# Valheim Builder Planner — UI texts. Game names (pieces, materials) come from the game itself and are not here.')
[void]$sb.AppendLine('msgid ""')
[void]$sb.AppendLine('msgstr ""')
[void]$sb.AppendLine('"Content-Type: text/plain; charset=UTF-8\n"')
[void]$sb.AppendLine('"Content-Transfer-Encoding: 8-bit\n"')
foreach ($k in $entries.Keys) {
    $e = $entries[$k]
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("#: $(($e.Where | Select-Object -Unique) -join ' ')")
    if ($k -match '\{\d') { [void]$sb.AppendLine('#, csharp-format') }
    [void]$sb.AppendLine("msgid `"$k`"")
    if ($e.Plural) {
        [void]$sb.AppendLine("msgid_plural `"$($e.Plural)`"")
        [void]$sb.AppendLine('msgstr[0] ""'); [void]$sb.AppendLine('msgstr[1] ""')
    } else { [void]$sb.AppendLine('msgstr ""') }
}
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
[IO.File]::WriteAllText($out, $sb.ToString(), [Text.UTF8Encoding]::new($false))
"$($entries.Count) texts -> $out"
