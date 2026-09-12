# Playback request investigation — 2026-09-11

The phone test at 22:43:08–22:43:26 EDT on September 10 produced five GET requests
for one 4,300,392,594-byte video in one Jellyfin session. It demonstrated:

1. A 55,759-byte request beginning at byte 4,300,336,835 reported the entire
   5,367.957-second duration and saved played=true with no resume position.
2. A new request reported roughly 29 minutes, then disposal of an older request
   overwrote the shared session position with roughly 31 seconds.
3. Closing the final request emitted progress instead of ending the session.

These observations establish tracking defects. The server retained a now-playing
item in the captured snapshots; they do not independently establish the cause of
an absent browser indicator. That requires comparing the next phone test with
browser behavior.

## Historical replay

The actual ProgressTrackingStream source from each commit was compiled in an
isolated test project. A seekable synthetic 4,300,392,594-byte stream replayed the
55,759-byte file-tail request followed immediately by asynchronous disposal.
SessionManager callbacks were recorded with mocks; no live user history changed.
All four historical assertions passed.

| Source commit | Immediate tail request behavior |
| --- | --- |
| 20477c2 | No progress report; disposal reports stopped at zero |
| 0ceed2a | Same as baseline for this short request |
| 6a63e19 | Same as baseline for this short request |
| f815c88 | Two progress reports at full duration; no stopped report |

This isolates the file-tail regression to f815c88. The request/session lifetime
problem existed at the baseline. This particular replay cannot establish whether
the background task in 0ceed2a caused additional ordering failures on longer reads.
Reproduce this comparison with `python3 tests/replay_historical.py`. The harness
extracts the original Git sources into a temporary project; the permanent .NET
regression suite tests the replacement.

## Replacement and validation

The coordinator accepts playback evidence before creating a session, rejects
small tail probes, serializes callbacks by user/device, and ignores superseded
request updates. Tests use an injected monotonic clock and blocked/failed callbacks
to reproduce ordering and timeout conditions without sleeps. Reporting-layer tests
verify that estimates at the end preserve both true and false existing watched
flags, never invoke UpdatePlayState, and end the synthetic session without an
invented completion event.

Additional tests cover adjacent ranges, backward seeks, reconnect grace, idle
expiry, late callbacks after expiry, multiple devices/users, item changes, shutdown,
maintenance failure isolation/retry, empty responses, and downloaded data ahead of
the playback clock. Live verification still requires a new phone/browser test.

## Follow-up review fixes — 2026-09-12

The diff review against 0eec286 identified six regressions or performance problems,
plus loss of explicitly configured user selection. All have been addressed:

- Short request gaps now contribute buffered elapsed time when a continuation
  arrives during reconnect grace. Ten 10 MiB requests eight seconds apart advance
  the last reported estimate to 72 seconds instead of freezing at zero.
- An idle open request can resume tracking after the 60-second session timeout.
  Its owner watermark remains until close, so older superseded requests stay
  rejected. Shutdown still prevents all resurrection.
- Ownership changes obey the five-second report interval. A burst of 100 adjacent
  1 MiB requests now produces one progress report, instead of 93. A seek can take
  up to five seconds to appear in Jellyfin.
- Backend callbacks run on one asynchronous worker per device, with one latest
  pending state. A blocked callback cannot stall HTTP reads or another device;
  pending progress coalesces and failed callbacks retry during maintenance.
  Cleanup for the actual reported session remains ordered with subsequent starts.
- Virtual user containers omit unknown childCount instead of declaring zero.
- Root user listings apply StartingIndex and RequestedCount, with stable ordering
  and separate total and returned counts.
- Device profile UserId again overrides DefaultUserId. With neither configured,
  the user picker remains available. Invalid/deleted configured users fail closed;
  prefixed object IDs cannot switch a fixed-user device to another user.

Validation: 30 regression tests, four historical assertions, a Release solution
build with zero warnings/errors, and JavaScript syntax validation pass. The
blocked-callback test also coalesces 10,000 pending updates and verifies another
user/device remains responsive. These deterministic tests do not replace the
remaining live phone/browser check.
