// SPDX-License-Identifier: GPL-3.0-or-later

using AppleDeviceHub.Core.Artwork;

namespace AppleDeviceHub.Core.Tests.Artwork;

public sealed class ArtworkCatalogTests
{
    [Theory]
    [InlineData((ushort)0x0E20)]
    [InlineData((ushort)0x1420)]
    [InlineData((ushort)0x2420)]
    [InlineData((ushort)0x2720)]
    public void AirPods_Pro_models_use_the_airpods_pro_drawing(ushort id)
    {
        Assert.Equal("airpods-pro", ArtworkCatalog.DrawingKeyFor(id));
    }

    [Theory]
    [InlineData((ushort)0x0220, "airpods")]
    [InlineData((ushort)0x0F20, "airpods")]
    [InlineData((ushort)0x1320, "airpods-3")]
    [InlineData((ushort)0x1920, "airpods-4")]
    [InlineData((ushort)0x1B20, "airpods-4")]
    [InlineData((ushort)0x3620, "airpods-4")]
    [InlineData((ushort)0x3020, "airpods-4")]
    [InlineData((ushort)0x0A20, "airpods-max")]
    [InlineData((ushort)0x1F20, "airpods-max")]
    [InlineData((ushort)0x2D20, "airpods-max")]
    [InlineData((ushort)0x1D20, "powerbeats-pro-2")]
    [InlineData((ushort)0x1620, "beats-studio-buds-plus")]
    [InlineData((ushort)0x2520, "beats-solo-4")]
    [InlineData((ushort)0x0B20, "powerbeats-pro")]
    [InlineData((ushort)0x2F20, "powerbeats-fit")]
    [InlineData((ushort)0x1220, "beats-fit-pro")]
    [InlineData((ushort)0x1120, "beats-studio-buds")]
    [InlineData((ushort)0x2620, "beats-solo-buds")]
    [InlineData((ushort)0x0620, "beats-solo-3")]
    [InlineData((ushort)0x0C20, "beats-solo-pro")]
    [InlineData((ushort)0x0920, "beats-studio-3")]
    [InlineData((ushort)0x1720, "beats-studio-pro")]
    [InlineData((ushort)0x0320, "powerbeats-3")]
    [InlineData((ushort)0x0D20, "powerbeats-4")]
    [InlineData((ushort)0x0520, "beatsx")]
    [InlineData((ushort)0x1020, "beats-flex")]
    public void Other_models_use_their_own_drawing(ushort id, string key)
    {
        Assert.Equal(key, ArtworkCatalog.DrawingKeyFor(id));
    }

    [Theory]
    [InlineData((ushort)0x0000)]
    [InlineData((ushort)0xFFFF)]
    [InlineData((ushort)0x3820)]
    public void Unmapped_models_have_no_per_model_drawing(ushort id)
    {
        Assert.Null(ArtworkCatalog.DrawingKeyFor(id));
    }

    [Fact]
    public void Unknown_model_has_no_drawing()
    {
        Assert.Null(ArtworkCatalog.DrawingKeyFor(null));
    }

    [Fact]
    public void File_name_is_the_key_plus_svg()
    {
        Assert.Equal("airpods-pro.svg", ArtworkCatalog.FileNameFor("airpods-pro"));
        Assert.Contains("airpods-pro", ArtworkCatalog.Keys);
        Assert.Equal(21, ArtworkCatalog.Keys.Count);
    }
}

public sealed class ShippedArtworkTests
{
    private static readonly HashSet<string> CaseDrawings =
    [
        "airpods", "airpods-3", "airpods-4", "airpods-pro", "powerbeats-pro", "powerbeats-pro-2", "powerbeats-fit",
        "beats-fit-pro", "beats-studio-buds", "beats-studio-buds-plus", "beats-solo-buds",
    ];

    private static string ArtworkFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "windows", "hub")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var folder = Path.Combine(dir.FullName, "windows", "hub", "src", "AppleDeviceHub.App", "Assets", "Artwork");
        Assert.True(Directory.Exists(folder), folder);
        return folder;
    }

    [Fact]
    public void Every_catalog_key_has_a_file_that_parses()
    {
        var folder = ArtworkFolder();
        foreach (var key in ArtworkCatalog.Keys)
        {
            var path = Path.Combine(folder, ArtworkCatalog.FileNameFor(key));
            Assert.True(File.Exists(path), path);
            var doc = ArtworkParser.Parse(File.ReadAllText(path));
            if (CaseDrawings.Contains(key))
            {
                Assert.NotNull(doc.Lid);
            }
            else
            {
                Assert.Null(doc.Lid);
            }

            if (key == "airpods-pro")
            {
                Assert.Contains("white", doc.Colorways.Select(c => c.Id));
            }
        }
    }

    [Fact]
    public void Every_drawing_declares_its_default_look_as_the_first_colorway()
    {
        var folder = ArtworkFolder();
        foreach (var key in ArtworkCatalog.Keys)
        {
            var doc = ArtworkParser.Parse(File.ReadAllText(Path.Combine(folder, ArtworkCatalog.FileNameFor(key))));
            Assert.True(doc.Colorways.Count >= 1, $"{key} declares no colourway");
            Assert.True(doc.Colorways[0].Overrides.Count == 0, $"{key}: the first colourway '{doc.Colorways[0].Id}' must be the default look (no overrides)");
        }
    }

    [Fact]
    public void Every_case_drawing_has_both_bud_parts()
    {
        var folder = ArtworkFolder();
        foreach (var key in CaseDrawings)
        {
            var text = File.ReadAllText(Path.Combine(folder, ArtworkCatalog.FileNameFor(key)));
            Assert.Contains("data-part=\"bud-left\"", text);
            Assert.Contains("data-part=\"bud-right\"", text);
        }
    }

    [Fact]
    public void Every_listed_model_except_beats_360_has_a_drawing()
    {
        foreach (var id in AppleDeviceHub.Core.Ble.AppleModelCatalog.ListedIds)
        {
            var key = ArtworkCatalog.DrawingKeyFor(id);
            if (id == 0x3820)
            {
                Assert.Null(key);
            }
            else
            {
                Assert.True(key is not null && ArtworkCatalog.Keys.Contains(key), $"0x{id:X4} has no drawing");
            }
        }
    }

    [Fact]
    public void Every_svg_in_the_folder_parses()
    {
        var files = Directory.GetFiles(ArtworkFolder(), "*.svg");
        Assert.NotEmpty(files);
        var failures = new List<string>();
        foreach (var file in files)
        {
            var ex = Record.Exception(() => ArtworkParser.Parse(File.ReadAllText(file)));
            if (ex is not null)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void The_airpods_pro_drawing_exposes_every_animated_part()
    {
        var doc = ArtworkParser.Parse(File.ReadAllText(Path.Combine(ArtworkFolder(), "airpods-pro.svg")));
        var parts = new HashSet<ArtPart>();
        void Walk(IReadOnlyList<ArtNode> nodes)
        {
            foreach (var n in nodes)
            {
                switch (n)
                {
                    case ArtGroup g:
                        if (g.Part is { } p)
                        {
                            parts.Add(p);
                        }

                        Walk(g.Children);
                        break;
                    case ArtLidNode:
                        parts.Add(ArtPart.Lid);
                        break;
                }
            }
        }

        Walk(doc.Nodes);
        Assert.Equal(Enum.GetValues<ArtPart>().Length, parts.Count);
    }
}
