using AppleDeviceHub.Core.Ble;
using Xunit;

namespace AppleDeviceHub.Core.Tests.Ble;

public class PairingInstructionsTests
{
    [Fact]
    public void Every_catalog_model_has_a_guide()
    {
        foreach (ushort id in AppleModelCatalog.ListedIds)
        {
            Assert.True(PairingInstructions.Find(id) is not null, $"No pairing guide for 0x{id:X4} ({AppleModelCatalog.NameOf(id)})");
        }
    }

    [Fact]
    public void Guides_only_name_catalog_models_and_none_twice()
    {
        var seen = new HashSet<ushort>();
        foreach (var guide in PairingInstructions.Guides)
        {
            foreach (ushort id in guide.ModelIds)
            {
                Assert.NotNull(AppleModelCatalog.Find(id));
                Assert.True(seen.Add(id), $"0x{id:X4} is in two guides");
            }
        }
    }

    [Fact]
    public void Each_guide_is_short_and_plain()
    {
        foreach (var guide in PairingInstructions.Guides)
        {
            Assert.False(string.IsNullOrWhiteSpace(guide.Title));
            Assert.False(string.IsNullOrWhiteSpace(guide.Label));
            Assert.InRange(guide.Steps.Count, 2, 4);
            Assert.All(guide.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s)));
        }
    }

    [Fact]
    public void Unlisted_and_missing_ids_do_not_claim_a_model()
    {
        Assert.Same(PairingInstructions.Fallback, PairingInstructions.ForModel(0xFFFF));
        Assert.Same(PairingInstructions.Guides[0], PairingInstructions.ForModel(null));
        Assert.True(PairingInstructions.Fallback.IsFallback);
    }

    [Fact]
    public void Looks_up_by_model_name()
    {
        Assert.Equal("airpods-double-tap", PairingInstructions.FindByModelName("AirPods Pro 3")?.Key);
        Assert.Equal("airpods-max", PairingInstructions.FindByModelName("airpods max (usb-c)")?.Key);
        Assert.Null(PairingInstructions.FindByModelName("Unrecognised Apple audio device"));
        Assert.Null(PairingInstructions.FindByModelName(null));
    }
}
