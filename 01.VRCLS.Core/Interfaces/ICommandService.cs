using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VRCLS.Core.Models;

namespace VRCLS.Core.Interfaces
{
    public enum CommandActionType
    {
        None = 0,
        MicToggle,
        MicOn,
        MicOff,
        DesktopToggle,
        DesktopOn,
        DesktopOff
    }

    public class CommandActionEventArgs : EventArgs
    {
        public CommandActionType Action { get; set; } = CommandActionType.None;
        public string TriggerText { get; set; } = string.Empty;
        public string? FeedbackText { get; set; }
    }

    /// <summary>
    /// 命令执行事件参数
    /// </summary>
    public class CommandExecutedEventArgs : EventArgs
    {
        /// <summary>
        /// 命令名称
        /// </summary>
        public string CommandName { get; set; } = string.Empty;

        /// <summary>
        /// 命令描述
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 是否执行成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 执行结果消息
        /// </summary>
        public string? Message { get; set; }

        /// <summary>
        /// 触发该命令的语音文本
        /// </summary>
        public string TriggerText { get; set; } = string.Empty;

        /// <summary>
        /// 语音反馈文本
        /// </summary>
        public string? FeedbackText { get; set; }
    }

    /// <summary>
    /// 命令服务接口
    /// 支持语音命令识别和执行
    /// </summary>
    public interface ICommandService
    {
        /// <summary>
        /// 命令执行完成事件
        /// </summary>
        event EventHandler<CommandExecutedEventArgs>? CommandExecuted;

        /// <summary>
        /// Raised when a command requests an immediate app action (e.g., toggle mic/desktop capture).
        /// </summary>
        event EventHandler<CommandActionEventArgs>? ActionRequested;

        /// <summary>
        /// 是否启用命令系统
        /// </summary>
        bool IsEnabled { get; set; }

        /// <summary>
        /// 处理识别到的文本，检查是否匹配命令
        /// </summary>
        /// <param name="recognizedText">识别到的文本</param>
        /// <returns>是否匹配并执行了命令</returns>
        bool ProcessText(string recognizedText, string? routeHint = null);

        /// <summary>
        /// 注册默认内置命令
        /// </summary>
        void RegisterDefaultCommands();

        /// <summary>
        /// 加载用户自定义脚本
        /// </summary>
        /// <param name="scripts">用户脚本列表</param>
        void LoadUserScripts(List<VoiceScript> scripts);

        /// <summary>
        /// 清除所有已注册的命令
        /// </summary>
        void ClearCommands();

        /// <summary>
        /// 获取所有已注册的命令列表
        /// </summary>
        List<string> GetRegisteredCommands();
    }

    /// <summary>
    /// 命令处理器接口
    /// </summary>
    public interface ICommandHandler
    {
        /// <summary>
        /// 命令名称
        /// </summary>
        string CommandName { get; }

        /// <summary>
        /// 命令描述
        /// </summary>
        string Description { get; }

        /// <summary>
        /// 触发关键词列表
        /// </summary>
        List<string> TriggerKeywords { get; }

        /// <summary>
        /// 检查文本是否匹配该命令
        /// </summary>
        bool Matches(string text);

        /// <summary>
        /// 执行命令
        /// </summary>
        Task<CommandResult> ExecuteAsync(string triggerText, string? routeHint = null);
    }

    /// <summary>
    /// 命令执行结果
    /// </summary>
    public class CommandResult
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? FeedbackText { get; set; }

        public static CommandResult Ok(string? message = null, string? feedback = null)
            => new() { Success = true, Message = message, FeedbackText = feedback };

        public static CommandResult Fail(string message)
            => new() { Success = false, Message = message };
    }
}
