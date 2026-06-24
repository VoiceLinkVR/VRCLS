using System;
using System.IO;

namespace VRCLS.Core.Models
{
    public sealed record ConfigSnapshotLogEntry(
        int Index,
        string Source,
        int LineNumber,
        DateTimeOffset? Timestamp,
        string? Reason,
        string? Algorithm,
        int? JsonBytes,
        int? CompressedBytes,
        string Base64,
        string RawLine)
    {
        public string SourceName
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Source))
                {
                    return string.Empty;
                }

                try
                {
                    var fileName = Path.GetFileName(Source);
                    return string.IsNullOrWhiteSpace(fileName) ? Source : fileName;
                }
                catch
                {
                    return Source;
                }
            }
        }
    }
}
