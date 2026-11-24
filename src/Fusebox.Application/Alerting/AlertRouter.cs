using Microsoft.Extensions.Options;

namespace Fusebox.Application.Alerting;

/// <summary>Decides which channels a notification of a given severity goes to.</summary>
public sealed class AlertRouter
{
    private readonly AlertOptions _options;
    private readonly string[] _available;

    public AlertRouter(IOptions<AlertOptions> options, IEnumerable<INotifier> notifiers)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(notifiers);

        _options = options.Value;
        _available = [.. notifiers.Select(n => n.Channel)];
    }

    /// <summary>
    /// The channels configured for the severity that are actually available. If none of them is, any available channel is
    /// used instead, so an alert is never silently dropped because one channel was not set up. Only when no channel at all
    /// is available is the answer empty.
    /// </summary>
    public IReadOnlyList<string> ChannelsFor(AlertSeverity severity)
    {
        var configured = _options.Routing.FirstOrDefault(pair => string.Equals(pair.Key, severity.ToString(), StringComparison.OrdinalIgnoreCase)).Value ?? [];

        // Always the channel's own name, so a notification is saved the same way however the routing was spelled.
        var chosen = configured
            .Select(name => _available.FirstOrDefault(channel => string.Equals(channel, name, StringComparison.OrdinalIgnoreCase)))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return chosen.Count > 0 ? chosen : [.. _available];
    }
}
