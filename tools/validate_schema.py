import sqlite3, os, shutil, tempfile, uuid
# Validates schema.sql (migration 001) against an in-temp SQLite database. Usage: python tools/validate_schema.py
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DDL = open(os.path.join(ROOT, "schema.sql"), encoding="utf-8").read()
ok = fail = 0
def check(name, cond):
    global ok, fail
    ok, fail = (ok + 1, fail) if cond else (ok, fail + 1)
    print(("PASS " if cond else "FAIL ") + name)

def connect(path):
    c = sqlite3.connect(path, isolation_level=None)
    for p in ("foreign_keys=ON", "busy_timeout=5000", "secure_delete=ON", "trusted_schema=OFF"):
        c.execute("PRAGMA " + p)
    return c

def migrate(c, ddl, version=1):
    c.execute("BEGIN IMMEDIATE")
    try:
        for stmt in split(ddl):  # not executescript: it commits implicitly
            c.execute(stmt)
        c.execute(f"PRAGMA user_version = {version}")
        c.execute("COMMIT")
    except Exception:
        c.execute("ROLLBACK")
        raise

def split(sql):
    buf, out = "", []
    for line in sql.splitlines(keepends=True):
        buf += line
        if sqlite3.complete_statement(buf):
            s = buf.strip()
            if s and not all(l.strip().startswith("--") or not l.strip() for l in s.splitlines()):
                out.append(s)
            buf = ""
    return out

def raises(name, fn, exc=sqlite3.DatabaseError):
    try:
        fn(); check(name, False)
    except exc:
        check(name, True)

tmp = tempfile.mkdtemp()
db = os.path.join(tmp, "cc.db")
c = connect(db)
check("journal_mode=WAL outside txn", c.execute("PRAGMA journal_mode=WAL").fetchone()[0] == "wal")
c.execute("PRAGMA synchronous=NORMAL")
migrate(c, DDL)
check("user_version=1", c.execute("PRAGMA user_version").fetchone()[0] == 1)
check("integrity_check ok", c.execute("PRAGMA integrity_check").fetchone()[0] == "ok")
c.execute("BEGIN")
try:
    changed = c.execute("PRAGMA journal_mode=DELETE").fetchone()[0] != "wal"
except sqlite3.OperationalError:
    changed = False
check("journal_mode cannot change inside a transaction", not changed)
c.execute("ROLLBACK")

# D3: every FK child key must lead some index (set-wise) to avoid cascade scans.
missing = []
for (t,) in c.execute("SELECT name FROM sqlite_schema WHERE type='table'").fetchall():
    fks = {}
    for row in c.execute(f"PRAGMA foreign_key_list('{t}')"):
        fks.setdefault(row[0], []).append(row[3])
    idx_cols = []
    for il in c.execute(f"PRAGMA index_list('{t}')").fetchall():
        idx_cols.append([r[2] for r in c.execute(f"PRAGMA index_info('{il[1]}')")])
    pk = [r[1] for r in sorted(c.execute(f"PRAGMA table_info('{t}')"), key=lambda r: r[5]) if r[5]]
    idx_cols.append(pk)
    for cols in fks.values():
        if not any(set(ic[:len(cols)]) == set(cols) for ic in idx_cols):
            if t != "Session" or cols != ["EndpointManifestHash"]:  # rare manifest GC; scan accepted
                missing.append((t, cols))
check(f"FK child indexes present {missing or ''}", not missing)

U = lambda: str(uuid.uuid4())
T0 = 1_780_000_000_000_000
ins = lambda sql, *a: c.execute(sql, a)
H = "a" * 64
ins("INSERT INTO EndpointManifest VALUES (?,?,?)", H, "2026.1", '{"targets":[]}')
def inv(created=T0):
    i = U(); ins("INSERT INTO Investigation(Id,CreatedUtc,UpdatedUtc,Symptom,AffectedDevice) VALUES (?,?,?,?,?)", i, created, created, "Gaming", "ThisPc"); return i
def ses(i, started=T0, state="Completed", reason="Deadline"):
    s = U(); ins("""INSERT INTO Session(Id,InvestigationId,StartedUtc,EndedUtc,EndedUs,PlannedDurationSeconds,State,EndReason,AppVersion,ProbeProfileJson,EndpointManifestHash)
      VALUES (?,?,?,?,?,?,?,?,?,?,?)""", s, i, started, started + 120_000_000, 120_000_000, 120, state, reason, "1.0.0", "{}", H); return s
def seg(s):
    g = U(); ins("INSERT INTO PathSegment(Id,SessionId,StartUs,Reason,ContextJson) VALUES (?,?,?,?,?)", g, s, 0, "SessionStart", "{}"); return g
seq = iter(range(10**6))
def obs(s, g, kind="Icmp", fam="IPv4", attr="RoutePredicted", status="Success", dur=12000, src="OsReported", err=None, started=0, ended=12000, detail="{}"):
    ins("""INSERT INTO ProbeObservation(SessionId,SegmentId,Sequence,TargetId,ProbeKind,RequestFamily,Attribution,ScheduledUs,StartedUs,EndedUs,Status,DurationUs,TimingSource,ErrorCode,DetailJson)
      VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""", s, g, next(seq), "ext-a", kind, fam, attr, 0, started, ended, status, dur, src, err, detail)

i1 = inv(); s1 = ses(i1); s2 = ses(i1, T0 + 10**9); g1 = seg(s1); g2 = seg(s2)
obs(s1, g1); obs(s1, g1, status="Timeout", dur=None, src=None, ended=800000)
obs(s1, g1, kind="Https", fam="Any", attr="Proxied", status="ProxyAuthRequired", dur=5000)
obs(s1, g1, status="Skipped", dur=None, src=None, err="ConcurrencyBudget", started=None, ended=None)
check("valid evidence graph accepted (json_valid under trusted_schema=OFF)", True)
ins("INSERT INTO ClockAnchor VALUES (?,?,?,?)", s1, 60_000_000, T0 + 61_000_000, "Resume")
ins("INSERT INTO ConnectionEvent(Id,SessionId,OffsetUs,Source,Kind) VALUES (?,?,?,?,?)", U(), s1, 5, "Wlan", "WlanRoamed")
ins("INSERT INTO CoverageGap VALUES (?,?,?,?,?)", U(), s1, 10, 20, "Sleep")
ins("INSERT INTO SessionCapability(SessionId,Capability,StreamKey,SinceUs,State) VALUES (?,?,?,?,?)", s1, "IcmpResponse", "ext-a|Icmp|IPv4", 0, "Available")
ins("INSERT INTO SymptomMarker(Id,SessionId,OffsetUs,Source) VALUES (?,?,?,?)", U(), s1, 30, "Hotkey")
r1 = U(); ins("INSERT INTO AnalysisRun VALUES (?,?,?,?)", r1, s1, "rules-1", T0)
ins("INSERT INTO Finding VALUES (?,?,?,?,?,?,?,?)", U(), r1, "R04", 0, 0, 10, "Observed", "{}")
ins("INSERT INTO AnalysisRun VALUES (?,?,?,?)", U(), s1, "rules-2", T0)
a1 = U(); ins("INSERT INTO ActionAttempt(Id,InvestigationId,SourceSessionId,ActionCode,State,CreatedUtc,UpdatedUtc) VALUES (?,?,?,?,?,?,?)", a1, i1, s1, "RetestOnEthernet", "Completed", T0, T0)
ins("INSERT INTO Comparison VALUES (?,?,?,?,?,?,?,?,?,?)", U(), i1, s1, s2, a1, 0, "rules-1", "Caution", T0, "{}")

i2 = inv(); s3 = ses(i2); g3 = seg(s3)
a2 = U(); ins("INSERT INTO ActionAttempt(Id,InvestigationId,ActionCode,State,CreatedUtc,UpdatedUtc) VALUES (?,?,?,?,?,?)", a2, i2, "X", "Suggested", T0, T0)
neg = [
  ("observation on another session's segment", lambda: obs(s1, g3)),
  ("comparison across investigations", lambda: ins("INSERT INTO Comparison VALUES (?,?,?,?,?,?,?,?,?,?)", U(), i1, s1, s3, None, 0, "r", "Caution", T0, "{}")),
  ("comparison with foreign action", lambda: ins("INSERT INTO Comparison VALUES (?,?,?,?,?,?,?,?,?,?)", U(), i1, s1, s2, a2, 0, "r", "Caution", T0, "{}")),
  ("comparison same session", lambda: ins("INSERT INTO Comparison VALUES (?,?,?,?,?,?,?,?,?,?)", U(), i1, s1, s1, None, 0, "r", "Caution", T0, "{}")),
  ("success without duration", lambda: obs(s1, g1, dur=None, src=None)),
  ("timeout with duration", lambda: obs(s1, g1, status="Timeout")),
  ("skipped without reason", lambda: obs(s1, g1, status="Skipped", dur=None, src=None, started=None, ended=None)),
  ("https with fixed family", lambda: obs(s1, g1, kind="Https", attr="SocketObserved")),
  ("icmp with Any family", lambda: obs(s1, g1, fam="Any")),
  ("icmp socket-observed attribution", lambda: obs(s1, g1, attr="SocketObserved")),
  ("tls failure on icmp", lambda: obs(s1, g1, status="TlsFailure")),
  ("duration without timing source", lambda: obs(s1, g1, src=None)),
  ("invalid detail json", lambda: obs(s1, g1, detail="{bad")),
  ("STRICT type: text in integer column", lambda: obs(s1, g1, dur="fast")),
  ("observation update blocked", lambda: ins("UPDATE ProbeObservation SET Status='Timeout'")),
  ("completed without end reason", lambda: ins("UPDATE Session SET EndReason=NULL WHERE Id=?", s1)),
  ("failed with Deadline reason", lambda: ins("UPDATE Session SET State='Failed' WHERE Id=?", s1)),
  ("active session with end reason", lambda: ses(i1, state="Capturing", reason="Deadline")),
  ("notes over 2000 chars", lambda: ins("UPDATE Investigation SET Notes=? WHERE Id=?", "x" * 2001, i1)),
  ("clock anchor at offset 0", lambda: ins("INSERT INTO ClockAnchor VALUES (?,?,?,?)", s1, 0, T0, "Resume")),
  ("icmp capability without stream key", lambda: ins("INSERT INTO SessionCapability(SessionId,Capability,SinceUs,State) VALUES (?,?,?,?)", s1, "IcmpResponse", 0, "Unknown")),
  ("wlan event from non-wlan source", lambda: ins("INSERT INTO ConnectionEvent(Id,SessionId,OffsetUs,Source,Kind) VALUES (?,?,?,?,?)", U(), s1, 5, "IpHelper", "WlanRoamed")),
  ("duplicate primary finding", lambda: ins("INSERT INTO Finding VALUES (?,?,?,?,?,?,?,?)", U(), r1, "R10", 0, 0, 1, "Observed", "{}")),
  ("delete session still referenced by action", lambda: ins("DELETE FROM Session WHERE Id=?", s1)),
]
for n, f in neg:
    raises("reject: " + n, f)

# Cascade + retention: old investigation removed completely; NO ACTION refs resolve within one statement.
cutoff = T0 + 5 * 10**9
old = [r[0] for r in c.execute("SELECT InvestigationId FROM InvestigationActivity WHERE LastActivityUtc < ?", (cutoff,))]
check("retention selects both old investigations", set(old) == {i1, i2})
c.execute("BEGIN")
c.execute("DELETE FROM Investigation WHERE Id IN (SELECT InvestigationId FROM InvestigationActivity WHERE LastActivityUtc < ?)", (cutoff,))
c.execute("DELETE FROM EndpointManifest WHERE Hash NOT IN (SELECT EndpointManifestHash FROM Session)")
c.execute("COMMIT")
tables = [r[0] for r in c.execute("SELECT name FROM sqlite_schema WHERE type='table'")]
left = {t: c.execute(f"SELECT count(*) FROM {t}").fetchone()[0] for t in tables}
check(f"cascade removed all rows {dict((k, v) for k, v in left.items() if v)}", not any(left.values()))
check("foreign_key_check clean", not c.execute("PRAGMA foreign_key_check").fetchall())
c.execute("PRAGMA wal_checkpoint(TRUNCATE)"); c.execute("VACUUM")
check("post-delete checkpoint+vacuum ok", c.execute("PRAGMA integrity_check").fetchone()[0] == "ok")
c.close(); shutil.rmtree(tmp, ignore_errors=True)
print(f"\n{ok} passed, {fail} failed (SQLite {sqlite3.sqlite_version})")
raise SystemExit(1 if fail else 0)
