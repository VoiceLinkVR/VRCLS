using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Serilog.Core;
using Serilog.Events;

namespace VRCLS.UI.Services
{
    public sealed class RuntimeLogBuffer
    {
        private readonly object _sync = new();
        private readonly Queue<string> _lines = new();
        private readonly int? _maxLines;

        public RuntimeLogBuffer(int? maxLines = null)
        {
            if (maxLines.HasValue && maxLines.Value > 0)
            {
                _maxLines = Math.Max(100, maxLines.Value);
            }
        }

        public void Append(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (_sync)
            {
                _lines.Enqueue(line);
                while (_maxLines.HasValue && _lines.Count > _maxLines.Value)
                {
                    _lines.Dequeue();
                }
            }
        }

        public string GetSnapshot()
        {
            lock (_sync)
            {
                return string.Join(Environment.NewLine, _lines);
            }
        }
    }

    public sealed class RuntimeLogSink : ILogEventSink
    {
        private readonly RuntimeLogBuffer _buffer;

        public RuntimeLogSink(RuntimeLogBuffer buffer)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        }

        public void Emit(LogEvent logEvent)
        {
            if (logEvent == null)
            {
                return;
            }

            var timestamp = logEvent.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var message = logEvent.RenderMessage(CultureInfo.InvariantCulture);
            var sb = new StringBuilder()
                .Append('[')
                .Append(timestamp)
                .Append(' ')
                .Append(logEvent.Level)
                .Append("] ")
                .Append(message);

            if (logEvent.Exception != null)
            {
                sb.AppendLine()
                  .Append(logEvent.Exception);
            }

            _buffer.Append(sb.ToString());
        }
    }
}
