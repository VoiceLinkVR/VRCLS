using System;
using System.Threading.Tasks;
using VRCLS.Core.Models;

namespace VRCLS.Core.Interfaces
{
    /// <summary>
    /// TTS 服务接口
    /// </summary>
    public interface ITtsService
    {
        /// <summary>
        /// 朗读文本
        /// </summary>
        /// <param name="text">要朗读的文本</param>
        /// <param name="voiceName">语音名称（可选）</param>
        Task SpeakAsync(string text, string? voiceName = null);

        /// <summary>
        /// 朗读文本（带语言指定）
        /// </summary>
        /// <param name="text">要朗读的文本</param>
        /// <param name="language">语言代码（如 zh, en, ja）</param>
        /// <param name="voiceName">语音名称（可选）</param>
        Task SpeakAsync(string text, string language, string? voiceName = null);

        /// <summary>
        /// 停止当前朗读
        /// </summary>
        void Stop();

        /// <summary>
        /// 是否正在朗读
        /// </summary>
        bool IsSpeaking { get; }

        /// <summary>
        /// 朗读完成事件
        /// </summary>
        event EventHandler? SpeakCompleted;

        /// <summary>
        /// 朗读错误事件
        /// </summary>
        event EventHandler<string>? SpeakError;

        /// <summary>
        /// 强类型错误事件
        /// </summary>
        event EventHandler<ServiceErrorEvent>? ErrorOccurred;
    }
}
