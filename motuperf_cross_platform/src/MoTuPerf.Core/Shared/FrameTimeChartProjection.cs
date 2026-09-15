namespace CSharpIosPerfMonitor
{
    internal static class FrameTimeChartProjection
    {
        internal static bool IsChartSample(PerfSample sample)
        {
            return sample != null && sample.HasFrameTimeMax && sample.FpsUpdated;
        }
    }
}
