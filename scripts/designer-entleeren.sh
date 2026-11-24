#!/usr/bin/env bash
# Run after every 'dotnet ef migrations add': empties old designers to cut build memory.
# designers a migration still needs stay full, see --list
set -euo pipefail

dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/NOOSE-Website/Data/Migrations"
keep_newest=3

export LC_ALL=C

# one line per hit
derive_needed() {
  local migrations
  mapfile -t migrations < <(cd "$dir" && printf '%s\n' [0-9]*_*.cs | grep -v '\.Designer\.cs$' | sort)
  (cd "$dir" && awk '
    function boundary(c) {
      if (pending != "") {
        if (c == "(") { call = pending; calltext = ""; depth = 1; pending = ""; return }
        if (c !~ /[ \t\r\n]/) pending = ""
      }
      if (ident != "") {
        if (ident == "TargetModel") hit("TargetModel")
        else if (ident ~ /^(InsertData|UpdateData|DeleteData|RenameColumn|RenameIndex)$/) pending = ident
        ident = ""
        if (pending != "" && c == "(") { call = pending; calltext = ""; depth = 1; pending = "" }
      }
    }
    function hit(what,    d) {
      if (section == "Down") { if (prev == "") return; d = prev } else d = base
      print d ".Designer.cs\t" base "\t" (section == "Down" ? "Down" : "Up") "\t" what
    }
    function endcall() {
      if (call ~ /^(RenameColumn|RenameIndex)$/ || calltext !~ /(^|[^A-Za-z0-9_])columnTypes[ \t\r\n]*:/) hit(call)
      call = ""
    }
    FNR == 1 {
      prev = last
      base = FILENAME; sub(/\.cs$/, "", base)
      last = base
      mode = "code"; section = ""; ident = ""; pending = ""; call = ""; depth = 0
    }
    {
      line = $0
      if (FNR == 1) sub(/^\357\273\277/, "", line)
      if (mode == "str") mode = "code"
      if (mode == "code" && call == "") {
        if (line ~ /^ +(public|private|protected|internal) .*[ ]Up\(/) section = "Up"
        else if (line ~ /^ +(public|private|protected|internal) .*[ ]Down\(/) section = "Down"
        else if (line ~ /^        (public|private|protected|internal) /) section = ""
      }
      n = length(line)
      for (i = 1; i <= n; i++) {
        c = substr(line, i, 1)
        if (mode == "block") { if (substr(line, i, 2) == "*/") { mode = "code"; i++ } continue }
        if (mode == "str") {
          if (c == "\\") i++
          else if (c == "\"") mode = "code"
          continue
        }
        if (mode == "vstr") {
          if (c == "\"") { if (substr(line, i + 1, 1) == "\"") i++; else mode = "code" }
          continue
        }
        if (mode == "raw") { if (substr(line, i, 3) == "\"\"\"") { mode = "code"; i += 2 } continue }
        # code
        if (c == "/" && substr(line, i + 1, 1) == "/") break
        if (c == "/" && substr(line, i + 1, 1) == "*") { mode = "block"; i++; continue }
        if (c == "\"") {
          if (call == "") boundary("\"")
          else ident = ""
          if (substr(line, i, 3) == "\"\"\"") { mode = "raw"; i += 2 }
          else {
            p1 = substr(line, i - 1, 1); p2 = substr(line, i - 2, 1)
            mode = (p1 == "@" || (p1 == "$" && p2 == "@")) ? "vstr" : "str"
          }
          if (call != "") calltext = calltext "\"\""
          continue
        }
        if (c == "\047") {
          if (call == "") boundary("\047")
          else ident = ""
          k = i + 1
          if (substr(line, k, 1) == "\\") k += 2
          while (k <= n && substr(line, k, 1) != "\047") k++
          i = k
          if (call != "") calltext = calltext "\047\047"
          continue
        }
        if (c ~ /[A-Za-z0-9_]/) { ident = ident c; if (call != "") calltext = calltext c; continue }
        if (call == "") boundary(c)
        else {
          ident = ""
          calltext = calltext c
          if (c == "(") depth++
          else if (c == ")" && --depth == 0) endcall()
        }
      }
      if (mode == "code") { if (call == "") boundary(" "); else { ident = ""; calltext = calltext " " } }
    }
  ' "${migrations[@]}")
}

mapfile -t needed < <(derive_needed)
if [[ ${#needed[@]} -eq 0 ]]; then
  echo "derived no designer to keep, the derivation in this script is broken" >&2
  exit 1
fi

declare -A keep=()
for line in "${needed[@]}"; do keep["${line%%$'\t'*}"]=1; done

if [[ "${1:-}" == "--list" ]]; then
  printf '%s\n' "${needed[@]}" | sort -u
  exit 0
fi

mapfile -t designers < <(cd "$dir" && printf '%s\n' *.Designer.cs | sort)
total=${#designers[@]}
changed=0

for ((i = 0; i < total - keep_newest; i++)); do
  name="${designers[$i]}"
  [[ -n "${keep[$name]:-}" ]] && continue
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
