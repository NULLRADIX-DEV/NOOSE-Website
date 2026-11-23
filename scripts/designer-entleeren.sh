#!/usr/bin/env bash
# Empties BuildTargetModel in the old migration designers. Run it after every 'dotnet ef migrations add' (from the repo
# root or scripts/), before the commit: the compiler needs far less memory without ~200 copies of the model, and the
# unit test DesignerTests fails if more than Phase109 and the five newest designers still carry one.
# Kept as they are: the exceptions below and the three newest migrations (the fresh one plus the two before it).
# Idempotent: a designer without BuildTargetModel is left alone.
set -euo pipefail

dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/NOOSE-Website/Data/Migrations"
keep_newest=3
# data operations without columnTypes take their column types from a model: in Up from the migration's own designer,
# in Down from the designer of the migration before it. So the first seven stay for Up, the last three for Down.
exceptions=(
  "20261103000239_Phase109_FibTexte.Designer.cs"
  "20260804192734_Phase56_GegenaufklaerungsRegeln.Designer.cs"
  "20260819092159_Oeffentlich08_HinweisUebernahme.Designer.cs"
  "20260903095002_Oeffentlich10_TicketBeteiligteUndIntern.Designer.cs"
  "20260908084531_Phase78_KontoLoeschenEntkoppeln.Designer.cs"
  "20261006124329_Phase86_PartnerBehoerdenprofil.Designer.cs"
  "20261014112112_Phase93_Ehrungen.Designer.cs"
  "20261113235508_Phase128_PartnerEinheiten.Designer.cs"
  "20260819083847_Oeffentlich08_HinweisTriage.Designer.cs"
  "20260903090101_Oeffentlich07_HinweisLesestandUndPrio.Designer.cs"
  "20260908083814_Oeffentlich21_TicketAnhaenge.Designer.cs"
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
