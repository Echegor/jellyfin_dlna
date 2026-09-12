# Version Pinning

## Context
Jellyfin has an automatic plugin updater that periodically checks the official plugin catalog. Because this repository is a specialized fork of the official DLNA plugin, an automatic update would overwrite all of our custom functionality with the standard stateless version.

## Decision
The assembly version of this plugin is deliberately hardcoded to `99.99.99`.

## Consequences
The plugin updater catalog will never see an upstream version higher than `99.99.99`, preventing Jellyfin from automatically overwriting this custom fork.
