using System;
using System.Collections.Generic;
using CSharpIosPerfMonitor;

namespace MoTuPerf.Desktop
{
    // Generation is checked both when receiving work and when executing UI callbacks.
    internal sealed class CaptureUiBuffer
    {
        private readonly object _gate = new object();
        private readonly Queue<PerfSample> _samples = new Queue<PerfSample>();
        private long _generation;
        private bool _open;

        public long Begin()
        {
            lock (_gate) { _samples.Clear(); _open = true; return ++_generation; }
        }

        public bool IsCurrent(long generation)
        {
            lock (_gate) return _open && generation == _generation;
        }

        public bool Enqueue(long generation, PerfSample sample)
        {
            lock (_gate)
            {
                if (!_open || generation != _generation || sample == null) return false;
                _samples.Enqueue(sample);
                return true;
            }
        }

        public IReadOnlyList<PerfSample> Drain(long generation, int maximum = 512, bool close = false)
        {
            lock (_gate)
            {
                if (generation != _generation) return Array.Empty<PerfSample>();
                if (close) _open = false;
                List<PerfSample> batch = new List<PerfSample>(Math.Min(maximum, _samples.Count));
                while (batch.Count < maximum && _samples.Count > 0) batch.Add(_samples.Dequeue());
                return batch;
            }
        }
    }
}
