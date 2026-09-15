using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class IosDeviceDetailsTests
    {
        [Fact]
        public void DeviceDetailsPopulateMarketCpuAndPhysicalResolution()
        {
            DeviceInfo device = new DeviceInfo
            {
                Name = "",
                MarketName = "",
                Platform = "ios"
            };

            IosLookupService.ApplyDeviceDetails(device,
                "{\"device_name\":\"dy\\u7684iPad\",\"market_name\":\"iPad mini (6th gen, WiFi)\",\"product_type\":\"iPad14,1\",\"hardware_platform\":\"t8110\",\"cpu_architecture\":\"arm64e\",\"resolution\":\"2266x1488\"}");

            Assert.Equal("iPad mini (6th gen, WiFi)", device.MarketName);
            Assert.Equal("dy的iPad", device.Name);
            Assert.Equal("Apple A15 (t8110)", device.CpuInfo);
            Assert.Equal("2266x1488", device.Resolution);
        }

        [Fact]
        public void UnknownChipRetainsRealHardwareIdentityWithoutGuessing()
        {
            DeviceInfo device = new DeviceInfo();

            IosLookupService.ApplyDeviceDetails(device,
                "{\"product_type\":\"iPhone99,1\",\"hardware_platform\":\"t9999\",\"cpu_architecture\":\"arm64e\",\"screen_width\":1179,\"screen_height\":2556}");

            Assert.Equal("Apple SoC (t9999, arm64e)", device.CpuInfo);
            Assert.Equal("2556x1179", device.Resolution);
            Assert.Equal("iPhone99,1", device.Name);
        }

        [Fact]
        public void MalformedDetailsDoNotEraseListDiscovery()
        {
            DeviceInfo device = new DeviceInfo
            {
                Name = "Existing device",
                MarketName = "Existing market"
            };

            IosLookupService.ApplyDeviceDetails(device, "not-json");

            Assert.Equal("Existing device", device.Name);
            Assert.Equal("Existing market", device.MarketName);
            Assert.Empty(device.CpuInfo);
            Assert.Empty(device.Resolution);
        }
    }
}
