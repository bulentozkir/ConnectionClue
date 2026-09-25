-- ConnectionClue migration 001 (schema v1). DDL only.
-- MigrationRunner: BEGIN IMMEDIATE; <this file>; PRAGMA user_version = 1; COMMIT. Refuse user_version > known.
-- Connection factory, every connection: foreign_keys=ON; busy_timeout=5000; secure_delete=ON; trusted_schema=OFF.
-- Once per database, outside any transaction: journal_mode=WAL; synchronous=NORMAL.
-- Not encrypted by design; protected by per-user OS file permissions.
-- Conventions: Id = UUID text; *Utc = INTEGER Unix epoch µs; *Us = INTEGER µs offset from Session.StartedUtc
-- on the monotonic clock; *Json = typed, versioned app models (validated by app; json_valid is a backstop).

CREATE TABLE Investigation (
  Id TEXT PRIMARY KEY NOT NULL,
  CreatedUtc INTEGER NOT NULL CHECK (CreatedUtc > 0),
  UpdatedUtc INTEGER NOT NULL CHECK (UpdatedUtc >= CreatedUtc),
  Symptom TEXT NOT NULL CHECK (Symptom IN ('Gaming','Video','Calls','Disconnects')),
  AffectedDevice TEXT NOT NULL CHECK (AffectedDevice IN ('ThisPc','OtherDevice')),
  ConstraintsJson TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(ConstraintsJson)), -- e.g. VpnRequired, NoCable
  Notes TEXT NOT NULL DEFAULT '' CHECK (length(Notes) <= 2000)
) STRICT;

-- Content-addressed snapshot of the shipped endpoint manifest (SHA-256 of canonical JSON).
CREATE TABLE EndpointManifest (
  Hash TEXT PRIMARY KEY NOT NULL CHECK (length(Hash) = 64),
  ManifestVersion TEXT NOT NULL,
  Json TEXT NOT NULL CHECK (json_valid(Json))
) STRICT, WITHOUT ROWID;

CREATE TABLE Session (
  Id TEXT PRIMARY KEY NOT NULL,
  InvestigationId TEXT NOT NULL REFERENCES Investigation(Id) ON DELETE CASCADE,
  StartedUtc INTEGER NOT NULL CHECK (StartedUtc > 0),  -- UTC anchor for offset 0
  EndedUtc INTEGER CHECK (EndedUtc IS NULL OR EndedUtc > 0),
  EndedUs INTEGER CHECK (EndedUs IS NULL OR EndedUs >= 0),
  PlannedDurationSeconds INTEGER NOT NULL CHECK (PlannedDurationSeconds IN (120,900,1800,3600)),
  State TEXT NOT NULL CHECK (State IN ('Preparing','Capturing','Suspended','Stopping','Completed','Interrupted','Failed')),
  EndReason TEXT CHECK (EndReason IN ('Deadline','UserStopped','AppExit','SleptPastDeadline','StorageFailure','InternalError','ProcessLost')),
  AppVersion TEXT NOT NULL,
  ProbeProfileJson TEXT NOT NULL CHECK (json_valid(ProbeProfileJson)),
  EndpointManifestHash TEXT NOT NULL REFERENCES EndpointManifest(Hash),
  UNIQUE (Id, InvestigationId),
  CHECK ((State IN ('Completed','Interrupted','Failed')) = (EndReason IS NOT NULL AND EndedUtc IS NOT NULL AND EndedUs IS NOT NULL)),
  CHECK (State <> 'Completed' OR EndReason IN ('Deadline','UserStopped','AppExit','SleptPastDeadline')),
  CHECK (State <> 'Failed' OR EndReason IN ('StorageFailure','InternalError')),
  CHECK (State <> 'Interrupted' OR EndReason = 'ProcessLost')
) STRICT;
CREATE INDEX IX_Session_Investigation ON Session(InvestigationId, StartedUtc);

-- UTC re-anchors after resume or a detected wall-clock jump. Display UTC = latest anchor at/before offset + delta.
CREATE TABLE ClockAnchor (
  SessionId TEXT NOT NULL REFERENCES Session(Id) ON DELETE CASCADE,
  OffsetUs INTEGER NOT NULL CHECK (OffsetUs > 0),
  Utc INTEGER NOT NULL CHECK (Utc > 0),
  Reason TEXT NOT NULL CHECK (Reason IN ('Resume','WallClockChange')),
  PRIMARY KEY (SessionId, OffsetUs)
) STRICT, WITHOUT ROWID;

-- Interval of stable network context. Key: per-family (interface, next hop) + proxy mode.
CREATE TABLE PathSegment (
  Id TEXT PRIMARY KEY NOT NULL,
  SessionId TEXT NOT NULL REFERENCES Session(Id) ON DELETE CASCADE,
  StartUs INTEGER NOT NULL CHECK (StartUs >= 0),
  EndUs INTEGER CHECK (EndUs IS NULL OR EndUs >= StartUs),
  Reason TEXT NOT NULL CHECK (Reason IN ('SessionStart','RouteChanged','InterfaceChanged','ProxyChanged','Resume')),
  ContextJson TEXT NOT NULL CHECK (json_valid(ContextJson) AND length(ContextJson) <= 8192),
  UNIQUE (Id, SessionId)
) STRICT;
CREATE INDEX IX_PathSegment_Session ON PathSegment(SessionId, StartUs);

-- Raw evidence. Append-only (see trigger).
CREATE TABLE ProbeObservation (
  Id INTEGER PRIMARY KEY,
  SessionId TEXT NOT NULL REFERENCES Session(Id) ON DELETE CASCADE,
  SegmentId TEXT NOT NULL,
  Sequence INTEGER NOT NULL CHECK (Sequence >= 0),
  TargetId TEXT NOT NULL,
  ProbeKind TEXT NOT NULL CHECK (ProbeKind IN ('Icmp','Tcp','SystemDns','Https')),
  RequestFamily TEXT NOT NULL CHECK (RequestFamily IN ('IPv4','IPv6','Any')),
  ObservedFamily TEXT CHECK (ObservedFamily IN ('IPv4','IPv6')),
  Attribution TEXT NOT NULL CHECK (Attribution IN ('SocketObserved','RoutePredicted','Proxied','Unknown')),
  ScheduledUs INTEGER NOT NULL CHECK (ScheduledUs >= 0),
  StartedUs INTEGER CHECK (StartedUs IS NULL OR StartedUs >= ScheduledUs),
  EndedUs INTEGER,
  Status TEXT NOT NULL CHECK (Status IN ('Success','Timeout','NetworkUnreachable','HostUnreachable','Refused','DnsFailure',
    'TlsFailure','HttpUnexpected','ProxyAuthRequired','RateLimited','PermissionDenied','Unsupported','Cancelled','Skipped','InternalError')),
  DurationUs INTEGER CHECK (DurationUs IS NULL OR DurationUs >= 0),
  TimingSource TEXT CHECK (TimingSource IN ('OsReported','UserMode')),
  ErrorCode TEXT CHECK (length(ErrorCode) <= 64),
  DetailJson TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(DetailJson) AND length(DetailJson) <= 4096),
  UNIQUE (SessionId, Sequence),
  FOREIGN KEY (SegmentId, SessionId) REFERENCES PathSegment(Id, SessionId) ON DELETE CASCADE,
  CHECK (EndedUs IS NULL OR (StartedUs IS NOT NULL AND EndedUs >= StartedUs)),
  CHECK ((DurationUs IS NULL) = (TimingSource IS NULL)),
  CHECK (Status <> 'Success' OR (EndedUs IS NOT NULL AND DurationUs IS NOT NULL)),
  CHECK (Status NOT IN ('Timeout','Cancelled','Skipped','PermissionDenied','Unsupported','InternalError') OR DurationUs IS NULL),
  CHECK (Status <> 'Skipped' OR (StartedUs IS NULL AND ErrorCode IS NOT NULL)),
  CHECK ((ProbeKind = 'Https') = (RequestFamily = 'Any')),
  CHECK (RequestFamily = 'Any' OR ObservedFamily IS NULL OR ObservedFamily = RequestFamily),
  CHECK (Status NOT IN ('TlsFailure','HttpUnexpected','ProxyAuthRequired','RateLimited') OR ProbeKind = 'Https'),
  CHECK (Status <> 'DnsFailure' OR ProbeKind IN ('SystemDns','Https')),
  CHECK (Attribution <> 'Proxied' OR ProbeKind = 'Https'),
  CHECK (ProbeKind <> 'Icmp' OR Attribution IN ('RoutePredicted','Unknown'))
) STRICT;
-- Serves stream queries and both FK child lookups (SessionId; SegmentId+SessionId).
CREATE INDEX IX_Observation_Stream ON ProbeObservation(SessionId, SegmentId, TargetId, ProbeKind, RequestFamily, ScheduledUs);

CREATE TABLE ConnectionEvent (
  Id TEXT PRIMARY KEY NOT NULL,
  SessionId TEXT NOT NULL REFERENCES Session(Id) ON DELETE CASCADE,
  OffsetUs INTEGER NOT NULL CHECK (OffsetUs >= 0),
  Source TEXT NOT NULL CHECK (Source IN ('IpHelper','Wlan','Power','Clock','App')),
  Kind TEXT NOT NULL CHECK (Kind IN ('LinkUp','LinkDown','WlanConnected','WlanDisconnected','WlanRoamed',
    'RouteChanged','AddressChanged','ProxyChanged','Suspend','Resume','WallClockChanged')),
  DetailJson TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(DetailJson) AND length(DetailJson) <= 8192),
  CHECK ((Kind LIKE 'Wlan%') = (Source = 'Wlan')),
  CHECK (Kind <> 'WallClockChanged' OR Source = 'Clock')
) STRICT;
CREATE INDEX IX_ConnectionEvent_Session ON ConnectionEvent(SessionId, OffsetUs);

-- Unobserved time. Detected from scheduler heartbeat; Reason is best-effort attribution.
CREATE TABLE CoverageGap (
  Id TEXT PRIMARY KEY NOT NULL,
  SessionId TEXT NOT NULL REFERENCES Session(Id) ON DELETE CASCADE,
  StartUs INTEGER NOT NULL CHECK (StartUs >= 0),
  EndUs INTEGER NOT NULL CHECK (EndUs >= StartUs),
  Reason TEXT NOT NULL CHECK (Reason IN ('Sleep','SchedulerDelay','ContextTransition','StorageBackpressure','ClockDiscontinuity'))
) STRICT;
CREATE INDEX IX_CoverageGap_Session ON CoverageGap(SessionId, StartUs);

-- Capability state transitions. StreamKey = 'targetId|kind|family' for stream-level entries, '' otherwise.
CREATE TABLE SessionCapability (
  SessionId TEXT NOT NULL REFERENCES Session(Id) ON DELETE CASCADE,
  Capability TEXT NOT NULL CHECK (Capability IN ('IcmpResponse','WlanEvents','WlanDetails','Hotkey')),
  StreamKey TEXT NOT NULL DEFAULT '',
  SinceUs INTEGER NOT NULL CHECK (SinceUs >= 0),
  State TEXT NOT NULL CHECK (State IN ('Available','Unavailable','Denied','Unsupported','Unknown')),
  Reason TEXT NOT NULL DEFAULT '' CHECK (length(Reason) <= 64),
  PRIMARY KEY (SessionId, Capability, StreamKey, SinceUs),
  CHECK ((Capability = 'IcmpResponse') = (StreamKey <> ''))
) STRICT, WITHOUT ROWID;

CREATE TABLE SymptomMarker (
  Id TEXT PRIMARY KEY NOT NULL,
  SessionId TEXT NOT NULL REFERENCES Session(Id) ON DELETE CASCADE,
  OffsetUs INTEGER NOT NULL CHECK (OffsetUs >= 0),
  Source TEXT NOT NULL CHECK (Source IN ('Button','Tray','Hotkey')),
  Notes TEXT NOT NULL DEFAULT '' CHECK (length(Notes) <= 2000)
) STRICT;
CREATE INDEX IX_SymptomMarker_Session ON SymptomMarker(SessionId, OffsetUs);

-- One rules evaluation of one session. New rules versions add runs; old runs stay for historical reports.
CREATE TABLE AnalysisRun (
  Id TEXT PRIMARY KEY NOT NULL,
  SessionId TEXT NOT NULL REFERENCES Session(Id) ON DELETE CASCADE,
  RulesVersion TEXT NOT NULL,
  CreatedUtc INTEGER NOT NULL CHECK (CreatedUtc > 0),
  UNIQUE (SessionId, RulesVersion)
) STRICT;

CREATE TABLE Finding (
  Id TEXT PRIMARY KEY NOT NULL,
  AnalysisRunId TEXT NOT NULL REFERENCES AnalysisRun(Id) ON DELETE CASCADE,
  RuleId TEXT NOT NULL CHECK (length(RuleId) <= 16),
  Rank INTEGER NOT NULL CHECK (Rank >= 0), -- 0 = primary card
  StartUs INTEGER NOT NULL CHECK (StartUs >= 0),
  EndUs INTEGER NOT NULL CHECK (EndUs >= StartUs),
  EvidenceLevel TEXT NOT NULL CHECK (EvidenceLevel IN ('Observed','Correlated','Inconclusive')),
  PayloadJson TEXT NOT NULL CHECK (json_valid(PayloadJson)), -- message key + params, evidence refs (SessionId, Sequence ranges)
  UNIQUE (AnalysisRunId, Rank)
) STRICT;

-- ActionCode is validated against the versioned app catalog (not a CHECK: the catalog evolves without table rebuilds).
CREATE TABLE ActionAttempt (
  Id TEXT PRIMARY KEY NOT NULL,
  InvestigationId TEXT NOT NULL REFERENCES Investigation(Id) ON DELETE CASCADE,
  SourceSessionId TEXT,
  ActionCode TEXT NOT NULL CHECK (length(ActionCode) <= 64),
  State TEXT NOT NULL CHECK (State IN ('Suggested','Completed','Failed','NotPossible','Skipped')),
  CreatedUtc INTEGER NOT NULL CHECK (CreatedUtc > 0),
  UpdatedUtc INTEGER NOT NULL CHECK (UpdatedUtc >= CreatedUtc),
  Notes TEXT NOT NULL DEFAULT '' CHECK (length(Notes) <= 2000),
  UNIQUE (Id, InvestigationId),
  FOREIGN KEY (SourceSessionId, InvestigationId) REFERENCES Session(Id, InvestigationId)
) STRICT;
CREATE INDEX IX_ActionAttempt_Investigation ON ActionAttempt(InvestigationId);
CREATE INDEX IX_ActionAttempt_SourceSession ON ActionAttempt(SourceSessionId, InvestigationId);

-- Composite FKs guarantee both sessions and the action belong to the comparison's investigation.
CREATE TABLE Comparison (
  Id TEXT PRIMARY KEY NOT NULL,
  InvestigationId TEXT NOT NULL REFERENCES Investigation(Id) ON DELETE CASCADE,
  BaselineSessionId TEXT NOT NULL,
  FollowupSessionId TEXT NOT NULL,
  ActionAttemptId TEXT,
  MultipleChanges INTEGER NOT NULL CHECK (MultipleChanges IN (0,1)), -- user-confirmed
  RulesVersion TEXT NOT NULL,
  Comparability TEXT NOT NULL CHECK (Comparability IN ('Comparable','Caution','NotComparable')),
  CreatedUtc INTEGER NOT NULL CHECK (CreatedUtc > 0),
  ResultJson TEXT NOT NULL CHECK (json_valid(ResultJson)),
  CHECK (BaselineSessionId <> FollowupSessionId),
  UNIQUE (BaselineSessionId, FollowupSessionId, RulesVersion),
  FOREIGN KEY (BaselineSessionId, InvestigationId) REFERENCES Session(Id, InvestigationId) ON DELETE CASCADE,
  FOREIGN KEY (FollowupSessionId, InvestigationId) REFERENCES Session(Id, InvestigationId) ON DELETE CASCADE,
  FOREIGN KEY (ActionAttemptId, InvestigationId) REFERENCES ActionAttempt(Id, InvestigationId)
) STRICT;
CREATE INDEX IX_Comparison_Investigation ON Comparison(InvestigationId);
CREATE INDEX IX_Comparison_Baseline ON Comparison(BaselineSessionId, InvestigationId);
CREATE INDEX IX_Comparison_Followup ON Comparison(FollowupSessionId, InvestigationId);
CREATE INDEX IX_Comparison_Action ON Comparison(ActionAttemptId, InvestigationId);

CREATE TRIGGER TR_ProbeObservation_AppendOnly BEFORE UPDATE ON ProbeObservation
BEGIN SELECT RAISE(ABORT, 'ProbeObservation is append-only'); END;
CREATE TRIGGER TR_ConnectionEvent_AppendOnly BEFORE UPDATE ON ConnectionEvent
BEGIN SELECT RAISE(ABORT, 'ConnectionEvent is append-only'); END;

-- Retention input: delete an investigation when LastActivityUtc is older than the retention period (default 30 d).
CREATE VIEW InvestigationActivity AS
SELECT i.Id AS InvestigationId,
       max(i.CreatedUtc, coalesce(max(coalesce(s.EndedUtc, s.StartedUtc)), 0)) AS LastActivityUtc,
       count(s.Id) AS SessionCount
FROM Investigation i LEFT JOIN Session s ON s.InvestigationId = i.Id
GROUP BY i.Id;
