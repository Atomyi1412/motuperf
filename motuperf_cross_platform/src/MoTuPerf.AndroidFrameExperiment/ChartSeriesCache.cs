using System;
using System.Collections.Generic;
using System.Linq;
using CSharpIosPerfMonitor;

namespace MoTuPerf.Desktop
{
    // Display-only index. Samples remain owned by the capture/session buffer.
    internal sealed class ChartSeriesCache
    {
        private readonly Func<PerfSample, double> _value;
        private readonly double _maxGap;
        private readonly bool _frames;
        private readonly bool _measuredGap;
        private readonly List<PerfSample> _samples = new List<PerfSample>();
        private readonly List<int> _breaks = new List<int>();
        private int _capacity = 1;
        private (int Min, int Max)[] _tree = { (-1, -1), (-1, -1) };
        public IReadOnlyList<PerfSample> Samples => _samples;
        public double Min => _samples.Count == 0 ? double.NaN : _value(_samples[_tree[1].Min]);
        public double Max => _samples.Count == 0 ? double.NaN : _value(_samples[_tree[1].Max]);
        internal int QueryCount { get; private set; }
        public bool DenseDiscontinuities { get; private set; }

        public ChartSeriesCache(Func<PerfSample, double> value, double maxGap, bool frames = false, bool measuredGap = false)
        { _value = value; _maxGap = maxGap; _frames = frames; _measuredGap = measuredGap; }

        public void Add(PerfSample sample)
        {
            double value = _value(sample);
            if (!double.IsFinite(value) || !double.IsFinite(sample.ElapsedSec)) return;
            if (_samples.Count > 0 && sample.ElapsedSec < _samples[_samples.Count - 1].ElapsedSec)
            {
                _samples.Insert(SampleSnapshot.UpperBound(_samples, sample.ElapsedSec), sample);
                Rebuild();
                return;
            }
            int index = _samples.Count;
            if (index > 0 && BreakBetween(_samples[index - 1], sample)) _breaks.Add(index);
            _samples.Add(sample);
            if (_samples.Count > _capacity) { Rebuild(); return; }
            int node = _capacity + index;
            _tree[node] = (index, index);
            while ((node /= 2) > 0) _tree[node] = Merge(_tree[node * 2], _tree[node * 2 + 1]);
        }

        private void Rebuild()
        {
            while (_capacity < _samples.Count) _capacity *= 2;
            _tree = Enumerable.Repeat((-1, -1), _capacity * 2).ToArray();
            _breaks.Clear();
            for (int i = 0; i < _samples.Count; i++)
            {
                _tree[_capacity + i] = (i, i);
                if (i > 0 && BreakBetween(_samples[i - 1], _samples[i])) _breaks.Add(i);
            }
            for (int i = _capacity - 1; i > 0; i--) _tree[i] = Merge(_tree[i * 2], _tree[i * 2 + 1]);
        }

        private bool BreakBetween(PerfSample before, PerfSample after)
        {
            // Sampled frame counts stay visually continuous across short provenance resets. Exact
            // ordered-frame metrics still split because their interval crosses incompatible sources.
            if (_frames && !_measuredGap
                && (after.FrameSourceSequenceDiscontinuity || !MetricStatistics.IsSameFrameSeries(before, after))) return true;
            if (_frames && !_measuredGap && before.HasFrameSourceSequence && after.HasFrameSourceSequence
                && after.FrameSourceSequence != before.FrameSourceSequence
                && (before.FrameSourceSequence == long.MaxValue || after.FrameSourceSequence != before.FrameSourceSequence + 1)) return true;
            if (after.ElapsedSec - before.ElapsedSec <= _maxGap) return false;
            // A long interval is measured only if the actual observation covers it.
            return !(_measuredGap && MetricStatistics.IsSameFrameSeries(before, after)
                && after.HasFrameObservation && after.FrameObservationMs > 0
                && after.FrameObservationMs / 1000.0 >= after.ElapsedSec - before.ElapsedSec - 0.1);
        }

        private (int Min, int Max) Merge((int Min, int Max) a, (int Min, int Max) b)
        {
            if (a.Min < 0) return b;
            if (b.Min < 0) return a;
            return (_value(_samples[a.Min]) <= _value(_samples[b.Min]) ? a.Min : b.Min,
                _value(_samples[a.Max]) >= _value(_samples[b.Max]) ? a.Max : b.Max);
        }

        private (int Min, int Max) Query(int start, int end)
        {
            QueryCount++;
            var result = (-1, -1);
            for (int left = start + _capacity, right = end + _capacity; left < right; left /= 2, right /= 2)
            {
                if ((left & 1) != 0) result = Merge(result, _tree[left++]);
                if ((right & 1) != 0) result = Merge(result, _tree[--right]);
            }
            return result;
        }

        internal static int LowerBound(IReadOnlyList<PerfSample> samples, double time)
        {
            int left = 0, right = samples.Count;
            while (left < right)
            {
                int mid = left + (right - left) / 2;
                if (samples[mid].ElapsedSec < time) left = mid + 1; else right = mid;
            }
            return left;
        }

        public IEnumerable<double> Discontinuities(double start, double end, int maxMarkers = int.MaxValue)
        {
            int index = _breaks.BinarySearch(LowerBound(_samples, start));
            if (index < 0) index = ~index;
            while (index < _breaks.Count)
            {
                double time = _samples[_breaks[index]].ElapsedSec;
                if (time > end) yield break;
                yield return time;
                if (maxMarkers < int.MaxValue && maxMarkers > 0 && end > start)
                {
                    double nextTime = start + (Math.Floor((time - start) / (end - start) * maxMarkers) + 1) * (end - start) / maxMarkers;
                    int next = _breaks.BinarySearch(LowerBound(_samples, nextTime));
                    index = Math.Max(index + 1, next < 0 ? ~next : next);
                }
                else index++;
            }
        }

        public IReadOnlyList<IReadOnlyList<PerfSample>> Project(double start, double end, int pixels)
        {
            var result = new List<IReadOnlyList<PerfSample>>();
            DenseDiscontinuities = false;
            if (_samples.Count == 0 || end <= start) return result;
            int left = Math.Max(0, LowerBound(_samples, start) - 1);
            int right = Math.Min(_samples.Count, SampleSnapshot.UpperBound(_samples, end) + 1);
            int b = _breaks.BinarySearch(left + 1);
            if (b < 0) b = ~b;
            int lastBreak = _breaks.BinarySearch(right);
            if (lastBreak < 0) lastBreak = ~lastBreak;
            if (lastBreak - b > Math.Max(1, pixels) * 2)
            {
                // Too many disconnected fragments to distinguish on screen. Keep real extrema
                // as isolated points; never invent connections between different sources.
                DenseDiscontinuities = true;
                foreach (var sample in ProjectSegment(left, right, start, end, Math.Max(1, pixels)))
                    result.Add(new[] { sample });
                return result;
            }
            while (left < right)
            {
                int limit = b < _breaks.Count ? Math.Min(right, _breaks[b]) : right;
                if (_samples[limit - 1].ElapsedSec >= start && _samples[left].ElapsedSec <= end)
                    result.Add(ProjectSegment(left, limit, start, end, Math.Max(1, pixels)));
                left = limit;
                b++;
            }
            return result;
        }

        private IReadOnlyList<PerfSample> ProjectSegment(int left, int right, double start, double end, int pixels)
        {
            var indices = new SortedSet<int> { left, right - 1 };
            int firstBucket = Math.Max(0, Math.Min(pixels - 1, (int)((_samples[left].ElapsedSec - start) / (end - start) * pixels)));
            int lastBucket = Math.Max(0, Math.Min(pixels - 1, (int)((_samples[right - 1].ElapsedSec - start) / (end - start) * pixels)));
            for (int bucket = firstBucket; bucket <= lastBucket; bucket++)
            {
                int from = Math.Max(left, LowerBound(_samples, start + (end - start) * bucket / pixels));
                int to = Math.Min(right, bucket == pixels - 1 ? SampleSnapshot.UpperBound(_samples, end)
                    : LowerBound(_samples, start + (end - start) * (bucket + 1) / pixels));
                if (to <= from) continue;
                // Keep the real point sequence in sparse regions; retain extrema in dense ones.
                if (to - from <= 4) { for (int i = from; i < to; i++) indices.Add(i); }
                else
                {
                    var extrema = Query(from, to);
                    indices.Add(from); indices.Add(extrema.Min); indices.Add(extrema.Max); indices.Add(to - 1);
                }
            }
            return indices.Select(i => _samples[i]).ToList();
        }
    }

    internal sealed class ChartDataCache
    {
        private const double ContinuousSampleGap = double.PositiveInfinity;
        private IReadOnlyList<PerfSample> _source;
        private int _processed;
        private string _mode;
        private readonly Dictionary<string, ChartSeriesCache> _series = new Dictionary<string, ChartSeriesCache>(StringComparer.OrdinalIgnoreCase);
        private readonly List<PerfSample> _memoryKinds = new List<PerfSample>();
        public IEnumerable<string> Keys => _series.Keys;
        public string PreferredMemory
        {
            get
            {
                string metric = MetricStatistics.PreferredMemoryMetric(_memoryKinds);
                return metric == "footprint" ? "physical_footprint" : metric;
            }
        }
        internal int InspectedSamples { get; private set; }

        public ChartSeriesCache Get(string key) => _series.TryGetValue(key, out var value) ? value : null;

        public void Update(IReadOnlyList<PerfSample> source, string mode)
        {
            if (ReferenceEquals(source, _source) && _mode == mode) return;
            bool append = _mode == mode && source is SampleSnapshot next && next.Extends(_source as SampleSnapshot);
            if (!append) { _series.Clear(); _memoryKinds.Clear(); _processed = 0; }
            _mode = mode;
            for (int i = _processed; i < source.Count; i++)
            {
                var s = source[i];
                InspectedSamples++;
                if (s == null) continue;
                switch (mode)
                {
                    case "fps":
                        // State events carry the last FPS but may have a different event Source.
                        // Keep the live curve attached to the current timeline while the value is still fresh.
                        // This is display-only carry-forward; statistics and exports continue to require FpsUpdated.
                        if (s.HasFreshnessMetadata && !s.FpsUpdated)
                        {
                            if (s.HasFps) Add("FPS", s, x => x.Fps, ContinuousSampleGap, true, true);
                            break;
                        }
                        // Sampled frame counts use a continuous display axis like Gears/PerfDog.
                        // Slow polls and short source resets stay connected; raw samples are not filled.
                        if (s.HasFps) Add("FPS", s, x => x.Fps, ContinuousSampleGap, true, true);
                        if (s.HasJank)
                        {
                            Add("Jank", s, x => x.Jank, ContinuousSampleGap, true, true);
                            Add("BigJank", s, x => x.BigJank, ContinuousSampleGap, true, true);
                        }
                        break;
                    case "frametime": if (FrameTimeChartProjection.IsChartSample(s)) Add("FrameTime", s, x => x.FrameTimeMaxMs, 3, true); break;
                    case "cpu": if (s.HasCpu) Add("CPU", s, x => x.CpuPercent, 7); break;
                    case "cpunormalized": if (s.HasCpuNormalized) Add("CPU", s, x => x.CpuNormalizedPercent, 7); break;
                    case "memory":
                        if (s.HasMemory && s.MemoryMb > 0)
                        {
                            string metric = (s.MemoryMetric ?? "").Trim().ToLowerInvariant();
                            if (metric == "footprint") metric = "physical_footprint";
                            Add(metric, s, x => x.MemoryMb, 7);
                            if (s.MemoryUpdated && !_memoryKinds.Any(x => string.Equals(x.MemoryMetric, s.MemoryMetric, StringComparison.OrdinalIgnoreCase))) _memoryKinds.Add(s);
                        }
                        break;
                    case "corecpu":
                        if (s.HasCpuCoreUsage && s.CpuCorePercents != null)
                            for (int core = 0; core < s.CpuCorePercents.Count; core++)
                            { int c = core; Add(PerformanceChartControl.CoreSeriesName(core), s, x => x.CpuCorePercents[c], 7); }
                        break;
                    case "temperature":
                        if (s.HasTemperature && s.TemperatureCelsius != null)
                            foreach (var pair in s.TemperatureCelsius)
                            {
                                string sensor = pair.Key;
                                if (pair.Value >= -20 && pair.Value <= 120) Add(sensor, s, x => TemperatureValue(x, sensor), 12);
                            }
                        break;
                    case "thermalstate": if (s.HasThermalState && s.ThermalStateLevel >= 0 && s.ThermalStateLevel <= 6) Add("Thermal", s, x => x.ThermalStateLevel, 7); break;
                }
            }
            _processed = source.Count;
            _source = source;
        }

        private void Add(string key, PerfSample sample, Func<PerfSample, double> value, double gap, bool frames = false, bool measuredGap = false)
        {
            if (!_series.TryGetValue(key, out var series)) _series[key] = series = new ChartSeriesCache(value, gap, frames, measuredGap);
            series.Add(sample);
        }

        internal static double TemperatureValue(PerfSample sample, string sensor)
        {
            if (sample.TemperatureCelsius.TryGetValue(sensor, out double value)) return value;
            foreach (var pair in sample.TemperatureCelsius)
                if (string.Equals(pair.Key, sensor, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            return double.NaN;
        }
    }
}
