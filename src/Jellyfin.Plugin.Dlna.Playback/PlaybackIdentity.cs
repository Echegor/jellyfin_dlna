using System;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Dlna.Playback;

/// <summary>Identity and stream metadata for an inferred playback session.</summary>
/// <param name="UserId">Jellyfin user.</param>
/// <param name="DeviceId">DLNA device identity.</param>
/// <param name="ItemId">Library item.</param>
/// <param name="MediaSourceId">Selected source.</param>
/// <param name="RemoteAddress">Remote address.</param>
/// <param name="DurationTicks">Source duration.</param>
/// <param name="Length">Stable file length.</param>
public sealed record PlaybackIdentity(Guid UserId, string DeviceId, Guid ItemId, string MediaSourceId, string? RemoteAddress, long DurationTicks, long Length);
