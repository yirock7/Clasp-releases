// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using AppleDeviceHub.Core.Artwork;

namespace AppleDeviceHub.App.Views;

/// <summary>
/// Loads the per-model drawings (loose SVG files under Assets\Artwork next to the exe) once and keeps them.
/// A drawing that is missing or does not parse is remembered as null, so it is not retried on every call and
/// the caller falls back to the older drawing.
/// </summary>
internal static class ArtworkStore
{
    private static readonly Dictionary<string, ArtworkDocument?> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static ArtworkDocument? Get(string key)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            ArtworkDocument? document = null;
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Artwork", ArtworkCatalog.FileNameFor(key));
                document = ArtworkParser.Parse(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Artwork '{key}' could not be loaded: {ex.Message}");
            }

            Cache[key] = document;
            return document;
        }
    }
}
