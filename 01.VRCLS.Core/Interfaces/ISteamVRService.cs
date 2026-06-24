using System;
using System.Threading.Tasks;
using VRCLS.Core.Models;

namespace VRCLS.Core.Interfaces
{
    public interface ISteamVRService : IDisposable
    {
        bool IsConnected { get; }
        string Status { get; }
        string StartupRegistrationStatus { get; }

        event EventHandler? Connected;
        event EventHandler? Disconnected;
        event EventHandler<string>? StatusChanged;
        event EventHandler<string>? StartupRegistrationStatusChanged;
        event EventHandler<SteamVrOverlayAction>? ActionRequested;

        Task InitializeAsync();
        void SetVisible(bool visible);
        void UpdateText(string text);
        void UpdatePanel(SteamVrOverlayPanelState state);
        void SetHand(int hand); // 0=右, 1=左, 2=双手
        void SetTrackingDeviceMode(SteamVrTrackingDeviceMode mode);
        void SetSize(float size);
        void SetAnchorMode(SteamVrOverlayAnchorMode anchorMode);
        void SetPositionOffset(float offsetX, float offsetY, float offsetZ);
        void SetRotationOffset(float rotationXDeg, float rotationYDeg, float rotationZDeg);
        void ConfigureStartupAutoLaunch(bool enabled);
        void RefreshStartupRegistrationStatus();
        void IdentifyCurrentProcess();
    }
}
