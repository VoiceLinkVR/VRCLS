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
        TtsAuthRequired,
        TtsRateLimit,
        TtsAuthExpired,
        TtsClientUpgradeRequired,
        TtsHttpError,
        TtsFailed
    }
}
