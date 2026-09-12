# DLNA Completion and Watched Status Bypass

## Context
Native Jellyfin clients send exact timestamp updates. When the timestamp crosses 90% (the default `MaxResumePct`), Jellyfin marks the movie as watched. However, in DLNA DMP (Digital Media Player) mode, the TV never reports a timestamp; it only requests byte ranges over HTTP. 

Because movies use Variable Bitrate (VBR), inferring time strictly from bytes is highly volatile. Furthermore, DLNA TVs frequently issue automated "metadata probes" (requesting the last 1MB of the file instantly) to read MP4/MKV index atoms. If native Jellyfin completion rules were active, these automated background probes at 99% of the file would instantly trigger a false "watched" status.

## Decision
The original author explicitly bypassed Jellyfin's native completion rules in `PlaybackTrackingService.cs` (`UpdatePlayState` is bypassed and `data.Played` is preserved). 

## Consequences
Currently, playing a video via DLNA through this plugin will **never** mark it as fully watched.

## Future Hardened Solutions
If we want to re-enable "mark as watched" for DLNA, we must rely on a **Real-Time Investment Gate** inside `StopAsync`. By verifying that the final byte-estimated position is >90% *AND* cross-referencing it with the real-time lifespan of the stream, we can ensure the user actually watched the movie (e.g., stream was active for > 5 minutes) rather than the TV just executing a 2-second background metadata probe.
