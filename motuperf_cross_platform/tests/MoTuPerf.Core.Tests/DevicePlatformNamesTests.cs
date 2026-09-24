using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Core.Tests
{
    public sealed class DevicePlatformNamesTests
    {
        [Theory]
        [InlineData("harmony")]
        [InlineData("openharmony")]
        [InlineData("harmonyos")]
        [InlineData("HARMONYOS")]
        public void HarmonyAliasesShareDisplayAndExportSemantics(string platform)
        {
            Assert.True(DevicePlatformNames.IsHarmony(platform));
            Assert.Equal("鸿蒙 设备", new DeviceInfo { Platform = platform }.PickerLabel);

            AppInfo app = new AppInfo { Platform = platform };
            Assert.Equal("可尝试启动", app.LaunchAvailability);
        }

        [Fact]
        public void UnknownPlatformKeepsItsOriginalLabel()
        {
            Assert.False(DevicePlatformNames.IsHarmony("custom-os"));
            Assert.Equal("custom-os 设备", new DeviceInfo { Platform = "custom-os" }.PickerLabel);
        }
    }
}
