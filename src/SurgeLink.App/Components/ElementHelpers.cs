using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Components;

/// <summary>Small composition helpers used by the data views.</summary>
internal static class ElementHelpers
{
    /// <summary>An empty element that grows along the main axis, pushing siblings apart.</summary>
    public static Element Box() => Border(Empty()).Flex(grow: 1, basis: 0);
}
