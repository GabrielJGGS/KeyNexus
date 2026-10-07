using KeyNexus.Core;

namespace KeyNexus.Tests;

public class DeviceIdentityParserTests
{
    private const string FreeWolfBle =
        @"\\?\HID#{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&0232c2_PID&1001_REV&0001_c4495def573b&Col05#b&2972d7bb&0&0004#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";

    [Fact]
    public void BluetoothLePath_ExtractsVidPidAndAddress()
    {
        var identity = DeviceIdentityParser.Parse(FreeWolfBle);

        Assert.Equal("32C2", identity.VendorId);
        Assert.Equal("1001", identity.ProductId);
        Assert.Equal(KeyboardBusType.BluetoothLeHid, identity.BusType);
        Assert.Equal("C4495DEF573B", identity.BluetoothAddress);
        Assert.Equal("C4:49:5D:EF:57:3B", DeviceIdentityParser.FormatBluetoothAddress(identity.BluetoothAddress!));
    }

    [Fact]
    public void UsbPath_ExtractsVidPid()
    {
        var identity = DeviceIdentityParser.Parse(
            @"\\?\HID#VID_320F&PID_227C&MI_00#a&1dd9831&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}");

        Assert.Equal("320F", identity.VendorId);
        Assert.Equal("227C", identity.ProductId);
        Assert.Equal(KeyboardBusType.UsbHid, identity.BusType);
    }

    [Fact]
    public void AcpiPath_IsTheBuiltInKeyboard()
    {
        var identity = DeviceIdentityParser.Parse(@"\\?\ACPI#ATK3001#4&18d228de&0#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}");

        Assert.Equal(KeyboardBusType.Acpi, identity.BusType);
    }

    [Theory]
    [InlineData(@"\\?\ROOT#FEIZHI_VIRTUAL_KEYBOARD#0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}")]
    [InlineData(@"\\?\HID#HID&Col05#2&2f3def6d&0&0004#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}")]
    public void SoftwareKeyboards_AreVirtual(string path)
    {
        Assert.Equal(KeyboardBusType.Virtual, DeviceIdentityParser.Parse(path).BusType);
    }

    [Fact]
    public void AllCollectionsOfTheSameKeyboard_ShareTheGroupKey()
    {
        string col01 = FreeWolfBle.Replace("&Col05#b&2972d7bb&0&0004", "&Col01#b&2972d7bb&0&0000");

        Assert.Equal(DeviceGrouping.GetGroupKey(FreeWolfBle), DeviceGrouping.GetGroupKey(col01));
    }
}
