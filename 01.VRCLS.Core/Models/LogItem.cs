using System;

namespace VRCLS.Core.Models
{
    public class LogItem
    {
        public DateTime Timestamp { get; set; }
        public string Source { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string Level { get; set; } = "Info";

        public LogItem() { }

        public LogItem(string source, string text, string level = "Info")
        {
            Timestamp = DateTime.Now;
            Source = source;
            Text = text;
            Level = level;
        }
    }
}
