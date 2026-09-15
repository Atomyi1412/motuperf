using System;

namespace MoTuPerf.Platform
{
    public sealed class RuntimeDataCleanupProgress
    {
        public RuntimeDataCleanupProgress(long totalBytes, long processedBytes, long deletedBytes, int totalFiles, int processedFiles, int failedFiles)
        {
            TotalBytes = Math.Max(0, totalBytes);
            ProcessedBytes = Math.Max(0, Math.Min(TotalBytes, processedBytes));
            DeletedBytes = Math.Max(0, Math.Min(ProcessedBytes, deletedBytes));
            TotalFiles = Math.Max(0, totalFiles);
            ProcessedFiles = Math.Max(0, Math.Min(TotalFiles, processedFiles));
            FailedFiles = Math.Max(0, failedFiles);
        }

        public long TotalBytes { get; private set; }
        public long ProcessedBytes { get; private set; }
        public long DeletedBytes { get; private set; }
        public int TotalFiles { get; private set; }
        public int ProcessedFiles { get; private set; }
        public int FailedFiles { get; private set; }
        public double Percent { get { return TotalBytes == 0 ? 100 : ProcessedBytes * 100d / TotalBytes; } }
    }
}
