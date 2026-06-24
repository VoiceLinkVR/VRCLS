using System;
using VRCLS.Core.Models;

namespace VRCLS.Core.Interfaces
{
    public interface IOscService
    {
        void SendChatbox(string text, bool direct = true, bool triggerNotificationSfx = true);
        void SendAvatarParameter(string path, float value);
        void SendAvatarParameter(string path, bool value);
        void SendAvatarParameter(string path, int value);
        void StartOscServer(int port);
        void RestartOscServer(int port);
        void StopOscServer();
        void SendInputButton(string inputName, bool press);
        void SendInputAxis(string inputName, float value);

        event EventHandler<OscMessageReceivedEventArgs>? MessageReceived;
    }
}
