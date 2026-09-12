using System;

namespace Jellyfin.Plugin.Dlna.Playback;

/// <summary>An individual HTTP request, not a playback session.</summary>
public sealed class PlaybackRequest
{
    internal PlaybackRequest(PlaybackIdentity identity, string id, long sequence)
    {
        Identity = identity;
        Id = id;
        Sequence = sequence;
    }

    internal PlaybackIdentity Identity { get; }

    internal string Id { get; }

    internal long Sequence { get; }

    internal Func<bool>? IsSuperseded { get; set; }

    internal bool Closed { get; set; }
}
