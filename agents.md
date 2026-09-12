# Jellyfin DLNA Custom Plugin - Purpose & Overview

This repository is a specialized fork of the official Jellyfin DLNA plugin. Its primary purpose is to solve two major limitations of DLNA media serving: **User Profiles** and **Stateful Playback Tracking**.

## Why this fork exists
The official Jellyfin DLNA implementation lacks user context (since DLNA clients don't log in) and provides unreliable playback progress tracking because standard DLNA players do not report their playback status back to the server. This breaks core functionality like personalized profiles, "Continue Watching", and syncing with third-party tools like Suggestarr.

## Core Modifications

By examining the commits since this library was forked, the purpose of this repository centers around these major changes:

### 1. Virtual User Picker
- **The Problem:** DLNA devices cannot natively log into Jellyfin profiles.
- **The Solution:** The DLNA directory tree is modified to inject a "User Picker" folder at the absolute root. A user must select their Jellyfin profile before they can browse any media.
- **Context Propagation:** The selected User ID is injected into the DLNA metadata and appended as a `?userId=` query parameter to all subsequent streaming URLs, ensuring playback is attributed to the correct user. There is also an option to set a "Default DLNA User".

### 2. Stateful Progress Tracking
- **The Problem:** The official plugin relies on stateless HTTP requests, which cannot distinguish between a user actually watching a video and a device probing a file for metadata or caching. 
- **The Solution:** This fork introduces a sophisticated coordinator that tracks HTTP range requests and downloaded bytes to infer playback progress.
- **Smart Filtering:** It distinguishes between real playback and metadata probes (e.g., rejecting reads of the final 1% of a file if it transfers less than 1 MiB).
- **Session Management:** It maintains a playback session, smoothly handling chunked streaming, buffering gaps, and device reconnects without incorrectly marking items as watched. 

### 3. Version Pinning
- **The Problem:** Jellyfin's automatic plugin updater could overwrite this custom fork with the official stateless version.
- **The Solution:** The assembly version is deliberately hardcoded to `99.99.99` so the plugin updater catalog will never see an upstream version higher than this custom fork.

## Summary
This custom library intercepts and analyzes the underlying DLNA HTTP requests to emulate the stateful behaviors (user attribution and progress tracking) of a native Jellyfin client, allowing for personalized profiles and accurate "Continue Watching" tracking over the stateless DLNA protocol.
