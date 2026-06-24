namespace VRCLS.Core.Models
{
    public sealed class AudioCaptureRequest
    {
        public string DeviceId { get; init; } = "default";
        public bool IsLoopback { get; init; }
        public DesktopAudioSourceMode DesktopSourceMode { get; init; } = DesktopAudioSourceMode.System;
        public uint TargetProcessId { get; init; }
        public string TargetProcessName { get; init; } = string.Empty;

        public bool IsApplicationLoopback
            => IsLoopback && DesktopSourceMode == DesktopAudioSourceMode.Application;
    }
}
