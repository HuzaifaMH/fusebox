using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Fusebox.Application.Llm;

/// <summary>Finds a registered <see cref="ILlmProtocol"/> by name.</summary>
public sealed class LlmProtocolCatalog
{
    private readonly FrozenDictionary<string, ILlmProtocol> _protocols;

    public LlmProtocolCatalog(IEnumerable<ILlmProtocol> protocols)
    {
        ArgumentNullException.ThrowIfNull(protocols);
        _protocols = protocols.ToFrozenDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGet(string name, [NotNullWhen(true)] out ILlmProtocol? protocol) =>
        _protocols.TryGetValue(name, out protocol);
}
