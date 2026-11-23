#!/usr/bin/env bash
# Empties BuildTargetModel in the old migration designers. Run it after every 'dotnet ef migrations add' (from the repo
# root or scripts/), before the commit: the compiler needs far less memory without ~200 copies of the model, and the
# unit test DesignerTests fails if more than Phase109 and the five newest designers still carry one.
# Kept as they are: the exceptions below and the three newest migrations (the fresh one plus the two before it).
# Idempotent: a designer without BuildTargetModel is left alone.
set -euo pipefail

dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/NOOSE-Website/Data/Migrations"
keep_newest=3
exceptions=("20261103000239_Phase109_FibTexte.Designer.cs")

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
