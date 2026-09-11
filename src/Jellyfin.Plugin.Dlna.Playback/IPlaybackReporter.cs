using System;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Dlna.Playback;

/// <summary>Reports ordered observations without treating downloads as completion.</summary>
public interface IPlaybackReporter
{
    /// <summary>Start one inferred playback.</summary>
    /// <param name="identity">Playback identity.</param>
    /// <param name="positionTicks">Estimated position.</param>
    /// <returns>The report task.</returns>
    Task StartAsync(PlaybackIdentity identity, long positionTicks);

    /// <summary>Report an estimated position.</summary>
    /// <param name="identity">Playback identity.</param>
    /// <param name="positionTicks">Estimated position.</param>
    /// <returns>The report task.</returns>
    Task ProgressAsync(PlaybackIdentity identity, long positionTicks);

    /// <summary>End the inferred session without asserting completion.</summary>
    /// <param name="identity">Playback identity.</param>
    /// <param name="positionTicks">Estimated position.</param>
    /// <returns>The report task.</returns>
    Task StopAsync(PlaybackIdentity identity, long positionTicks);
}
