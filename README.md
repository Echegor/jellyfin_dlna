<h1 align="center">Jellyfin DLNA Plugin (Custom Fork for Stateful Tracking)</h1>

> **⚠️ CUSTOM FORK**: This is a specialized fork of the official Jellyfin DLNA plugin pinned to **Jellyfin 12.0**.

## Why this exists
The official DLNA protocol is completely stateless, meaning that watching a movie on a Smart TV via DLNA will not update your "Continue Watching" progress or mark the file as played inside Jellyfin. This behavior breaks downstream services like **Suggestarr**, which rely on proper play states to curate new content.

## What we changed
To solve this, we completely modified how DLNA serves content:

1. **Virtual User Picker as Root**: We altered the DLNA directory structure so that the absolute root folder is a "User Picker". Before seeing any media, the TV user must select their Jellyfin profile.
2. **User Context Propagation**: Once a user is selected, their User ID is injected into the DLNA metadata and passed along as a `?userId=` query parameter to all streaming URLs.
3. **Stateful Progress Tracking**: We intercept the video streaming responses and wrap them in a custom `ProgressTrackingStream`. This monitors exactly how many bytes are read, calculates the exact time in the movie (ticks), and reports progress natively to Jellyfin's `SessionManager` and `UserDataManager`.

Because of these changes, any playback over DLNA behaves exactly like playback on an official web or mobile app — preserving progress, marking items as played, and allowing Suggestarr to do its job.

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

Search the Jellyfin log for `DLNA trace`. All events include an HTTP `request`
identifier; tracked streams also include the shared Jellyfin `session` identifier.
The diagnostics record request ranges, response status/content range, seeks,
first read, end of stream, disposal, elapsed time, bytes read, cancellation state,
progress report submission/completion/failure, session now-playing state, and saved
resume/played values. Full URLs, access tokens, and general request headers are
not logged. There is no log entry for every data buffer.

To reproduce, use one phone and one video, note the time, play for about a minute,
seek once, then stop. Note when the browser's playing indicator disappears and
whether refreshing changes it. Keep the complete interval of `DLNA trace` entries
and any errors, preferably from the file log with millisecond timestamps.

Different request IDs with the same session ID and overlapping `stream-open` to
`dispose` intervals establish concurrent HTTP requests from that session. Compare
`report-before`/`report-after` and `OnPlaybackStart` snapshots to see whether the
session item or position changes as another request finishes. Snapshots are
observations of shared state, not proof that a particular callback caused a change.
A request ending is not proof that the viewer stopped watching. Byte-derived
`estimatedTicks` measure downloaded data, not the phone's actual playback clock.

This instrumentation preserves the current reporting behavior, including the
existing progress report on disposal; it does not restore stop notifications or
change probe handling. The former “reporting stopped” log label was misleading
because the current implementation calls `OnPlaybackProgress` there.

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
