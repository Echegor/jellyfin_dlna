<h1 align="center">Jellyfin DLNA Plugin (Custom Fork for Stateful Tracking)</h1>

> **⚠️ CUSTOM FORK**: This is a specialized fork of the official Jellyfin DLNA plugin pinned to **Jellyfin 12.0**.

## Why this exists
When a device browses Jellyfin and pulls a video over DLNA, its HTTP requests do not provide the playback feedback available from a Jellyfin client. This fork adds user selection and conservative estimated resume tracking for those requests.

## What we changed
To solve this, we completely modified how DLNA serves content:

1. **Virtual User Picker as Root**: We altered the DLNA directory structure so that the absolute root folder is a "User Picker". Before seeing any media, the TV user must select their Jellyfin profile.
2. **User Context Propagation**: Once a user is selected, their User ID is injected into the DLNA metadata and passed along as a `?userId=` query parameter to all streaming URLs.
3. **Estimated Progress Tracking**: A shared coordinator tracks qualified file reads per user/device and reports an inferred playback session. Older HTTP requests cannot overwrite a newer qualified seek. Resume positions use elapsed time bounded by downloaded data; they are estimates, particularly with variable bitrate files.

The HTTP tracker preserves existing watched flags and does **not** automatically mark items watched. Downloading a file, including its last bytes, does not prove that the viewer watched it. Services such as Suggestarr therefore still need confirmed watched state from a player or a manual action.

## Installation & Version Pinning (99.99.99)

To install this custom fork:
1. Go to the **[Releases](https://github.com/Echegor/jellyfin_dlna/releases)** page of this repository.
2. Download the `jellyfin-plugin-dlna-custom-12.0.zip` file.
3. Extract the contents (`.dll` files) directly into your Jellyfin plugins directory (usually `/config/plugins/DLNA/`).
4. Restart your Jellyfin server.

> [!NOTE]
> **Why is the version `99.99.99`?**
> We deliberately hardcoded this plugin's version to `99.99.99` in the assembly manifest. This ensures that Jellyfin's automatic plugin updater catalog will *never* see an upstream version that is mathematically higher than ours. This guarantees that your Jellyfin server won't accidentally overwrite our custom stateful DLNA logic with the official stateless upstream version during a routine plugin update.

## Playback diagnostics

Playback diagnostics use the existing `Debug` log level. In Jellyfin's active
logging configuration, merge this entry into `Serilog.MinimumLevel.Override`
(preserve the other settings):

```json
"Jellyfin.Plugin.Dlna.Playback": "Debug"
```

Restart Jellyfin after installing the diagnostic build and applying the logging
configuration. Set this override to `Information` to silence the diagnostic
messages again; the instrumentation can remain installed. Report failures are
logged at `Error` even when debug logging is disabled.

Search the Jellyfin log for `DLNA trace`. Request events include the HTTP trace ID;
`tracking-candidate` links it to the user/device/item. Session events include the
Jellyfin session and item IDs. The logs record ranges, first reads, disposal,
qualification decisions, superseded requests, completed reports, session state,
and saved resume/played values. Full URLs, tokens, and general request headers
are not logged. There is no log entry for every data buffer.

To reproduce, use one phone and one video, note the time, play for about a minute,
seek once, then stop. Check whether the browser's playing indicator appears,
updates, and clears. Allow roughly 10–11 seconds after stopping for session cleanup.
Use a video whose watched state was not altered by an earlier test, or record its
existing state before testing. The tracker never clears an existing watched flag.

In Dashboard → DLNA, **Default user for DLNA** offers **None** and every existing
Jellyfin user. None shows the user picker. Selecting a user opens their normal
media landing screen directly and attributes new video requests to that user,
including cached links naming another user. Reopen the DLNA server after changing
the setting to refresh the listing. Device-profile user settings do not override
this choice. Resume thresholds and watched-status handling stay the same.

## Tracking policy and limits

- Playback requires either 8 MiB of contiguous read coverage or at least 1 MiB
  observed over two seconds. Adjacent short requests can accumulate evidence
  within a 10-second window; repeated overlapping bytes do not accumulate twice.
- A read starting in the final 1% (capped at 1 MiB) and transferring less than
  1 MiB is treated as a potential file-tail probe, not playback evidence.
- A later request only supersedes an earlier request once it qualifies. All
  backend callbacks for a user/device run in order on a separate worker, including
  item changes. Slow callbacks cannot block media reads or another device. Each
  device keeps only its latest pending report; failures retry on maintenance.
- Qualified sessions update about every five seconds, including when short range
  requests replace one another. Inferred positions cannot
  outrun downloaded coverage or accrue playback time while that buffer is empty.
- Closing the current request starts a 10-second reconnect grace period. A
  continuation credits the gap, bounded by downloaded coverage; if no continuation
  arrives, cleanup saves the position at close. An open request with no observed
  reads for 60 seconds ends the session, but can start a new session if it resumes
  reading. These are explicit heuristics: a buffered or paused phone can still be
  watching after requests stop, and HTTP alone cannot distinguish those cases.
- Cleanup uses `ReportSessionEnded` for the synthetic session, not a fabricated
  `OnPlaybackStopped` completion event. Resume saves respect Jellyfin's minimum
  resume thresholds and preserve watched status. They do not invoke its automatic
  completion rules. Jellyfin's separate automatic progress timer is disabled for
  these sessions so it cannot race with the coordinator.
- This tracking applies to static video streams with stable seekable byte offsets
  and known duration/length. Transcoded, live, unknown-length, and audio streams
  are not estimated by this HTTP tracker. Their media delivery is unchanged.
- Device identity comes from the request's device ID, falling back to remote IP.
  Multiple devices behind the same proxy/IP without distinct IDs cannot be
  reliably distinguished. Seeks within buffered coverage can also be ambiguous.

Run the deterministic regression tests with:

```sh
dotnet test tests/Jellyfin.Plugin.Dlna.Playback.Tests -c Release
```

See [the investigation](docs/playback-investigation.md) for the captured failure
and historical comparison. Debug logging can remain in the code permanently.

---

<h3 align="center">Original Upstream Documentation</h3>

<p align="center">
<img alt="Plugin Banner" src="https://raw.githubusercontent.com/jellyfin/jellyfin-ux/master/plugins/SVG/jellyfin-plugin-dlna.svg?sanitize=true"/>
<br/>
<br/>
<a href="https://github.com/jellyfin/jellyfin-plugin-dlna/actions?query=workflow%3A%22Test+Build+Plugin%22">
<img alt="GitHub Workflow Status" src="https://img.shields.io/github/workflow/status/jellyfin/jellyfin-plugin-dlna/Test%20Build%20Plugin.svg">
</a>
<a href="https://github.com/jellyfin/jellyfin-plugin-dlna">
<img alt="GPLv3 License" src="https://img.shields.io/github/license/jellyfin/jellyfin-plugin-dlna.svg"/>
</a>
<a href="https://github.com/jellyfin/jellyfin-plugin-dlna/releases">
<img alt="Current Release" src="https://img.shields.io/github/release/jellyfin/jellyfin-plugin-dlna.svg"/>
</a>
</p>
