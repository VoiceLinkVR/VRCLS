using System;

namespace VRCLS.Core.Models
{
    /// <summary>
    /// Correlates recognition output with a captured speech segment.
    /// </summary>
    public sealed class RecognitionSegmentInfo
    {
        public long SegmentId { get; init; }

        /// <summary>
        /// Segment capture timestamp in UTC.
        /// </summary>
        public DateTime CapturedAtUtc { get; init; } = DateTime.MinValue;

        public static RecognitionSegmentInfo Empty { get; } = new();
    }
}
