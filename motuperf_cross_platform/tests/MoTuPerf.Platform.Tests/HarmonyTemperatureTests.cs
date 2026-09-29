using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyTemperatureTests
    {
        [Fact]
        public void ManagerTemperatureSurvivesArchiveAndCsvWithoutInventingThermalState()
        {
            using var collector = new PerfCollector();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(PerfCollector).GetField("_captureGeneration", flags).SetValue(collector, 1);
            MethodInfo parse = typeof(PerfCollector).GetMethod("ParseLine", flags);
            parse.Invoke(collector, new object[] {
                "temperature {\"platform\":\"harmony\",\"values\":{\"soc (Thermal Manager)\":42.5}," +
                "\"unit\":\"C\",\"source\":\"hdc-hidumper-3303-temperature\",\"scope\":\"device\"}", 1, "harmony" });
            parse.Invoke(collector, new object[] {
                "thermal_state {\"platform\":\"harmony\",\"value\":3,\"state\":\"level-3\"," +
                "\"source\":\"hdc-hidumper-3303-level\",\"scope\":\"device\"}", 1, "harmony" });
            var queue = (Queue<PerfSample>)typeof(PerfCollector).GetField("_pendingProcessSamples", flags).GetValue(collector);
            PerfSample sample = Assert.Single(queue);
            Assert.True(sample.HasTemperature && sample.TemperatureUpdated);
            Assert.False(sample.HasThermalState);
            Assert.Equal(42.5, sample.TemperatureCelsius["soc (Thermal Manager)"]);
            string directory = Directory.CreateTempSubdirectory("motuperf-temperature-").FullName;
            try
            {
                var document = new SessionDocument
                {
                    Format = "motuperf-session", Version = 5,
                    Device = new DeviceInfo { Platform = "harmony", Udid = "test-hdc" }
                };
                document.Samples.Add(sample);
                string archive = Path.Combine(directory, "temperature.motuperf");
                SessionArchiveService.Save(archive, document);
                var loaded = SessionArchiveService.Load(archive, Path.Combine(directory, "opened"));
                var saved = Assert.Single(loaded.Samples);
                Assert.Equal(42.5, saved.TemperatureCelsius["soc (Thermal Manager)"]);
                Assert.Equal("hdc-hidumper-3303-temperature", saved.TemperatureSource);
                Assert.Equal("device", saved.TemperatureScope);
                Assert.False(saved.HasThermalState);
                string csv = CsvExportService.Build(loaded);
                Assert.Contains("hdc-hidumper-3303-temperature", csv);
                Assert.Contains("soc (Thermal Manager)", csv);
                Assert.Contains("42.5", csv);
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
