#!/usr/bin/env bash
# Run after every 'dotnet ef migrations add': empties old designers to cut build memory.
set -euo pipefail

dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/NOOSE-Website/Data/Migrations"
keep_newest=3
# data ops without columnTypes and column renames need a model: own designer for Up, previous one for Down
exceptions=(
  "20261103000239_Phase109_FibTexte.Designer.cs"
  "20260804192734_Phase56_GegenaufklaerungsRegeln.Designer.cs"
  "20260819092159_Oeffentlich08_HinweisUebernahme.Designer.cs"
  "20261006124329_Phase86_PartnerBehoerdenprofil.Designer.cs"
  "20261014112112_Phase93_Ehrungen.Designer.cs"
  "20261113235508_Phase128_PartnerEinheiten.Designer.cs"
  "20260819083847_Oeffentlich08_HinweisTriage.Designer.cs"
  "20260903090101_Oeffentlich07_HinweisLesestandUndPrio.Designer.cs"
  "20260908083814_Oeffentlich21_TicketAnhaenge.Designer.cs"
  "20260609133913_AgentCodenameKlarnameDienstnummer.Designer.cs"
  "20260721072858_Phase41_BesprechungZweiErinnerungen.Designer.cs"
  "20260807161825_Phase66_NooseiKurzbrief.Designer.cs"
  "20260609020929_Phase2_AktenzeichenZaehlerFix.Designer.cs"
  "20260720091239_Phase40_BesprechungenUndAbmeldungen.Designer.cs"
  "20260807160610_Phase65_KiKontingente.Designer.cs"
)

export LC_ALL=C
mapfile -t designers < <(cd "$dir" && printf '%s\n' *.Designer.cs | sort)
total=${#designers[@]}
changed=0

for ((i = 0; i < total - keep_newest; i++)); do
  name="${designers[$i]}"
  skip=0
  for e in "${exceptions[@]}"; do [[ "$name" == "$e" ]] && skip=1; done
  [[ $skip -eq 1 ]] && continue
  file="$dir/$name"
  grep -q 'BuildTargetModel' "$file" || continue

  tmp="$(mktemp)"
  # drop the doc line plus the method, keep only the usings the two attributes need
  awk '
    /^using / && $0 !~ /^using (Microsoft\.EntityFrameworkCore\.Infrastructure|Microsoft\.EntityFrameworkCore\.Migrations|NOOSE_Website\.Data);\r?$/ { next }
    /^        \/\/\/ <inheritdoc \/>\r?$/ { held = $0; next }
    /^        protected override void BuildTargetModel/ { skipping = 1; held = ""; next }
    skipping { if ($0 ~ /^        }\r?$/) skipping = 0; next }
    { if (held != "") { print held; held = "" } print }
  ' "$file" > "$tmp"
  cat "$tmp" > "$file"
  rm -f "$tmp"
  changed=$((changed + 1))
  echo "emptied $name"
done

echo "$changed designer(s) emptied"
