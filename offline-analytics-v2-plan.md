# Offline Analytics v2 — Re-architecture Plan

Supersedes the Phase-1 plan in `offline-analytics.md` (kept for history). Written 2026-07-16
after a cross-cutting review of PR #43 plus a consumer census. This plan is why PR #43, which
was "approved and nearly done", is being substantially reworked rather than merged.

## Why the plan changed

Four findings, in order of impact.

1. **FW Lite is an intended consumer** (hahn-kev, PR #43 review 4660741363: *"I want to use it in
   FW Lite, however FW Lite uses async Tasks where possible"*). FW Lite is **net10**, ships on
   **Windows, Linux x64/arm64, macOS x64/arm64, and Android**, is self-contained, and is
   single-file on Linux. **A net462-only package cannot be consumed by it at all.** Multi-targeting
   is now mandatory, not a nice-to-have.
2. **Only 2 of 7 consumers use Mixpanel** — classic FieldWorks and (intended) FW Lite. The other
   five (Bloom, HearThis, SayMore, Glyssen, Transcelerator) use the Segment client and can never
   execute the spool. As written, PR #43 makes all five inherit DiskQueue + Polly + mixpanel-csharp
   for a code path they cannot reach.
3. **The storage engine choice was made on a fabricated constraint.** `CONTEXT.md` asserted "no
   native components (consuming apps assume registration-free COM)". That invariant exists nowhere
   in this repo or in FieldWorks; an earlier planning session invented it and later sessions cited
   it as fact. FieldWorks already ships native NuGet assets (`libSkiaSharp`, `libHarfBuzzSharp`,
   icu.net). It ruled out SQLite for no reason. (Corrected in `CONTEXT.md`.)
4. **A UI-freeze bug on exactly the target scenario.** `EventSpool` holds its lock across the
   network send, so `Track()` — called on the host UI thread — blocks for the whole send.
   Measured: 3033 ms behind a 3 s send; ~45 s in production against stalled Wi-Fi (15 s HttpClient
   timeout x 3 Polly attempts). `ShutDown()` measured 12 s against its documented ~5 s bound.

### What is NOT a reason

The stall is **not** inherent to DiskQueue. Probed 2026-07-16: DiskQueue enqueues in ~8 ms while an
uncommitted dequeue session is open, and a second session dequeues a *distinct* item rather than
blocking or double-serving. Dropping `EventSpool`'s global semaphore would fix the stall with no
engine change. SQLite is chosen for the reasons below, not for the stall.

## Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | **Split into two packages**: `SIL.DesktopAnalytics` (facade + Segment) and `SIL.DesktopAnalytics.Mixpanel` (Mixpanel client + durable spool + Polly + mixpanel-csharp) | The only way to *guarantee* the five Segment consumers are unaffected. Also makes them lighter than PR #43 leaves them. |
| D2 | **Both packages multi-target `net462;netstandard2.0;net8.0`** | See "TFM strategy" below. **`net462` floor is NOT negotiable up to `net48`** — that would break HearThis and Glyssen, which are net472. |
| D2a | **Settings: `#if NET462` keeps `ApplicationSettingsBase` exactly as today; modern TFMs get a JSON store** | Eliminates the migration risk entirely — existing users' `user.config` is never touched. FW Lite has no existing settings to preserve. |
| D3 | **SQLite (`Microsoft.Data.Sqlite`) replaces DiskQueue**; delete `EventSpool` | Fixes multi-process event loss (below) and the stall by construction. FW Lite already ships `SQLitePCLRaw.bundle_e_sqlite3`, so it costs FW Lite nothing. |
| D4 | **API break, major version (7.0.0)** | `ClientType.Mixpanel` in a core enum cannot resolve a type in another package. Approved by the owner. Consumers are on 4.0.0/4.0.4/6.0.2 anyway. |
| D5 | **Async-first API** | hahn-kev's explicit requirement for FW Lite. |
| D6 | **Per-event retry counter** | hahn-kev, PR #43: *"if there's a bug with a specific event we could get stuck on a single event and retry it forever"*. |
| D7 | **Path scrubbing, `$insert_id` dedup, consent purge, `/import` batching, 60-day retention** — all carry over unchanged from PR #43 | These were reviewed and are sound. |

### Why SQLite specifically (the reason that survives scrutiny)

Not the stall (see above). The real reasons:

- **Multi-process event loss.** FieldWorks runs multiple `FieldWorks.exe` processes against one
  per-user spool. DiskQueue takes a **cross-process exclusive lock**, so the second process cannot
  open the spool, degrades to a no-op, and **silently loses every event it produces**. This is
  unfixable within DiskQueue. SQLite's WAL gives real concurrent readers/writers.
- **Consent purge honesty.** `DELETE` + `VACUUM` genuinely reclaims bytes. DiskQueue needed
  dispose + delete-the-directory + reopen, which briefly drops the cross-process lock and can
  degrade the spool to a no-op if another process steals it.
- **Complexity that exists only to compensate for DiskQueue**: the `spool-bytes.txt` sidecar, its
  staleness heuristic, and its re-measure fallback all collapse to `SUM(len)`.
- **Cross-platform**: FW Lite needs Linux/macOS/**Android**. DiskQueue's file-locking on those is
  untested by us; SQLite's is not.

Rejected: **LiteDB** (pure-managed, no native asset) — no stable release in ~2 years, and open bugs
#1538 (multi-process lock contention) and #2526 (unrecoverable state under concurrency) sit exactly
on the claim/release/send/delete pattern we depend on.

## Consumer matrix

| Consumer | Client | Needs spool | TFM | Platforms | Impact of this plan |
|---|---|---|---|---|---|
| FieldWorks (classic) | Mixpanel | **yes** | net48 (net10 Linux planned) | Win x64 | Adds `.Mixpanel` package; gains multi-process durability. Linux is a new platform for this app — no existing settings to migrate there |
| FW Lite | Mixpanel (intended) | **yes** | net10 / MAUI | Win, Linux, macOS, Android | Becomes consumable at all; already ships `bundle_e_sqlite3` |
| Bloom | Segment | no | net8.0-windows | Win | **Lighter** — sheds DiskQueue/Polly/mixpanel-csharp; avoids `bundle_green` collision risk |
| HearThis | Segment | no | net472 | Win | **Lighter** |
| SayMore | Segment | no | net48 | Win | **Lighter** |
| Glyssen | Segment | no | net472 | Win | **Lighter** |
| Transcelerator | Segment | no | net48 | Win (Paratext plugin) | **Lighter** |
| Paratext (host) | unknown | — | — | — | unknown; closed source |

## Phases

### Phase 0 — Verify the long pole ✅ DONE (2026-07-16)

**Question:** can this library reach `netstandard2.0` at all?
**Answer: yes, and the blocker is small.** A `netstandard2.0` build fails on exactly two files with
four missing types:

| File | Missing types |
|---|---|
| `AnalyticsSettings.Designer.cs` | `ApplicationSettingsBase`, `UserScopedSettingAttribute`, `DefaultSettingValueAttribute` |
| `Analytics.cs` | `ApplicationSettingsBase`, `ConfigurationUserLevel` |

Everything else compiles clean on netstandard2.0 — Segment, mixpanel-csharp, DiskQueue, Polly,
**Microsoft.Data.Sqlite**, `NetworkChange`, `TimeProvider`. `AnalyticsSettings` is only six
user-scoped values (`IdForAnalytics`, `LastVersionLaunched`, `NeedUpgrade`, `FirstName`,
`LastName`, `Email`). This is a contained refactor, not a rewrite.

## TFM strategy (decided 2026-07-16, empirically verified)

Options considered. The net4x floor is the decisive axis: **a net4x TFM is only consumable by that
version or higher**, and HearThis + Glyssen are **net472**.

| Option | net472? | net48? | net10? | Redirect risk | Verdict |
|---|---|---|---|---|---|
| A. `netstandard2.0` only | via facades | via facades | ✅ | **High** — reintroduces binding-redirect fragility for net4x consumers | no |
| B. `net462;netstandard2.0` | ✅ native | ✅ | ✅ (fallback) | low | safe, but net10 gets legacy surface |
| C. `net462;net8.0` | ✅ native | ✅ | ✅ | low | lean, defensible |
| **D. `net48;net10.0`** *(as originally requested)* | ❌ **BREAKS** | ✅ | ✅ | — | **ruled out** |
| **E. `net462;netstandard2.0;net8.0`** | ✅ native | ✅ | ✅ native | low | **chosen** |
| F. `netstandard2.0;net8.0` | via facades | via facades | ✅ | **High** | no |

**Chosen: E.** It's Microsoft's own documented pattern (*"CONSIDER adding a target for net462 when
you're also targeting netstandard2.0 — using .NET Standard 2.0 from .NET Framework has some issues
that were addressed in 4.7.2"*). A `net8.0` asset serves net10 and MAUI's `net10.0-*` TFMs by
forward compatibility; no net10-only APIs are needed. It also lets `Microsoft.Bcl.TimeProvider` be
dropped conditionally on the net8.0 leg (`TimeProvider` is in-box there).

**Note on the net48 request:** keeping the floor at `net462` *does* serve net48 — every net4x
consumer from 4.6.2 up gets the native asset. Raising the floor to `net48` gains nothing and
breaks two shipping consumers.

**Verified empirically (2026-07-16), not inferred:**
- `net462;netstandard2.0;net8.0` compiles **clean — zero code changes, zero warnings** — once
  `System.Configuration.ConfigurationManager` 8.0.0 is added for the non-net462 legs. The four
  "missing types" are all supplied by that package (it multi-targets net462/netstandard2.0/net8.0).
  So the earlier claim that `ApplicationSettingsBase` is WindowsDesktop-only is **false**.
- `ApplicationSettingsBase` also **runs** on .NET 8 on Windows *and* on real Linux (Ubuntu 26.04,
  via WSL): `Save()` succeeds, persists to an XDG-correct `~/.local/share/.../user.config`, and
  reads back across restarts. It works under `PublishSingleFile` on Linux too. (This contradicts
  `dotnet/runtime#28833`, which appears stale.)

### So why not just use `ApplicationSettingsBase` everywhere?

Because "it runs" isn't the bar. The probe surfaced the disqualifying detail:

```
self-contained, non-single-file:  .../settingsprobe_Url_13lsa3weyuz52bthetj4czmrh2ia2n3f/1.0.0.0/user.config
self-contained, single-file:      .../settingsprobe_Path_rwy2hyf5upl41j2llclliaklcluo0ikv/1.0.0.0/user.config
```

**The settings identity is a hash of the app's path/URL evidence plus its version** — it changed
(`_Url_` → `_Path_`) purely from changing the publish shape. For FW Lite — self-contained,
single-file, auto-updating, installed to varying paths — that means **`IdForAnalytics` silently
resets and the user looks brand-new**. That is precisely what `IdForAnalytics` exists to prevent.
The `1.0.0.0` version folder is the same problem, and is why `.Upgrade()` exists at all.

This is a known family of bugs, not our misuse: `dotnet/runtime#121053` (unstable per-install
settings hash), `#98715` (`.Upgrade()` throws on fresh macOS accounts). Both open. And
`System.Configuration.ConfigurationManager`'s own README says it *"exists only to support migrating
existing .NET Framework code"* — it is a migration shim, not new cross-platform infrastructure.

A plain JSON file at a **stable, well-known path** (`LocalApplicationData/SIL/DesktopAnalytics/`) is
strictly more stable than `user.config` — no evidence hash, no version folder, no `.Upgrade()`
needed. It's also the pattern Avalonia documents, which is the UI stack FW Lite is built on.

**A related risk was considered and does NOT apply, on closer inspection.** John: *"FieldWorks will
ship Linux again, but with net10, not Mono."* First pass reasoning here proposed a Windows
`user.config` import to protect against that move — that doesn't hold up. **New Linux users have no
prior `user.config` on that platform to lose**; there is nothing to import for them. The only
scenario that *would* need an import is classic FieldWorks **on Windows** later leaving net462 for
the net8/net10 leg — a different, unscheduled, unconfirmed move that has not been stated as planned.
Building an import keyed to the wrong transition (Windows path, justified by a Linux launch) would
be dead code for the Linux users and untested for the Windows scenario it would actually serve.
**Decision: do not build this now.** `IAnalyticsSettingsStore` (Phase 1) is the seam — if classic
FieldWorks' Windows build is ever moved off net462, add a `user.config`-import path to the JSON
store then, when the actual transition is confirmed. Flagging the seam here so it isn't rediscovered
from scratch.

### Phase 1 — Multi-target + settings split (the gate)

**Risk reassessment: this phase got much cheaper.** The original plan moved *all* TFMs to a JSON
store and migrated existing users — carrying a High risk of losing `IdForAnalytics` for every
existing FieldWorks user. That is no longer necessary.

1. Multi-target `net462;netstandard2.0;net8.0`. Add `System.Configuration.ConfigurationManager` for
   the non-net462 legs; drop `Microsoft.Bcl.TimeProvider` on the net8.0 leg.
2. **`#if NET462`: keep `AnalyticsSettings`/`ApplicationSettingsBase` byte-for-byte as it is
   today.** Existing consumers (all .NET Framework) keep reading and writing the exact same
   `user.config`, with the exact same `.Upgrade()` behavior. **Zero migration. Zero risk. No
   behavior change.**
3. **Modern TFMs: a small JSON-backed store** (`LocalApplicationData/SIL/DesktopAnalytics/settings.json`,
   System.Text.Json) behind `IAnalyticsSettingsStore`. No migration needed for FW Lite — it has no
   existing settings. No `.Upgrade()` — a stable path makes it unnecessary.
4. Do **not** route net8/net10 through `ApplicationSettingsBase`, even though it compiles and runs.
   See above.
5. **No legacy import in this phase.** Considered and rejected for now — see the note above. FW
   Lite has no prior settings to migrate; there is no confirmed transition on the Windows FieldWorks
   build that would need one yet. `IAnalyticsSettingsStore` is the seam to extend if/when that
   changes.
6. Tests: JSON store round-trip + restart stability on the modern leg; net462 leg unchanged
   (existing tests must still pass untouched).

**Exit criteria:** all three TFMs build; net462 behavior provably unchanged; JSON store stable
across restart and across a simulated path/version change.

### Phase 2 — Package split + API break (7.0.0)

1. New project/package `SIL.DesktopAnalytics.Mixpanel`: `MixpanelClient`, `IEventSpool`,
   `SqliteEventSpool`, `IEventSender`, `MixpanelEventSender`, `AnalyticsEvent`, `PathScrubber`.
   Moves `Microsoft.Data.Sqlite`, `Polly`, `mixpanel-csharp` out of core.
2. Core keeps the facade, `SegmentClient`, `IClient`, `UserInfo`, settings.
3. Replace the `ClientType` enum with client injection. Confirm `IClient` is a good public seam.
4. **Flag for FW Lite (per owner):** we pull `SQLitePCLRaw` 2.1.6 via `Microsoft.Data.Sqlite`
   8.0.10; FW Lite pins **3.0.3** via `EFCore.Sqlite` 10.0.8. NuGet unifies upward — **FW Lite to
   verify the upgrade causes no issues.** May need TFM-conditional `Microsoft.Data.Sqlite`
   versions (8.0.x for net462, 9.x/10.x for netstandard2.0) if newer versions drop netstandard2.0.
5. Migration notes for all five Segment consumers (they should see only a version bump + a lighter
   dependency graph).

### Phase 3 — Adopt SQLite, delete DiskQueue

1. Point `MixpanelClient.Initialize` at `SqliteEventSpool`.
2. Delete `EventSpool`, `EventSpoolTests`, the DiskQueue dependency, and the
   `Track_WhileSendInFlight_BlocksUntilSendCompletes_DiskQueue` characterization test.
3. Collapse `EventSpoolContractTests<TFactory>` to a single engine.
4. Keep `IEventSpool` — it's the test seam and the injection point.

### Phase 4 — Async-first

1. `TrackAsync` / `ReportExceptionAsync` as primitives; `IEventSpool.EnqueueAsync`.
2. **Honest caveat:** `Microsoft.Data.Sqlite` does **not** implement true async I/O — its async
   ADO.NET methods are synchronous internally. The win here is API ergonomics for FW Lite and the
   already-async network path, **not** non-blocking disk I/O. Do not claim otherwise. A local
   SQLite insert is sub-millisecond; that is why it's acceptable.
3. Decide the fate of sync `Track()`: keep as a non-blocking convenience for classic FieldWorks
   (which has ~dozens of UI-thread call sites and will not await), or drop it. Leaning keep.

### Phase 5 — Per-event retry counter (D6)

1. Add `attempts INTEGER NOT NULL DEFAULT 0` to the events table.
2. On `RetryableFailure`: `UPDATE events SET attempts = attempts + 1, lease_until_unix_ms = 0
   WHERE id IN (...)`.
3. Then `DELETE FROM events WHERE attempts >= @maxAttempts`, counting each as Failed via
   `ItemDroppedByCap` (or a new signal).
4. Default ~10 attempts. Note this bounds a wedge that today only the 60-day age floor catches —
   i.e. 60 days of pointless retries.
5. Tests: N-1 attempts retained; Nth dropped; counter survives restart; a Delivered batch never
   increments.

### Phase 6 — Outstanding review findings (still open from PR #43)

1. **`ShutDown()` violates its own bound** — measured 12 s vs. a documented ~5 s. It never calls
   `CancelInFlightSend()` the way `PurgeQueuedEvents` does, so it waits out an in-flight send.
   (SQLite likely improves this; **measure, do not assume**.)
2. **Drain pacing** — 256 KB per 30 s tick is ~8.5 KB/s (~68 kbit/s). Clearing a full backlog takes
   **50-100 minutes of continuous connectivity**; a cafe session will not finish it. It resumes
   safely, but this likely defeats the intent. Consider back-to-back ticks while a backlog exists
   and sends succeed.
3. **PR text** claims Flush/ShutDown are bounded — currently false (see 1).
4. `ApproximateCount` semantics differ mid-send between engines (moot once DiskQueue is deleted).

### Phase 7 — Consumer integration

Packages, as actually built (Phases 1-6, all committed on this branch):

- **`SIL.DesktopAnalytics`** — facade (`Analytics`), `SegmentClient`, `IClient`, `UserInfo`,
  `Statistics`. Multi-targets `net462;netstandard2.0;net8.0`.
- **`SIL.DesktopAnalytics.Mixpanel`** — `MixpanelClient`, `SqliteEventSpool`/`IEventSpool`,
  `IEventSender`/`MixpanelEventSender`, `AnalyticsEvent`, `PathScrubber`. Same TFMs. References
  `Microsoft.Data.Sqlite`, Polly, `mixpanel-csharp` — none of which leak into core (verified via
  `dotnet list package --include-transitive`).

API break, major version bump (D4): `ClientType` enum is gone. Callers now inject an `IClient`:
```csharp
// before
new Analytics(apiSecret, userInfo, clientType: ClientType.Mixpanel);
// after (classic FieldWorks / FW Lite -- add a reference to SIL.DesktopAnalytics.Mixpanel)
new Analytics(apiSecret, userInfo, client: new MixpanelClient());
// Segment default is unchanged -- omitting `client` still defaults to `new SegmentClient()`
new Analytics(apiSecret, userInfo);
```

1. **FieldWorks (classic):** add a `SIL.DesktopAnalytics.Mixpanel` package reference; change the
   `ClientType.Mixpanel` call site to `client: new MixpanelClient()` per above. Installer:
   `e_sqlite3.dll` (~4.1 MB, 3 Windows RIDs) is a normal transitive native asset under
   `Microsoft.Data.Sqlite`; WiX heat auto-harvests it the same way it already does for
   `libSkiaSharp`/`libHarfBuzzSharp`/icu.net — no installer authoring expected, but worth a
   sanity-check build once adopted. **Flag for FieldWorks to verify themselves:** this package
   pins `SQLitePCLRaw.bundle_e_sqlite3` 2.1.6 (via `Microsoft.Data.Sqlite` 8.0.10); if FieldWorks
   pins a different SQLitePCLRaw version anywhere else in its dependency graph, NuGet unifies
   upward and FieldWorks should confirm that causes no issues.
2. **FW Lite:** same package addition; this is what makes it consumable at all (previously
   net462-only). **Flag for FW Lite to verify themselves:** FW Lite reportedly already pins
   `SQLitePCLRaw` 3.0.3 via `EFCore.Sqlite` 10.0.8 — higher than this package's 2.1.6, so NuGet
   unification should just take FW Lite's existing pin with no action needed, but confirm rather
   than assume. `PublishSingleFile` on Linux needs `IncludeNativeLibrariesForSelfExtract=true` for
   the SQLite native asset to extract correctly; FW Lite very likely already has this set (it
   would need it for its own existing SQLite dependency), but worth confirming when adopting.
3. **Segment consumers (Bloom, HearThis, SayMore, Glyssen, Transcelerator):** version bump only.
   They take `SIL.DesktopAnalytics` alone and end up *lighter* than PR #43 would have left them —
   no DiskQueue/Polly/mixpanel-csharp/SQLite in their dependency graph at all. No code changes
   expected unless a consumer explicitly referenced `ClientType` (none should have, since none use
   Mixpanel).

## Risks

| Risk | Severity | Mitigation |
|---|---|---|
| Settings migration loses `IdForAnalytics` | Low, for now | `#if NET462` keeps *today's* users on `user.config` untouched. A Linux launch creates no migration (no prior Linux `user.config` to lose). If classic FieldWorks' **Windows** build is ever moved off net462, `IAnalyticsSettingsStore` is the seam to add a `user.config` import then — not built speculatively now |
| Raising the net4x floor to net48 breaks HearThis + Glyssen (net472) | **High if done** | Keep the floor at `net462`; it already serves net48 |
| Routing modern TFMs through `ApplicationSettingsBase` resets `IdForAnalytics` on path/version change | **High if done** | Don't. JSON store at a stable path. Verified: the evidence hash changes with publish shape (`_Url_`→`_Path_`) |
| `Microsoft.Data.Sqlite` v11 proposes dropping .NET Framework support (`dotnet/efcore#37895`) | Low (proposed, not shipped) | Pin ≤10.x for net462/netstandard2.0 legs via TFM-conditional versions if it lands |
| `.Upgrade()` has open bugs on macOS/iOS (`#98715`, `#121053`) | Low | We only call `.Upgrade()` on the net462 leg (Windows) |
| SQLitePCLRaw version skew with FW Lite | Medium | Flagged in PR; FW Lite verifies; TFM-conditional versions if needed |
| API break churns 7 consumers | Medium | Accepted by owner; all are on old pins already |
| Bloom `bundle_green` vs `bundle_e_sqlite3` collision | Low (moot under D1) | Package split removes the question entirely. *Note: verified both bundles ship an assembly named `SQLitePCLRaw.batteries_v2.dll`; a real build break was NOT demonstrated.* |
| netstandard2.0 loses `NetworkChange` reconnect accelerator | Low | Works on modern .NET (netlink); already wrapped in try/catch and non-fatal — the poll is the guarantee |
| Scope growth | Medium | Phases 1-3 are the core; 4-6 can ship incrementally |

## Current state (what exists today, on `feature/offline-mixpanel-durability`)

Phases 1-6 are done, each independently rebuilt/retested/inspected (not just taken on a
subagent's word) before being committed:

- **Phase 1:** multi-targets `net462;netstandard2.0;net8.0`. `#if NET462` keeps
  `ApplicationSettingsBase`/`user.config` byte-for-byte; modern TFMs get a JSON-backed
  `IAnalyticsSettingsStore` at a stable path. No legacy-import logic (deliberately not built --
  see the Risks table).
- **Phase 2:** split into `SIL.DesktopAnalytics` + `SIL.DesktopAnalytics.Mixpanel`. `ClientType`
  enum replaced by `IClient` injection. Verified via `dotnet list package --include-transitive`
  that core carries none of DiskQueue/Sqlite/Polly/mixpanel-csharp.
- **Phase 3:** `SqliteEventSpool` adopted in production; `EventSpool`/DiskQueue deleted entirely.
  Never-throws contract verified by inspection (all six `IEventSpool` members on
  `SqliteEventSpool` log-and-swallow).
- **Phase 4:** `TrackAsync`/`ReportExceptionAsync` added across `IClient`/`SegmentClient`/
  `MixpanelClient`/`Analytics`, honest about being ergonomics (Microsoft.Data.Sqlite has no true
  async I/O) rather than a concurrency fix. Verified with a timed test: ~0.8ms while a send is
  genuinely in flight.
- **Phase 5:** per-event retry ceiling (default 10 attempts). Caught and fixed a real bug before
  shipping it: a naive counter would have dropped good events after ~5 minutes offline, since
  "can't reach the server" and "server rejected this" both collapsed into one `RetryableFailure`
  value. Split into `RetryableFailure` (connectivity, never counted) vs. new
  `RetryableRejection` (408/429/5xx, counted) — verified against the 3 tests that caught the bug.
- **Phase 6:** `ShutDownAsync` now calls `CancelInFlightSend()` (closing a gap where a
  timer-driven drain wedged mid-send would otherwise linger past `ShutDown`'s own dispose).
  Drain pacing now accelerates (1s) after a tick that made progress and still leaves a backlog,
  instead of waiting the full 30s interval — fixes an estimated 50-100 minute full-backlog clear
  time down to accelerated back-to-back ticks. Both verified with real `Stopwatch` measurements,
  not just green tests.

Suite: 114 Mixpanel tests + 19 (net8.0) / 14 (net462) core tests, all green on both runnable TFMs.
Not yet pushed to the fork that backs PR #43 (fork last saw only the plan-doc commit) --
none of this rework is visible on the PR yet.
- `CONTEXT.md` and `offline-analytics.md` corrected re: the fabricated invariant.
- **Phase 7 (this phase):** consumer integration notes above are written; PR #43's description
  still describes the pre-rework (DiskQueue, single-package) design and has not been touched --
  see below.
