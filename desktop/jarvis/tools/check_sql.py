#!/usr/bin/env python3
"""Best-effort SQL check: applies the schema from Database.cs to an in-memory SQLite database and asks
SQLite to prepare every SQL string literal found in the source tree. Catches misspelt tables/columns and syntax errors.
It does not prove the C# code binds the right parameter values. Exit code 1 when a statement fails to prepare."""
import pathlib, re, sqlite3, sys

root = pathlib.Path(__file__).resolve().parent.parent
src = root / "src"
db_cs = (src / "Jarvis.Infrastructure/Data/Database.cs").read_text(encoding="utf-8")
m = re.search(r'public const string V1 = @"(.*?)";', db_cs, re.S)
if not m:
    print("Schema.V1 not found"); sys.exit(1)
schema = m.group(1).replace('""', '"')

con = sqlite3.connect(":memory:")
try:
    con.executescript(schema)
except sqlite3.Error as ex:
    print("SCHEMA ERROR:", ex); sys.exit(1)
tables = [r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%'")]

LIT = re.compile(r'\$?@"((?:[^"]|"")*)"|\$?"((?:[^"\\]|\\.)*)"')
START = re.compile(r'^\s*(SELECT|INSERT|UPDATE|DELETE|WITH)\b', re.I)
checked = failed = 0
CLASS = re.compile(r'^(?:public|internal)\s+(?:sealed\s+|static\s+)*class\s+\w+', re.M)
CONST = re.compile(r'const string (\w+)\s*=\s*"((?:[^"\\]|\\.)*)"')
for path in sorted((src / "Jarvis.Infrastructure").rglob("*.cs")):
    text = path.read_text(encoding="utf-8")
    starts = [c.start() for c in CLASS.finditer(text)] + [len(text)]
    def consts_at(pos):
        # constants of the class that encloses this position
        for a, b in zip(starts, starts[1:]):
            if a <= pos < b:
                return dict(CONST.findall(text[a:b]))
        return {}
    for lm in LIT.finditer(text):
        verbatim = lm.group(1) is not None
        sql = lm.group(1).replace('""', '"') if verbatim else lm.group(2).encode().decode("unicode_escape")
        if not START.match(sql):
            continue
        if text[lm.start()] == "$":                       # interpolated: substitute the class's string constants
            consts = consts_at(lm.start())
            sql = re.sub(r"\{(\w+)\}", lambda k: consts.get(k.group(1), "1"), sql)
            # conditional fragments such as {(flag ? "WHERE x=1" : "")}: check both branches
            cond = re.search(r'\{\((\w+) \? \\?"(.*?)\\?" : \\?"(.*?)\\?"\)\}', sql)
            if cond:
                variants = [sql.replace(cond.group(0), cond.group(2)), sql.replace(cond.group(0), cond.group(3))]
            else:
                variants = [sql]
        else:
            variants = [sql]
        line = text.count("\n", 0, lm.start()) + 1
        for sql in variants:
            names = set(re.findall(r"\$\w+", sql))
            checked += 1
            try:
                con.execute("EXPLAIN " + sql, {n.lstrip("$"): "1" for n in names})
            except sqlite3.Error as ex:
                failed += 1
                print(f"{path.relative_to(root)}:{line}: {ex}\n    {' '.join(sql.split())[:160]}")
print(f"schema ok ({len(tables)} tables: {', '.join(sorted(tables))})")
print(f"{checked} SQL statements prepared, {failed} failed")
sys.exit(1 if failed else 0)
