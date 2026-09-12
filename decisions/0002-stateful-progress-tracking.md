# Stateful Progress Tracking

## Context
The official DLNA plugin relies on stateless HTTP requests, which cannot distinguish between a user actually watching a video and a device probing a file for metadata or caching. This results in highly unreliable playback progress tracking.

## Decision
We introduced a sophisticated coordinator (`PlaybackTracker`) that analyzes HTTP range requests and downloaded bytes to infer playback progress.

## Consequences
- **Smart Filtering:** It distinguishes between real playback and metadata probes (e.g., rejecting reads of the final 1% of a file if the transfer is less than 1 MiB).
- **Session Management:** It maintains a synthetic playback session that smoothly handles chunked streaming, buffering gaps, and device reconnects without incorrectly marking items as watched.
