using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CSharpIosPerfMonitor;

namespace MoTuPerf.Desktop
{
    // The backing list is append-only. A new session must allocate a new list.
    internal sealed class SampleSnapshot : IReadOnlyList<PerfSample>
    {
        private readonly List<PerfSample> _source;
        public int Count { get; }
        public PerfSample this[int index] => index >= 0 && index < Count ? _source[index] : throw new ArgumentOutOfRangeException(nameof(index));
        public IReadOnlyList<PerfSample> Ordered { get; }

        public SampleSnapshot(List<PerfSample> source, SampleSnapshot previous = null)
        {
            _source = source;
            Count = source.Count;
            bool reuse = previous != null && ReferenceEquals(previous._source, source) && previous.Count <= Count;
            int start = reuse ? previous.Count : 0;
            bool ordered = !reuse || ReferenceEquals(previous.Ordered, previous);
            for (int i = Math.Max(1, start); ordered && i < Count; i++)
                ordered = source[i - 1].ElapsedSec <= source[i].ElapsedSec;
            if (ordered) { Ordered = this; return; }

            PerfSample[] added = source.Skip(start).OrderBy(sample => sample.ElapsedSec).ToArray();
            IReadOnlyList<PerfSample> old = reuse ? previous.Ordered : Array.Empty<PerfSample>();
            PerfSample[] merged = new PerfSample[Count];
            int left = 0, right = 0, next = 0;
            while (left < old.Count || right < added.Length)
                merged[next++] = right >= added.Length || (left < old.Count && old[left].ElapsedSec <= added[right].ElapsedSec)
                    ? old[left++] : added[right++];
            Ordered = merged;
        }

        public IEnumerator<PerfSample> GetEnumerator() { for (int i = 0; i < Count; i++) yield return _source[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        internal static int UpperBound(IReadOnlyList<PerfSample> samples, double time)
        {
            int left = 0, right = samples.Count;
            while (left < right)
            {
                int mid = left + (right - left) / 2;
                if (samples[mid].ElapsedSec <= time) left = mid + 1; else right = mid;
            }
            return left;
        }

        internal static PerfSample Nearest(IReadOnlyList<PerfSample> samples, double time, Func<PerfSample, bool> predicate, double distance)
        {
            int right = UpperBound(samples, time), left = right - 1;
            while (left >= 0 || right < samples.Count)
            {
                double before = left >= 0 ? Math.Abs(samples[left].ElapsedSec - time) : double.PositiveInfinity;
                double after = right < samples.Count ? Math.Abs(samples[right].ElapsedSec - time) : double.PositiveInfinity;
                if (Math.Min(before, after) > distance) return null;
                PerfSample item = before <= after ? samples[left--] : samples[right++];
                if (predicate(item)) return item;
            }
            return null;
        }
    }
}
