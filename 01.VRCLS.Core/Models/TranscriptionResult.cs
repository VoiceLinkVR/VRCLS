using System;

namespace VRCLS.Core.Models
{
    public class TranscriptionResult
    {
        /// <summary>
        /// Raw text (original). For backward compatibility.
        /// </summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// Original text (preferred).
        /// </summary>
        public string Original { get; set; } = string.Empty;

        /// <summary>
        /// Primary translated text.
        /// </summary>
        public string Translation { get; set; } = string.Empty;

        /// <summary>
        /// Optional secondary translation.
        /// </summary>
        public string Translation2 { get; set; } = string.Empty;

        /// <summary>
        /// Optional tertiary translation.
        /// </summary>
        public string Translation3 { get; set; } = string.Empty;

        public string SourceLanguage { get; set; } = string.Empty;
        public string TargetLanguage { get; set; } = string.Empty;
        public string TargetLanguage2 { get; set; } = string.Empty;
        public string TargetLanguage3 { get; set; } = string.Empty;

        public bool IsFinal { get; set; } = true;
        public float Confidence { get; set; } = 1.0f;
        public string Source { get; set; } = "Mic"; // Mic or Desktop

        // Segment metadata used to correlate late recognition results with capture timing.
        public long SegmentId { get; set; }
        public DateTime CapturedAtUtc { get; set; } = DateTime.MinValue;
    }
}
