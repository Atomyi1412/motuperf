using System;
using System.ComponentModel;
using System.IO;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class AndroidDiscoveryErrorTests
    {
        [Fact]
        public void WrappedToolFailuresKeepActionableRepairGuidance()
        {
            foreach (Exception cause in new Exception[] { new FileNotFoundException("adb"), new Win32Exception(193) })
            {
                string message = AndroidLookupService.DescribeDiscoveryException(new InvalidOperationException("launch", cause));
                Assert.Contains("ADB", message);
                Assert.Contains("重新安装", message);
            }
            string timeout = AndroidLookupService.DescribeDiscoveryException(new InvalidOperationException("query", new TimeoutException()));
            Assert.Contains("超时", timeout);
        }
    }
}
