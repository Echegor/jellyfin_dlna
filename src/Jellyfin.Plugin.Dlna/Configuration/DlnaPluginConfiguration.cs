#pragma warning disable CS1591, CS1572, CS1573, SA1508, SA1513, SA1214, SA1306, SA1516, SA1201, SA1611, SA1612, SA1503, SA1116, SA1117
using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Dlna.Configuration;

/// <summary>
/// Defines the <see cref="DlnaPluginConfiguration" />.
/// </summary>
public class DlnaPluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether gets or sets a value to indicate the status of the dlna playTo subsystem.
    /// </summary>
    public bool EnablePlayTo { get; set; } = true;

    /// <summary>
    /// Gets or sets the ssdp client discovery interval time (in seconds).
    /// This is the time after which the server will send a ssdp search request.
    /// </summary>
    public int ClientDiscoveryIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets a value indicating whether to blast alive messages.
    /// </summary>
    public bool BlastAliveMessages { get; set; } = true;

    /// <summary>
    /// Gets or sets the frequency at which ssdp alive notifications are transmitted.
    /// </summary>
    public int AliveMessageIntervalSeconds { get; set; } = 180;

    /// <summary>
    /// Gets or sets a value indicating whether to send only matched host.
    /// </summary>
    public bool SendOnlyMatchedHost { get; set; } = true;

    /// <summary>Gets or sets the default DLNA user; null enables the user picker.</summary>
    public Guid? DefaultUserId { get; set; }
}
