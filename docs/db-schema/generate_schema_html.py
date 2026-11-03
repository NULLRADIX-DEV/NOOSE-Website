#!/usr/bin/env python3
"""Generate DatenbankStruktur.html from the EF Core model snapshot.

Parses NOOSE-Website/Data/Migrations/AppDbContextModelSnapshot.cs and emits a
self-contained HTML file (vis-network from CDN) visualizing the DB schema.

Usage:  python docs/db-schema/generate_schema_html.py
Output: DatenbankStruktur.html in docs/.
"""

import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SNAPSHOT = REPO_ROOT / "NOOSE-Website" / "Data" / "Migrations" / "AppDbContextModelSnapshot.cs"
OUTPUT = REPO_ROOT / "docs" / "DatenbankStruktur.html"


def extract_entity_blocks(text):
    """Yield (entity_full_name, block_body) for each modelBuilder.Entity(...) call."""
    pattern = re.compile(r'modelBuilder\.Entity\("([^"]+)",\s*b\s*=>\s*\{')
    for m in pattern.finditer(text):
        depth = 1
        i = m.end()
        while depth > 0:
            c = text[i]
            if c == "{":
                depth += 1
            elif c == "}":
                depth -= 1
            i += 1
        yield m.group(1), text[m.end():i - 1]


def split_statements(block):
    """Split a block body into fluent statements terminated by ';'."""
    return [s.strip() for s in block.split(";") if s.strip()]


def parse_property(stmt):
    """Parse one b.Property<...>("...")... statement into a column dict."""
    m = re.match(r'b\.Property<(.+?)>\("([^"]+)"\)', stmt, re.S)
    if not m:
        return None
    col = {"prop": m.group(2), "csType": m.group(1)}
    cm = re.search(r'\.HasColumnName\("([^"]+)"\)', stmt)
    col["name"] = cm.group(1) if cm else col["prop"]
    col["required"] = ".IsRequired()" in stmt
    col["autoInc"] = ".ValueGeneratedOnAdd()" in stmt
    lm = re.search(r"\.HasMaxLength\((\d+)\)", stmt)
    col["maxLength"] = int(lm.group(1)) if lm else None
    tm = re.search(r'\.HasColumnType\("([^"]+)"\)', stmt)
    col["columnType"] = tm.group(1) if tm else None
    return col


def parse_relationship(stmt, child_entity):
    """Parse one b.HasOne(...).WithMany/WithOne(...).HasForeignKey(...) statement."""
    hm = re.match(r'b\.HasOne\("([^"]+)"', stmt)
    fm = re.search(r"\.HasForeignKey\(([^)]*)\)", stmt)
    if not hm or not fm:
        return None
    fk_args = re.findall(r'"([^"]+)"', fm.group(1))
    # HasForeignKey may be (dependentTypeName, prop) — drop full type names
    fk_props = [a for a in fk_args if "." not in a]
    dm = re.search(r"\.OnDelete\(DeleteBehavior\.(\w+)\)", stmt)
    return {
        "childEntity": child_entity,
        "parentEntity": hm.group(1),
        "fkProps": fk_props,
        "deleteBehavior": dm.group(1) if dm else None,
    }


def short_name(full):
    name = full.split(".")[-1]
    # strip generic arity like IdentityRoleClaim<string>
    return re.sub(r"<.*>$", "", name)


def domain_of(full):
    if full.startswith("Microsoft.AspNetCore.Identity"):
        return "Identity"
    parts = full.split(".")
    # NOOSE_Website.Data.Entities[.<Domain>].<Name>
    return parts[3] if len(parts) > 4 else "Basis"


def main():
    text = SNAPSHOT.read_text(encoding="utf-8")
    entities = {}   # full name -> dict
    relationships = []

    for full_name, block in extract_entity_blocks(text):
        ent = entities.setdefault(full_name, {
            "entity": full_name,
            "short": short_name(full_name),
            "domain": domain_of(full_name),
            "table": None,
            "columns": [],
            "pk": [],
        })
        for stmt in split_statements(block):
            if stmt.startswith("b.Property<"):
                col = parse_property(stmt)
                if col:
                    ent["columns"].append(col)
            elif stmt.startswith("b.HasKey("):
                ent["pk"] = re.findall(r'"([^"]+)"', stmt)
            elif stmt.startswith("b.ToTable("):
                tm = re.search(r'\.ToTable\("([^"]+)"', stmt)
                if tm:
                    ent["table"] = tm.group(1)
            elif stmt.startswith("b.HasOne("):
                rel = parse_relationship(stmt, full_name)
                if rel:
                    relationships.append(rel)

    # table names: explicit ToTable, else entity short name
    for ent in entities.values():
        if not ent["table"]:
            ent["table"] = ent["short"]

    # entities without any columns are relationship-only stub blocks -> drop
    entities = {k: v for k, v in entities.items() if v["columns"] or v["table"]}

    # mark PK / FK on columns
    for ent in entities.values():
        for col in ent["columns"]:
            col["pk"] = col["prop"] in ent["pk"]
            col["fk"] = False

    entity_to_table = {k: v["table"] for k, v in entities.items()}
    rels_out = []
    for rel in relationships:
        child = entity_to_table.get(rel["childEntity"])
        parent = entity_to_table.get(rel["parentEntity"])
        if not child or not parent:
            continue
        rels_out.append({
            "from": child,
            "to": parent,
            "fk": ", ".join(rel["fkProps"]),
            "onDelete": rel["deleteBehavior"],
        })
        child_ent = entities[rel["childEntity"]]
        for col in child_ent["columns"]:
            if col["prop"] in rel["fkProps"]:
                col["fk"] = True

    tables = []
    for ent in sorted(entities.values(), key=lambda e: e["table"].lower()):
        tables.append({
            "name": ent["table"],
            "entity": ent["short"],
            "domain": ent["domain"],
            "columns": ent["columns"],
            "columnCount": len(ent["columns"]),
        })

    total_columns = sum(t["columnCount"] for t in tables)
    domains = {}
    for t in tables:
        d = domains.setdefault(t["domain"], {"tables": 0, "columns": 0})
        d["tables"] += 1
        d["columns"] += t["columnCount"]

    stats = {
        "tableCount": len(tables),
        "columnCount": total_columns,
        "relationshipCount": len(rels_out),
        "avgColumns": round(total_columns / len(tables), 1) if tables else 0,
        "maxColumns": max((t["columnCount"] for t in tables), default=0),
        "minColumns": min((t["columnCount"] for t in tables), default=0),
        "largestTable": max(tables, key=lambda t: t["columnCount"])["name"] if tables else "",
        "domains": domains,
    }

    schema = {"tables": tables, "relationships": rels_out, "stats": stats}
    html = HTML_TEMPLATE.replace("__SCHEMA_JSON__", json.dumps(schema, ensure_ascii=False))
    OUTPUT.write_text(html, encoding="utf-8")

    print(f"Tabellen:      {stats['tableCount']}")
    print(f"Spalten:       {stats['columnCount']}")
    print(f"Beziehungen:   {stats['relationshipCount']}")
    print(f"Ø Spalten:     {stats['avgColumns']} (min {stats['minColumns']}, max {stats['maxColumns']})")
    print(f"Größte Tabelle: {stats['largestTable']}")
    print(f"HTML geschrieben: {OUTPUT}")
    return stats


HTML_TEMPLATE = r"""<!DOCTYPE html>
<html lang="de">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>FIB – Datenbankstruktur</title>
<script src="https://unpkg.com/vis-network/standalone/umd/vis-network.min.js"></script>
<style>
:root {
  --bg: #1c1c1c;
  --bg2: #242424;
  --bg3: #2c2c2c;
  --fg: #ece8e1;
  --muted: #a8a29a;
  --bronze: #b5a488;
  --bronze-dim: rgba(181,164,136,.25);
  --border: #333333;
}
* { box-sizing: border-box; }
body {
  margin: 0; background: var(--bg); color: var(--fg);
  font-family: "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
}
header {
  padding: 20px 28px 12px; border-bottom: 1px solid var(--border);
  background: var(--bg2); position: sticky; top: 0; z-index: 10;
}
h1 { margin: 0 0 4px; font-size: 22px; font-weight: 600; }
h1 .accent { color: var(--bronze); }
.subtitle { color: var(--muted); font-size: 13px; }
nav.tabs { margin-top: 12px; display: flex; gap: 8px; flex-wrap: wrap; }
nav.tabs button {
  background: var(--bg3); color: var(--fg); border: 1px solid var(--border);
  border-radius: 6px; padding: 6px 14px; cursor: pointer; font-size: 13px;
}
nav.tabs button.active { background: var(--bronze-dim); border-color: var(--bronze); color: var(--bronze); }
main { padding: 20px 28px 40px; }
section.view { display: none; }
section.view.active { display: block; }
.cards { display: flex; flex-wrap: wrap; gap: 14px; margin-bottom: 22px; }
.card {
  background: var(--bg2); border: 1px solid var(--border); border-radius: 10px;
  padding: 14px 20px; min-width: 150px;
}
.card .value { font-size: 26px; font-weight: 700; color: var(--bronze); }
.card .label { font-size: 12px; color: var(--muted); margin-top: 2px; }
.panel {
  background: var(--bg2); border: 1px solid var(--border);
  border-radius: 10px; padding: 16px 20px; margin-bottom: 22px;
}
.panel h2 { margin: 0 0 12px; font-size: 16px; font-weight: 600; }
.bar-row { display: grid; grid-template-columns: 180px 1fr 90px; align-items: center; gap: 10px; margin: 5px 0; font-size: 13px; }
.bar-row .name { color: var(--fg); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.bar-row .count { color: var(--muted); text-align: right; }
.bar { height: 14px; border-radius: 4px; background: linear-gradient(90deg, var(--bronze), #71614a); min-width: 2px; }
input[type="search"] {
  background: var(--bg3); border: 1px solid var(--border); border-radius: 6px;
  color: var(--fg); padding: 7px 12px; font-size: 13px; width: 260px;
}
input[type="search"]:focus { outline: none; border-color: var(--bronze); }
table.data { border-collapse: collapse; width: 100%; font-size: 13px; }
table.data th, table.data td { padding: 7px 10px; border-bottom: 1px solid var(--border); text-align: left; }
table.data th { color: var(--bronze); cursor: pointer; user-select: none; white-space: nowrap; position: sticky; top: 0; background: var(--bg2); }
table.data tr:hover td { background: rgba(181,164,136,.06); }
.badge { display: inline-block; font-size: 10px; font-weight: 700; border-radius: 4px; padding: 1px 5px; margin-right: 4px; }
.badge.pk { background: rgba(181,164,136,.2); color: var(--bronze); border: 1px solid var(--bronze); }
.badge.fk { background: rgba(255,183,77,.15); color: #ffb74d; border: 1px solid #ffb74d; }
.mono { font-family: Consolas, "Courier New", monospace; }
.muted { color: var(--muted); }
#graph-wrap { display: grid; grid-template-columns: 1fr 340px; gap: 14px; }
@media (max-width: 1100px) { #graph-wrap { grid-template-columns: 1fr; } }
#graph { height: 72vh; background: var(--bg2); border: 1px solid var(--border); border-radius: 10px; }
#detail {
  background: var(--bg2); border: 1px solid var(--border); border-radius: 10px;
  padding: 14px 16px; height: 72vh; overflow-y: auto; font-size: 13px;
}
#detail h3 { margin: 0 0 4px; font-size: 15px; color: var(--bronze); }
#detail table { border-collapse: collapse; width: 100%; font-size: 12px; margin-top: 10px; }
#detail td { padding: 4px 6px; border-bottom: 1px solid var(--border); vertical-align: top; }
.graph-toolbar { display: flex; gap: 10px; align-items: center; margin-bottom: 10px; flex-wrap: wrap; }
.graph-toolbar label { font-size: 13px; color: var(--muted); display: flex; gap: 5px; align-items: center; }
.legend { display: flex; flex-wrap: wrap; gap: 8px 16px; margin-top: 10px; font-size: 12px; color: var(--muted); }
.legend span { display: inline-flex; align-items: center; gap: 6px; }
.dot { width: 10px; height: 10px; border-radius: 50%; display: inline-block; }
</style>
</head>
<body>
<header>
  <h1>FIB <span class="accent">Datenbankstruktur</span></h1>
  <div class="subtitle">Generiert aus <span class="mono">AppDbContextModelSnapshot.cs</span> (EF Core 9)</div>
  <nav class="tabs">
    <button data-view="graph" class="active">ER-Diagramm</button>
    <button data-view="stats">Statistik</button>
    <button data-view="tables">Tabellenübersicht</button>
  </nav>
</header>
<main>
  <section id="view-graph" class="view active">
    <div class="graph-toolbar">
      <input type="search" id="graph-search" placeholder="Tabelle suchen…">
      <label><input type="checkbox" id="toggle-columns"> Spalten im Graph anzeigen</label>
      <label><input type="checkbox" id="toggle-hierarchy"> Hierarchisches Layout</label>
      <label><input type="checkbox" id="toggle-labels" checked> FK-Beschriftungen</label>
    </div>
    <div id="graph-wrap">
      <div id="graph"></div>
      <div id="detail"><h3>Detailansicht</h3><div class="muted">Tabelle im Diagramm anklicken, um Spalten und Beziehungen anzuzeigen.</div></div>
    </div>
    <div class="legend" id="legend"></div>
  </section>

  <section id="view-stats" class="view">
    <div class="cards" id="stat-cards"></div>
    <div class="panel">
      <h2>Tabellen pro Domäne</h2>
      <div id="domain-bars"></div>
    </div>
    <div class="panel">
      <h2>Größte Tabellen (nach Spaltenanzahl)</h2>
      <div id="top-bars"></div>
    </div>
  </section>

  <section id="view-tables" class="view">
    <div class="panel">
      <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:12px;flex-wrap:wrap;gap:10px;">
        <h2 style="margin:0">Alle Tabellen</h2>
        <input type="search" id="table-search" placeholder="Filtern…">
      </div>
      <table class="data" id="tables-table">
        <thead><tr>
          <th data-key="name">Tabelle ⇅</th>
          <th data-key="entity">Entität ⇅</th>
          <th data-key="domain">Domäne ⇅</th>
          <th data-key="columnCount">Spalten ⇅</th>
          <th data-key="fkCount">FKs ⇅</th>
        </tr></thead>
        <tbody></tbody>
      </table>
    </div>
  </section>
</main>

<script>
const SCHEMA = __SCHEMA_JSON__;

// ---------- helpers ----------
const $ = (sel) => document.querySelector(sel);
const esc = (s) => String(s).replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));

// domain colours: bronze first, then distinguishable hues
const palette = ["#b5a488","#ffb74d","#81c784","#ba68c8","#e57373","#64b5f6","#fff176","#4db6ac","#f06292","#a1887f","#90a4ae","#dce775","#9575cd","#4dd0e1","#aed581","#ff8a65","#7986cb","#f48fb1","#a5d6a7","#ce93d8"];
const domains = [...new Set(SCHEMA.tables.map(t => t.domain))].sort();
const domainColor = {};
domains.forEach((d, i) => domainColor[d] = palette[i % palette.length]);

// ---------- tab switching ----------
document.querySelectorAll("nav.tabs button").forEach(btn => {
  btn.addEventListener("click", () => {
    document.querySelectorAll("nav.tabs button").forEach(b => b.classList.remove("active"));
    document.querySelectorAll("section.view").forEach(s => s.classList.remove("active"));
    btn.classList.add("active");
    $("#view-" + btn.dataset.view).classList.add("active");
    if (btn.dataset.view === "graph") network && network.redraw();
  });
});

// ---------- stats view ----------
const s = SCHEMA.stats;
$("#stat-cards").innerHTML = [
  [s.tableCount, "Tabellen"],
  [s.columnCount, "Spalten gesamt"],
  [s.relationshipCount, "Beziehungen (FK)"],
  [s.avgColumns, "Ø Spalten / Tabelle"],
  [s.maxColumns, "Max. Spalten (" + esc(s.largestTable) + ")"],
  [s.minColumns, "Min. Spalten"],
].map(([v, l]) => `<div class="card"><div class="value">${v}</div><div class="label">${l}</div></div>`).join("");

function renderBars(el, rows, maxVal, labelFn, valFn) {
  el.innerHTML = rows.map(r => `
    <div class="bar-row">
      <div class="name">${esc(labelFn(r))}</div>
      <div class="bar" style="width:${Math.max(1, valFn(r) / maxVal * 100)}%"></div>
      <div class="count">${valFn(r)}</div>
    </div>`).join("");
}
const domainRows = Object.entries(s.domains).map(([name, d]) => ({name, ...d}))
  .sort((a, b) => b.tables - a.tables);
renderBars($("#domain-bars"), domainRows, Math.max(...domainRows.map(r => r.tables)),
  r => `${r.name} (${r.columns} Spalten)`, r => r.tables);
const topTables = [...SCHEMA.tables].sort((a, b) => b.columnCount - a.columnCount).slice(0, 15);
renderBars($("#top-bars"), topTables, topTables[0].columnCount, r => r.name, r => r.columnCount);

// ---------- tables view ----------
const fkCountByTable = {};
SCHEMA.relationships.forEach(r => fkCountByTable[r.from] = (fkCountByTable[r.from] || 0) + 1);
let sortKey = "name", sortAsc = true, filterText = "";

function renderTableList() {
  const rows = SCHEMA.tables
    .filter(t => !filterText || t.name.toLowerCase().includes(filterText) || t.entity.toLowerCase().includes(filterText))
    .sort((a, b) => {
      const va = a[sortKey] ?? 0, vb = b[sortKey] ?? 0;
      const cmp = typeof va === "number" ? va - vb : String(va).localeCompare(String(vb));
      return sortAsc ? cmp : -cmp;
    });
  $("#tables-table tbody").innerHTML = rows.map(t => `<tr data-table="${esc(t.name)}" style="cursor:pointer">
    <td class="mono">${esc(t.name)}</td><td class="mono muted">${esc(t.entity)}</td>
    <td><span class="dot" style="background:${domainColor[t.domain]}"></span> ${esc(t.domain)}</td>
    <td>${t.columnCount}</td><td>${fkCountByTable[t.name] || 0}</td></tr>`).join("");
  document.querySelectorAll("#tables-table tbody tr").forEach(tr =>
    tr.addEventListener("click", () => {
      document.querySelector('nav.tabs button[data-view="graph"]').click();
      focusTable(tr.dataset.table);
    }));
}
document.querySelectorAll("#tables-table th").forEach(th => th.addEventListener("click", () => {
  const key = th.dataset.key;
  if (sortKey === key) sortAsc = !sortAsc; else { sortKey = key; sortAsc = true; }
  renderTableList();
}));
$("#table-search").addEventListener("input", e => { filterText = e.target.value.toLowerCase(); renderTableList(); });
renderTableList();

// ---------- graph view ----------
const showCols = () => $("#toggle-columns").checked;
const nodeLabel = (t) => {
  if (!showCols()) return t.name;
  const cols = t.columns.map(c => (c.pk ? "🔑 " : c.fk ? "🔗 " : "") + c.name).slice(0, 20);
  const more = t.columns.length > 20 ? [`… +${t.columns.length - 20}`] : [];
  return t.name + "\n" + "─".repeat(12) + "\n" + [...cols, ...more].join("\n");
};
const nodes = new vis.DataSet(SCHEMA.tables.map(t => ({
  id: t.name,
  label: nodeLabel(t),
  title: `${t.entity} · ${t.domain} · ${t.columnCount} Spalten`,
  color: { background: "#242424", border: domainColor[t.domain],
           highlight: { background: "#2c2c2c", border: "#b5a488" } },
  font: { color: "#ece8e1", face: "Consolas, monospace", size: showCols() ? 11 : 14, align: "left" },
  shape: "box",
  margin: 8,
  borderWidth: 2,
})));
const edgeLabel = () => $("#toggle-labels").checked;
const edges = new vis.DataSet(SCHEMA.relationships.map((r, i) => ({
  id: i, from: r.from, to: r.to,
  label: edgeLabel() ? r.fk : undefined,
  font: { color: "#a8a29a", size: 10, strokeWidth: 0 },
  color: { color: "rgba(181,164,136,.35)", highlight: "#b5a488" },
  arrows: { to: { enabled: true, scaleFactor: 0.6 } },
  smooth: { type: "continuous" },
})));

const network = new vis.Network($("#graph"), { nodes, edges }, {
  physics: {
    solver: "forceAtlas2Based",
    forceAtlas2Based: { gravitationalConstant: -120, springLength: 140, springConstant: 0.05, avoidOverlap: 0.6 },
    stabilization: { iterations: 300 },
  },
  interaction: { hover: true, tooltipDelay: 120 },
  layout: { improvedLayout: true },
});

function graphOptions() {
  const hier = $("#toggle-hierarchy").checked;
  network.setOptions(hier ? {
    layout: { hierarchical: { enabled: true, direction: "UD", sortMethod: "directed", levelSeparation: 180, nodeSpacing: 160 } },
    physics: { enabled: false },
  } : {
    layout: { hierarchical: { enabled: false } },
    physics: { enabled: true, solver: "forceAtlas2Based",
      forceAtlas2Based: { gravitationalConstant: -120, springLength: 140, springConstant: 0.05, avoidOverlap: 0.6 } },
  });
}
$("#toggle-hierarchy").addEventListener("change", graphOptions);
$("#toggle-labels").addEventListener("change", () =>
  edges.update(SCHEMA.relationships.map((r, i) => ({ id: i, label: edgeLabel() ? r.fk : undefined }))));
$("#toggle-columns").addEventListener("change", () =>
  nodes.update(SCHEMA.tables.map(t => ({ id: t.name, label: nodeLabel(t),
    font: { color: "#ece8e1", face: "Consolas, monospace", size: showCols() ? 11 : 14, align: "left" } }))));

// search: select + focus first match
$("#graph-search").addEventListener("input", (e) => {
  const q = e.target.value.toLowerCase();
  if (!q) { network.unselectAll(); return; }
  const hit = SCHEMA.tables.find(t => t.name.toLowerCase().includes(q));
  if (hit) focusTable(hit.name, false);
});

function neighborIds(table) {
  const ids = new Set([table]);
  SCHEMA.relationships.forEach(r => {
    if (r.from === table) ids.add(r.to);
    if (r.to === table) ids.add(r.from);
  });
  return [...ids];
}

function highlight(table) {
  const keep = new Set(neighborIds(table));
  nodes.update(SCHEMA.tables.map(t => ({
    id: t.name,
    opacity: keep.has(t.name) ? 1 : 0.15,
    font: { color: keep.has(t.name) ? "#ece8e1" : "rgba(236,232,225,.15)",
            face: "Consolas, monospace", size: showCols() ? 11 : 14, align: "left" },
  })));
  edges.update(SCHEMA.relationships.map((r, i) => ({
    id: i,
    color: { color: (r.from === table || r.to === table) ? "#b5a488" : "rgba(181,164,136,.06)" },
    font: { color: "#a8a29a", size: 10, strokeWidth: 0,
            background: (r.from === table || r.to === table) ? "#1c1c1c" : "none" },
    hidden: !(r.from === table || r.to === table) ? false : false,
  })));
}

function showDetail(table) {
  const t = SCHEMA.tables.find(x => x.name === table);
  if (!t) return;
  const rels = SCHEMA.relationships.filter(r => r.from === table || r.to === table);
  $("#detail").innerHTML = `
    <h3 class="mono">${esc(t.name)}</h3>
    <div class="muted">${esc(t.entity)} · Domäne: ${esc(t.domain)} · ${t.columnCount} Spalten</div>
    <table>
      ${t.columns.map(c => `<tr>
        <td>${c.pk ? '<span class="badge pk">PK</span>' : ""}${c.fk ? '<span class="badge fk">FK</span>' : ""}<span class="mono">${esc(c.name)}</span></td>
        <td class="muted mono">${esc(c.columnType || c.csType)}${c.maxLength ? ` (${c.maxLength})` : ""}${c.required ? " · not null" : ""}</td>
      </tr>`).join("")}
    </table>
    ${rels.length ? `<h3 style="margin-top:14px">Beziehungen</h3><table>` +
      rels.map(r => `<tr><td class="mono">${r.from === table
        ? `${esc(r.from)} → ${esc(r.to)}`
        : `${esc(r.from)} → ${esc(r.to)}`}</td>
        <td class="muted mono">${esc(r.fk)}${r.onDelete ? " · " + esc(r.onDelete) : ""}</td></tr>`).join("") +
      `</table>` : ""}`;
}

function focusTable(table, show = true) {
  network.selectNodes([table]);
  network.focus(table, { scale: 1.0, animation: { duration: 400, easingFunction: "easeInOutQuad" } });
  highlight(table);
  if (show) showDetail(table);
}

network.on("click", (params) => {
  if (params.nodes.length) {
    highlight(params.nodes[0]);
    showDetail(params.nodes[0]);
  } else {
    nodes.update(SCHEMA.tables.map(t => ({ id: t.name, opacity: 1,
      font: { color: "#ece8e1", face: "Consolas, monospace", size: showCols() ? 11 : 14, align: "left" } })));
    edges.update(SCHEMA.relationships.map((r, i) => ({ id: i, color: { color: "rgba(181,164,136,.35)" } })));
  }
});

// legend
$("#legend").innerHTML = domains.map(d =>
  `<span><span class="dot" style="background:${domainColor[d]}"></span>${esc(d)} (${s.domains[d].tables})</span>`).join("");
</script>
</body>
</html>
"""

if __name__ == "__main__":
    sys.exit(0 if main() else 1)
