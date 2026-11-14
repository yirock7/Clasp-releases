using AppleDeviceHub.Core.Connection;

namespace AppleDeviceHub.Core.Tests.Connection;

public sealed class BluetoothAddressParserTests
{
    [Theory]
    [InlineData(@"BTHENUM\DEV_001122334455\7&2a5d4c0&0&BLUETOOTHDEVICE_001122334455", 0x001122334455UL)]
    [InlineData(@"BTHENUM\DEV_001122334455", 0x001122334455UL)]
    [InlineData(@"BTHENUM\DEV_aabbccddeeff", 0xAABBCCDDEEFFUL)]
    [InlineData("Bluetooth#Bluetooth66:77:88:99:aa:bb-00:11:22:33:44:55", 0x001122334455UL)]
    [InlineData("Bluetooth#Bluetooth66:77:88:99:AA:BB-AA:BB:CC:DD:EE:FF", 0xAABBCCDDEEFFUL)]
    [InlineData("Bluetooth#Bluetooth66:77:88:99:aa:bb-aa:bb:cc:dd:ee:ff", 0xAABBCCDDEEFFUL)]
    [InlineData("BluetoothDevice#00:11:22:33:44:55", 0x001122334455UL)]
    public void Reads_the_paired_device_address_from_known_id_shapes(string id, ulong expected)
    {
        Assert.Equal(expected, BluetoothAddressParser.TryParse(id));
    }

    [Fact]
    public void With_two_addresses_the_last_one_is_the_paired_device_not_the_radio()
    {
        var value = BluetoothAddressParser.TryParse("Bluetooth#Bluetooth9c:b6:d0:11:22:33-aa:bb:cc:dd:ee:ff");
        Assert.Equal(0xAABBCCDDEEFFUL, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("Bluetooth#Bluetooth00:00:00:00:00:00-00:00:00:00:00:00")]
    [InlineData(@"BTHENUM\DEV_000000000000")]
    [InlineData("00:11:22:33:44")]
    [InlineData(@"BTHENUM\DEV_001122334455FF")]
    [InlineData("xx:yy:zz:dd:ee:ff")]
    public void Returns_null_when_there_is_no_usable_address(string? id)
    {
        Assert.Null(BluetoothAddressParser.TryParse(id));
    }
}
