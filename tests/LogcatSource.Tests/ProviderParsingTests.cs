using LogAce.Plugins.Logcat;
using Xunit;

public sealed class ProviderParsingTests
{

    [Fact]
    public void AdbDeviceCatalog_parses_devices_l_output()
    {
        const string output =
            "List of devices attached\n" +
            "emulator-5554          device product:sdk_gphone64 model:Pixel_7 device:emu64a transport_id:1\n" +
            "R5CT1234567            unauthorized usb:1-1\n";

        List<AdbDeviceListEntry> devices = AdbDeviceCatalog.ParseDevices(output).ToList();

        Assert.Equal(2, devices.Count);

        AdbDeviceListEntry emulator = devices.Single(device => device.Serial == "emulator-5554");
        Assert.Equal("device", emulator.State);
        Assert.Equal("Pixel_7", emulator.Model);
        Assert.Equal("1", emulator.TransportId);
        Assert.True(emulator.IsOnline);

        AdbDeviceListEntry phone = devices.Single(device => device.Serial == "R5CT1234567");
        Assert.Equal("unauthorized", phone.State);
        Assert.False(phone.IsOnline);
    }
}
