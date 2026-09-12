# Virtual User Picker

## Context
Standard DLNA clients act as unauthenticated Digital Media Players (DMP). They have no native concept of "login" or user profiles. This breaks core Jellyfin functionality like personalized profiles, "Continue Watching", and syncing with third-party tracking tools.

## Decision
We modify the DLNA directory tree to inject a "User Picker" folder at the absolute root. A user must select their Jellyfin profile before they can browse any media.

## Consequences
The selected User ID is injected into the DLNA metadata and appended as a `?userId=` query parameter to all subsequent streaming URLs. This ensures playback is correctly attributed to the specific user's session in Jellyfin.
