using System;

namespace VRCLS.Core.Interfaces
{
    /// <summary>
    /// 热键事件参数
    /// </summary>
    public class HotkeyEventArgs : EventArgs
    {
        /// <summary>
        /// 热键标识符 (如 "alt+1", "ctrl+shift+m")
        /// </summary>
        public string HotkeyId { get; set; } = string.Empty;

        /// <summary>
        /// 热键类型 (mic/desktop)
        /// </summary>
        public string HotkeyType { get; set; } = string.Empty;
    }

    /// <summary>
    /// 按键状态变化事件参数
    /// </summary>
    public class KeyStateEventArgs : EventArgs
    {
        /// <summary>
        /// 按键名称 (如 "v", "b")
        /// </summary>
        public string KeyName { get; set; } = string.Empty;

        /// <summary>
        /// 是否按下
        /// </summary>
        public bool IsPressed { get; set; }

        /// <summary>
        /// 按键类型 (mic/desktop)
        /// </summary>
        public string KeyType { get; set; } = string.Empty;
    }

    /// <summary>
    /// 热键服务接口
    /// 支持全局热键和按住说话功能
    /// </summary>
    public interface IHotkeyService : IDisposable
    {
        /// <summary>
        /// 热键按下事件 (用于切换模式)
        /// </summary>
        event EventHandler<HotkeyEventArgs>? HotkeyPressed;

        /// <summary>
        /// 按键状态变化事件 (用于按住说话)
        /// </summary>
        event EventHandler<KeyStateEventArgs>? KeyStateChanged;

        /// <summary>
        /// 服务是否正在运行
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// 注册热键组合 (如 "&lt;alt&gt;+1")
        /// </summary>
        /// <param name="hotkeyString">热键字符串，格式如 "&lt;alt&gt;+1", "&lt;ctrl&gt;+&lt;shift&gt;+m"</param>
        /// <param name="hotkeyType">热键类型标识</param>
        void RegisterHotkey(string hotkeyString, string hotkeyType);

        /// <summary>
        /// 注册按住说话按键
        /// </summary>
        /// <param name="keyName">按键名称 (如 "v", "b")</param>
        /// <param name="keyType">按键类型标识</param>
        /// <param name="releaseDelayMs">松开后延迟时间(毫秒)</param>
        void RegisterPressKey(string keyName, string keyType, int releaseDelayMs = 0);

        /// <summary>
        /// 取消注册热键
        /// </summary>
        /// <param name="hotkeyType">热键类型标识</param>
        void UnregisterHotkey(string hotkeyType);

        /// <summary>
        /// 取消注册按住说话按键
        /// </summary>
        /// <param name="keyType">按键类型标识</param>
        void UnregisterPressKey(string keyType);

        /// <summary>
        /// 启动热键监听
        /// </summary>
        void Start();

        /// <summary>
        /// 停止热键监听
        /// </summary>
        void Stop();
    }
}
