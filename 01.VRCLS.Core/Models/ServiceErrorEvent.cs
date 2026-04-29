using System;
using System.Collections.Generic;

namespace VRCLS.Core.Models
{
    public sealed class ServiceErrorEvent
    {
        public ServiceErrorCode Code { get; init; }

        public IReadOnlyDictionary<string, string>? Context { get; init; }

        public Exception? Exception { get; init; }

        public string? GetContext(string key)
        {
            if (Context == null || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            return Context.TryGetValue(key, out var value) ? value : null;
        }

        public long? GetContextInt64(string key)
        {
            var value = GetContext(key);
            return long.TryParse(value, out var result) ? result : null;
        }
    }
}
