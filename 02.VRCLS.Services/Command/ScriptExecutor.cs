using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
using Serilog;

namespace VRCLS.Services.Command
{
    /// <summary>
    /// 脚本执行器
    /// 执行用户自定义的 VoiceScript
    /// </summary>
    public class ScriptExecutor
    {
        private static readonly ILogger _logger = Log.ForContext<ScriptExecutor>();
        private readonly IOscService _oscService;

        public ScriptExecutor(IOscService oscService)
        {
            _oscService = oscService ?? throw new ArgumentNullException(nameof(oscService));
        }

        /// <summary>
        /// 执行脚本
        /// </summary>
        public async Task<CommandResult> ExecuteAsync(VoiceScript script, string triggerText)
        {
            try
            {
                _logger.Debug("开始执行脚本: {Action}, 动作数量: {Count}",
                    script.Action, script.VrcActions?.Count ?? 0);

                if (script.VrcActions == null || script.VrcActions.Count == 0)
                {
                    return CommandResult.Fail("脚本没有定义任何动作");
                }

                foreach (var action in script.VrcActions)
                {
                    await ExecuteActionAsync(action);
                }

                var feedback = $"已执行: {script.Action}";
                return CommandResult.Ok($"脚本执行成功: {script.Action}", feedback);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "执行脚本失败: {Action}", script.Action);
                return CommandResult.Fail($"执行脚本失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 执行单个动作
        /// </summary>
        private async Task ExecuteActionAsync(VrcAction action)
        {
            _logger.Debug("执行动作: Path={Path}, Type={Type}, Value={Value}",
                action.VrcPath, action.VrcValueType, action.VrcValue);

            try
            {
                // 发送 OSC 参数
                switch (action.VrcValueType?.ToLowerInvariant())
                {
                    case "float":
                    case "float32":
                        if (TryConvertToFloat(action.VrcValue, out var floatValue))
                        {
                            _oscService.SendAvatarParameter(action.VrcPath, floatValue);
                            _logger.Verbose("发送 Float 参数: {Path} = {Value}", action.VrcPath, floatValue);
                        }
                        break;

                    case "int":
                    case "integer":
                    case "int32":
                        if (TryConvertToInt(action.VrcValue, out var intValue))
                        {
                            _oscService.SendAvatarParameter(action.VrcPath, intValue);
                            _logger.Verbose("发送 Int 参数: {Path} = {Value}", action.VrcPath, intValue);
                        }
                        break;

                    case "bool":
                    case "boolean":
                        if (TryConvertToBool(action.VrcValue, out var boolValue))
                        {
                            _oscService.SendAvatarParameter(action.VrcPath, boolValue);
                            _logger.Verbose("发送 Bool 参数: {Path} = {Value}", action.VrcPath, boolValue);
                        }
                        break;

                    default:
                        _logger.Warning("未知的参数类型: {Type}", action.VrcValueType);
                        break;
                }

                // 执行延迟
                if (action.SleepTime > 0)
                {
                    _logger.Verbose("延迟 {SleepTime}ms", action.SleepTime);
                    await Task.Delay((int)action.SleepTime);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "执行动作失败: {Path}", action.VrcPath);
                throw;
            }
        }

        /// <summary>
        /// 查找匹配的脚本
        /// </summary>
        public VoiceScript? FindMatchingScript(List<VoiceScript> scripts, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || scripts == null || scripts.Count == 0)
                return null;

            var normalized = text.Trim().ToLowerInvariant();

            foreach (var script in scripts)
            {
                if (script.Text == null || script.Text.Count == 0)
                    continue;

                // 检查是否匹配任何触发词
                foreach (var trigger in script.Text)
                {
                    if (normalized.Contains(trigger.Trim().ToLowerInvariant()))
                    {
                        _logger.Debug("脚本匹配成功: {Action} (触发词: {Trigger})",
                            script.Action, trigger);
                        return script;
                    }
                }
            }

            return null;
        }

        #region Type Conversion Helpers

        private bool TryConvertToFloat(object? value, out float result)
        {
            result = 0f;
            if (value == null) return false;

            try
            {
                result = Convert.ToSingle(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryConvertToInt(object? value, out int result)
        {
            result = 0;
            if (value == null) return false;

            try
            {
                result = Convert.ToInt32(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryConvertToBool(object? value, out bool result)
        {
            result = false;
            if (value == null) return false;

            try
            {
                // 处理数字类型的布尔值 (0=false, 其他=true)
                if (value is int i)
                {
                    result = i != 0;
                    return true;
                }
                if (value is float f)
                {
                    result = f != 0f;
                    return true;
                }

                result = Convert.ToBoolean(value);
                return true;
            }
            catch
            {
                // 尝试解析字符串
                var str = value.ToString()?.ToLowerInvariant();
                if (str == "true" || str == "1" || str == "yes" || str == "on")
                {
                    result = true;
                    return true;
                }
                if (str == "false" || str == "0" || str == "no" || str == "off")
                {
                    result = false;
                    return true;
                }
                return false;
            }
        }

        #endregion
    }

    /// <summary>
    /// 用户脚本命令处理器
    /// </summary>
    public class UserScriptCommandHandler : ICommandHandler
    {
        private readonly VoiceScript _script;
        private readonly ScriptExecutor _executor;

        public UserScriptCommandHandler(VoiceScript script, ScriptExecutor executor)
        {
            _script = script ?? throw new ArgumentNullException(nameof(script));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        public string CommandName => _script.Action;
        public string Description => $"用户脚本: {_script.Action}";
        public List<string> TriggerKeywords => _script.Text ?? new List<string>();

        public bool Matches(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || _script.Text == null)
                return false;

            var normalized = text.Trim().ToLowerInvariant();
            return _script.Text.Any(trigger =>
                normalized.Contains(trigger.Trim().ToLowerInvariant()));
        }

        public Task<CommandResult> ExecuteAsync(string triggerText, string? routeHint = null)
        {
            return _executor.ExecuteAsync(_script, triggerText);
        }
    }
}
