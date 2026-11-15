using AppleDeviceHub.Core.Connection;

namespace AppleDeviceHub.Core.Tests.Connection;

public sealed class ConnectionDriverStateTests
{
    [Theory]
    [InlineData(0x4u, true)]
    [InlineData(0x5u, true)]
    [InlineData(0xCu, true)]
    [InlineData(0x1u, false)]
    [InlineData(0x2u, false)]
    [InlineData(0x8u, false)]
    [InlineData(0x0u, false)]
    public void Endpoint_not_present_is_the_notpresent_bit_only(uint state, bool expected)
        => Assert.Equal(expected, ConnectionDriverState.IsEndpointNotPresent(state));

    [Fact]
    public void Missing_interface_matches_only_the_no_such_device_interface_hresult()
    {
        Assert.True(ConnectionDriverState.IsMissingInterface(unchecked((int)0xE0000225)));
        Assert.False(ConnectionDriverState.IsMissingInterface(unchecked((int)0x80004005)));
        Assert.False(ConnectionDriverState.IsMissingInterface(0));
    }
}
