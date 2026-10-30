using System;
using System.IO;

namespace MoTuPerf.Platform
{
    internal sealed class RetryableRuntimePath
    {
        private readonly object _lock = new object();
        private readonly Func<string> _resolve;
        private readonly Func<string, bool> _isValid;
        private string _cachedPath;

        internal RetryableRuntimePath(Func<string> resolve, Func<string, bool> isValid)
        {
            _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
            _isValid = isValid ?? File.Exists;
        }

        internal string Value
        {
            get
            {
                lock (_lock)
                {
                    if (!string.IsNullOrWhiteSpace(_cachedPath) && _isValid(_cachedPath))
                    {
                        return _cachedPath;
                    }

                    // Exceptions deliberately do not populate the cache. A profile,
                    // volume, or launcher availability problem may be temporary.
                    _cachedPath = _resolve();
                    return _cachedPath ?? "";
                }
            }
        }
    }
}
