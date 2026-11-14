using System.Reflection;
using System.Security.Cryptography;

namespace GattDiscoveryProbe.Analysis;

public static class SourceHashCatalog
{
    private const string Prefix = "GattDiscoveryProbe.Source.";

    public static IReadOnlyList<SourceHash> Compute()
    {
        Assembly assembly = typeof(SourceHashCatalog).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                using Stream stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException("An embedded source file could not be opened.");
                string file = name[Prefix.Length..].Replace('\\', '/');
                string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                return new SourceHash(file, hash);
            })
            .ToArray();
    }

    public static string? ComputeAssemblySha256()
    {
        string location = typeof(SourceHashCatalog).Assembly.Location;
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(location)) return null;
        using FileStream stream = File.OpenRead(location);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
