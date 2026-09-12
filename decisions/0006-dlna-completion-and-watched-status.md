# DLNA Completion and Watched Status Bypass

## Context
Native Jellyfin clients send exact timestamp updates. When the timestamp crosses 90% (the default `MaxResumePct`), Jellyfin marks the movie as watched. However, in DLNA DMP (Digital Media Player) mode, the TV never reports a timestamp; it only requests byte ranges over HTTP. 

Because movies use Variable Bitrate (VBR), inferring time strictly from bytes is highly volatile. Furthermore, DLNA TVs frequently issue automated "metadata probes" (requesting the last 1MB of the file instantly) to read MP4/MKV index atoms. If native Jellyfin completion rules were active, these automated background probes at 99% of the file would instantly trigger a false "watched" status.

## Decision
Native Jellyfin completion rules are still bypassed in the active stream loop. However, we implemented a **Time-Gated Stopwatch** inside `StopAsync` to handle watched status securely.

## Consequences
When a DLNA session stops and the final byte-estimated position is >90% (the default `MaxResumePct`), the plugin now checks the elapsed real-time of the session. If the session was active for at least 1 minute, it officially marks the movie as watched (setting `Played = true` and `LastPlayedDate = DateTime.UtcNow`) and fires the `PlaybackFinished` event so the UI and webhooks (like Suggestarr) react instantly. If the session was under 1 minute (e.g. an automated background tail probe), the completion is ignored and only the resume position is saved.
