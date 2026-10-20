using System.Reflection;

namespace SurgeLink.Protocols.Conformance;

/// <summary>
/// Discovers <see cref="IProtocolUnderTest"/> implementations so adding a
/// protocol is exactly one fixture class. Fixtures are reflected from the
/// conformance assembly and from any loaded assembly that references it.
/// Explicit <see cref="Register"/> calls are also supported for fixtures that
/// cannot use a parameterless constructor.
/// </summary>
public static class ProtocolConformanceRegistry
{
    private static readonly object Gate = new();
    private static readonly List<IProtocolUnderTest> Registered = new();
    private static readonly List<string> DiscoveryFailures = new();
    private static IReadOnlyList<IProtocolUnderTest>? _discovered;

    /// <summary>Registers a fixture explicitly, in addition to discovered ones.</summary>
    public static void Register(IProtocolUnderTest fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (string.IsNullOrWhiteSpace(fixture.Name))
        {
            throw new ArgumentException("A conformance fixture needs a non-empty Name.", nameof(fixture));
        }

        lock (Gate)
        {
            if (Registered.Any(existing => string.Equals(existing.Name, fixture.Name, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"A protocol fixture named '{fixture.Name}' is already registered.");
            }

            Registered.Add(fixture);
        }
    }

    /// <summary>Every fixture known to the suite, ordered by name.</summary>
    public static IReadOnlyList<IProtocolUnderTest> All
    {
        get
        {
            lock (Gate)
            {
                _discovered ??= DiscoverFixtures();
                var combined = _discovered
                    .Concat(Registered)
                    .OrderBy(fixture => fixture.Name, StringComparer.Ordinal)
                    .ToList();
                EnsureUniqueNames(combined);
                return combined;
            }
        }
    }

    /// <summary>Fixture names in the order the suite runs them.</summary>
    public static IReadOnlyList<string> Names => All.Select(fixture => fixture.Name).ToList();

    /// <summary>
    /// Problems encountered while reflecting fixtures (for example a constructor
    /// that threw). The registry tests require this to be empty so a broken
    /// fixture cannot silently drop out of the suite.
    /// </summary>
    public static IReadOnlyList<string> DiscoveryErrors
    {
        get
        {
            _ = All;
            lock (Gate)
            {
                return DiscoveryFailures.ToList();
            }
        }
    }

    /// <summary>Resolves one fixture by name; throws a descriptive error when absent.</summary>
    public static IProtocolUnderTest Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (IProtocolUnderTest fixture in All)
        {
            if (string.Equals(fixture.Name, name, StringComparison.Ordinal))
            {
                return fixture;
            }
        }

        throw new InvalidOperationException(
            $"No protocol fixture named '{name}' is registered. Known fixtures: {string.Join(", ", Names)}.");
    }

    private static void EnsureUniqueNames(IReadOnlyList<IProtocolUnderTest> fixtures)
    {
        var duplicate = fixtures
            .GroupBy(fixture => fixture.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate protocol fixture name '{duplicate.Key}'. Fixture names must be unique so test cases stay addressable.");
        }
    }

    private static IReadOnlyList<IProtocolUnderTest> DiscoverFixtures()
    {
        Assembly self = typeof(ProtocolConformanceRegistry).Assembly;
        var fixtures = new List<IProtocolUnderTest>();
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || !IsCandidate(assembly, self))
            {
                continue;
            }

            foreach (Type type in GetLoadableTypes(assembly))
            {
                if (!typeof(IProtocolUnderTest).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                {
                    continue;
                }

                if (type.GetConstructor(Type.EmptyTypes) is null)
                {
                    continue;
                }

                try
                {
                    if (Activator.CreateInstance(type) is IProtocolUnderTest fixture)
                    {
                        fixtures.Add(fixture);
                    }
                    else
                    {
                        DiscoveryFailures.Add($"Fixture '{type.FullName}' could not be created.");
                    }
                }
                catch (Exception exception)
                {
                    DiscoveryFailures.Add($"Fixture '{type.FullName}' threw while being created: {exception.Message}");
                }
            }
        }

        return fixtures;
    }

    private static bool IsCandidate(Assembly assembly, Assembly self)
    {
        if (assembly == self)
        {
            return true;
        }

        string selfName = self.GetName().Name!;
        try
        {
            return assembly
                .GetReferencedAssemblies()
                .Any(reference => string.Equals(reference.Name, selfName, StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null).Select(type => type!);
        }
        catch (NotSupportedException)
        {
            return Array.Empty<Type>();
        }
    }
}
