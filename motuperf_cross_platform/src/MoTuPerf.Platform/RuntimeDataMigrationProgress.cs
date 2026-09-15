using System;

namespace MoTuPerf.Platform
{
    public sealed class RuntimeDataMigrationProgress
    {
        public RuntimeDataMigrationProgress(long totalBytes, long processedBytes, int totalFiles, int processedFiles, int failedFiles, string stage = "复制")
        {
            TotalBytes = Math.Max(0, totalBytes);
            ProcessedBytes = Math.Max(0, Math.Min(TotalBytes, processedBytes));
            TotalFiles = Math.Max(0, totalFiles);
            ProcessedFiles = Math.Max(0, Math.Min(TotalFiles, processedFiles));
            FailedFiles = Math.Max(0, failedFiles);
            Stage = stage;
        }

        public long TotalBytes { get; private set; }
        public string Stage { get; private set; }
        public long ProcessedBytes { get; private set; }
        public int TotalFiles { get; private set; }
        public int ProcessedFiles { get; private set; }
        public int FailedFiles { get; private set; }
        public double Percent
        {
            get { return TotalBytes == 0 ? 100 : ProcessedBytes * 100d / TotalBytes; }
        }
    }
}
