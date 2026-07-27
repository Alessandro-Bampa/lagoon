<#
.SYNOPSIS
    Verifica che nessuna stringa visibile all'utente sia scritta nel codice e che ogni chiave di
    traduzione usata esista davvero.

.DESCRIPTION
    Nessuna CI in questa fase (CLAUDE.md par. 6): lo script si lancia a mano prima di considerare
    conclusa una fase, o dopo aver aggiunto UI/oggetti.

        pwsh tools/check-i18n.ps1          # report + exit code
        pwsh tools/check-i18n.ps1 -Quiet   # solo il riepilogo

    Tre controlli:
      1. LETTERALI  - testo naturale assegnato a proprieta' visibili (.Text, .TooltipText, voci di
                      PopupMenu, ...) nei .cs, e testo non-chiave nei .tscn.
      2. MANCANTI   - chiavi referenziate dal codice o dalle scene ma assenti dai cataloghi.
      3. INCOMPLETE - righe dei CSV con una colonna di lingua vuota, e chiavi mai referenziate.

    Un letterale legittimo (un simbolo, non testo naturale) si esenta con `// i18n-ignore` sulla
    stessa riga. Nelle scene si usa `auto_translate_mode = 2` sul nodo.

    Convenzioni e razionale: .claude/skills/i18n-localization/SKILL.md
#>
[CmdletBinding()]
param(
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Chiavi generate per convenzione: non compaiono mai come letterale nel codice (si costruiscono da
# un ItemId o dal nome di un enum), quindi non vanno segnalate come "mai referenziate".
$conventionPrefixes = @('ITEM_', 'SLOT_', 'CATEGORY_', 'UI_MOUSE_', 'DLG_')

# Proprieta' che finiscono sotto gli occhi del giocatore. Se ne aggiungi una qui, il controllo la
# copre ovunque nel progetto.
$visibleProperties = 'Text|TooltipText|PlaceholderText|Title|SlotLabel'

# Una chiave contiene SEMPRE almeno un underscore (UI_, ITEM_, NET_ERR_, SLOT_, ...): e' cio' che
# la distingue da un identificatore maiuscolo qualsiasi (il nome di un bus audio, un acronimo).
$keyPattern = '^[A-Z][A-Z0-9]*(_[A-Z0-9]+)+$'

$findings = New-Object System.Collections.Generic.List[object]
function Add-Finding([string]$Kind, [string]$File, [int]$Line, [string]$Message) {
    $findings.Add([pscustomobject]@{ Kind = $Kind; File = $File; Line = $Line; Message = $Message })
}

function Get-RelativePath([string]$Path) {
    return $Path.Substring($root.Length).TrimStart('\', '/') -replace '\\', '/'
}

# ======================================================================================
#  1. Catalogo delle chiavi (CSV + PO)
# ======================================================================================

$catalog = @{}   # chiave -> file di origine

foreach ($csv in Get-ChildItem -Path (Join-Path $root 'locales') -Filter '*.csv' -Recurse) {
    $rel = Get-RelativePath $csv.FullName
    $rows = Import-Csv -Path $csv.FullName -Encoding UTF8
    $columns = @($rows[0].PSObject.Properties.Name)
    if ($columns[0] -ne 'keys') {
        Add-Finding 'LETTERALI' $rel 1 "La prima colonna deve chiamarsi 'keys' (trovata '$($columns[0])')."
        continue
    }

    $lineNo = 1
    foreach ($row in $rows) {
        $lineNo++
        $key = $row.keys
        if ([string]::IsNullOrWhiteSpace($key)) { continue }

        if ($catalog.ContainsKey($key)) {
            Add-Finding 'INCOMPLETE' $rel $lineNo "Chiave duplicata '$key' (gia' in $($catalog[$key]))."
        }
        $catalog[$key] = $rel

        foreach ($locale in $columns | Select-Object -Skip 1) {
            if ([string]::IsNullOrWhiteSpace($row.$locale)) {
                Add-Finding 'INCOMPLETE' $rel $lineNo "'$key' non ha traduzione per '$locale'."
            }
        }
    }
}

foreach ($po in Get-ChildItem -Path (Join-Path $root 'locales') -Filter '*.po' -Recurse) {
    $rel = Get-RelativePath $po.FullName
    $n = 0
    foreach ($line in Get-Content -Path $po.FullName -Encoding UTF8) {
        $n++
        $m = [regex]::Match($line, '^\s*msgid\s+"(.+)"\s*$')
        if ($m.Success) { $catalog[$m.Groups[1].Value] = $rel }
    }
}

# ======================================================================================
#  2. Scansione del codice C#
# ======================================================================================

$referenced = New-Object System.Collections.Generic.HashSet[string]

$excludedDirs = '\\(\.godot|addons|obj|bin|\.git)\\'
$sources = Get-ChildItem -Path $root -Filter '*.cs' -Recurse |
    Where-Object { $_.FullName -notmatch $excludedDirs }

foreach ($file in $sources) {
    $rel = Get-RelativePath $file.FullName
    # Loc.cs e' l'implementazione del meccanismo: i suoi letterali non sono testo di gioco.
    if ($rel -eq 'core/Utils/Loc.cs') { continue }

    $n = 0
    foreach ($line in Get-Content -Path $file.FullName -Encoding UTF8) {
        $n++

        # Qualunque letterale in forma di CHIAVE conta come riferimento, ovunque compaia: le chiavi
        # non passano solo per Loc.T(...) ma anche per helper come AddEntry(key, ...),
        # AddColumn(..., key, ...) o SetStatus(key, args). Un elenco di firme da riconoscere sarebbe
        # sempre in ritardo di una funzione.
        foreach ($m in [regex]::Matches($line, '"([A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+)"')) {
            [void]$referenced.Add($m.Groups[1].Value)
        }

        if ($line -match '//\s*i18n-ignore') { continue }
        # I messaggi di sola console non sono UI e restano in italiano.
        if ($line -match 'GD\.(Print|PrintErr|PrintRich|PushWarning|PushError)') { continue }

        # Letterali assegnati a una proprieta' visibile, o passati come voce di menu.
        $patterns = @(
            "(?:$visibleProperties)\s*=\s*""([^""]*)""",
            'AddItem\(\s*"([^"]*)"',
            'SetItemTooltip\([^,]+,\s*"([^"]*)"'
        )
        foreach ($pattern in $patterns) {
            foreach ($m in [regex]::Matches($line, $pattern)) {
                $value = $m.Groups[1].Value
                if ([string]::IsNullOrEmpty($value)) { continue }

                if ($value -cmatch $keyPattern) {
                    # E' una chiave: la risolve l'auto-translate dei Control.
                    [void]$referenced.Add($value)
                }
                else {
                    Add-Finding 'LETTERALI' $rel $n "Stringa scritta nel codice: '$value' - usa Loc.T('CHIAVE') o marca la riga con // i18n-ignore."
                }
            }
        }
    }
}

# ======================================================================================
#  3. Scansione delle scene
# ======================================================================================

$scenes = Get-ChildItem -Path $root -Include '*.tscn', '*.tres' -Recurse |
    Where-Object { $_.FullName -notmatch $excludedDirs }

foreach ($file in $scenes) {
    $rel = Get-RelativePath $file.FullName
    $n = 0
    $autoTranslateOff = $false

    foreach ($line in Get-Content -Path $file.FullName -Encoding UTF8) {
        $n++
        # Un nuovo nodo azzera il contesto: auto_translate_mode vale solo per il nodo che lo dichiara.
        if ($line -match '^\[node ') { $autoTranslateOff = $false; continue }
        if ($line -match '^auto_translate_mode\s*=\s*2') { $autoTranslateOff = $true; continue }

        $m = [regex]::Match($line, '^(text|placeholder_text|tooltip_text)\s*=\s*"([^"]*)"')
        if (-not $m.Success) { continue }

        $value = $m.Groups[2].Value
        if ([string]::IsNullOrEmpty($value)) { continue }

        if ($value -cmatch $keyPattern) {
            [void]$referenced.Add($value)
        }
        elseif (-not $autoTranslateOff) {
            Add-Finding 'LETTERALI' $rel $n "Testo nella scena: '$value' - sostituiscilo con una chiave, oppure dichiara auto_translate_mode = 2 se e' un valore scritto a runtime."
        }
    }
}

# ======================================================================================
#  4. Confronto catalogo <-> riferimenti
# ======================================================================================

foreach ($key in $referenced) {
    if (-not $catalog.ContainsKey($key)) {
        Add-Finding 'MANCANTI' 'locales/' 0 "La chiave '$key' e' usata ma non esiste in nessun catalogo."
    }
}

foreach ($key in $catalog.Keys) {
    if ($referenced.Contains($key)) { continue }
    $isConvention = $false
    foreach ($prefix in $conventionPrefixes) {
        if ($key.StartsWith($prefix)) { $isConvention = $true; break }
    }
    if (-not $isConvention) {
        Add-Finding 'INCOMPLETE' $catalog[$key] 0 "La chiave '$key' non e' referenziata da nessuna parte (rimuovila o usala)."
    }
}

# ======================================================================================
#  Report
# ======================================================================================

if (-not $Quiet) {
    foreach ($kind in @('LETTERALI', 'MANCANTI', 'INCOMPLETE')) {
        $group = @($findings | Where-Object { $_.Kind -eq $kind })
        if ($group.Count -eq 0) { continue }

        Write-Host ""
        Write-Host "[$kind] $($group.Count)" -ForegroundColor Yellow
        foreach ($f in $group) {
            $where = $f.File
            if ($f.Line -gt 0) { $where = "$($f.File):$($f.Line)" }
            Write-Host "  $where`n    $($f.Message)"
        }
    }
}

Write-Host ""
Write-Host "Chiavi in catalogo: $($catalog.Count)   referenziate: $($referenced.Count)   segnalazioni: $($findings.Count)"

if ($findings.Count -gt 0) {
    Write-Host "check-i18n: FALLITO" -ForegroundColor Red
    exit 1
}

Write-Host "check-i18n: OK" -ForegroundColor Green
exit 0
