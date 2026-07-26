namespace VRCLS.Core.Models
{
    public enum ServiceErrorCode
    {
        RateLimit,
        AudioCaptureStartFailed,
        AudioCaptureStoppedUnexpectedly,
        AudioCaptureDeviceFallback,
        AudioProcessingRepeatedFailures,
        RecognitionAuthRequired,
        RecognitionAuthExpired,
        RecognitionFailed,
        TtsAuthRequired,
        TtsRateLimit,
        TtsAuthExpired,
        TtsClientUpgradeRequired,
        TtsHttpError,
        TtsFailed
    }
}
