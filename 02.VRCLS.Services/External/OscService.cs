using System;
using System.Reflection;
using System.Linq;
using BuildSoft.VRChat.Osc;
using BuildSoft.VRChat.Osc.Chatbox;
using BuildSoft.VRChat.Osc.Input;
using BuildSoft.VRChat.Osc.Avatar;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
using Serilog;

namespace VRCLS.Services.External
{
    public class OscService : IOscService, IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<OscService>();
        private readonly AppConfig _config;
        private readonly object _serverLock = new();
        private bool _isServerRunning = false;

        // 当接收到 VRChat 的 OSC 消息时触发（主要用于日志）
        public event EventHandler<OscMessageReceivedEventArgs>? MessageReceived;

        public OscService()
            : this(new AppConfig())
        {
        }

        public OscService(AppConfig config)
        {
            _logger.Information("初始化 OscService");
            _config = config ?? throw new ArgumentNullException(nameof(config));

            // Keep send side aligned with config (affects OscChatbox/OscParameter sends).
            try
            {
                OscConnectionSettings.VrcIPAddress = _config.OscIp;
                OscConnectionSettings.SendPort = _config.OscPort;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "设置 VRChat OSC 发送地址失败 (ip={Ip}, port={Port})", _config.OscIp, _config.OscPort);
            }

            _logger.Debug("OscService 初始化完成");
        }

        public void StartOscServer(int port)
        {
            lock (_serverLock)
            {
                if (_isServerRunning)
                {
                    _logger.Debug("OSC服务器已在运行，忽略重复启动请求");
                    return;
                }

                _logger.Information("启动OSC服务器 - 端口: {Port}", port);
                // VRCOscLib 自动管理连接，这里只需配置端口
                OscConnectionSettings.ReceivePort = port;

                // Ensure send side uses the latest config.
                try
                {
                    OscConnectionSettings.VrcIPAddress = _config.OscIp;
                    OscConnectionSettings.SendPort = _config.OscPort;
                }
                catch { }

                try
                {
                    // Initialize parameter system so OSC settings take effect consistently.
                    OscUtility.Initialize();
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "启动OSC监听失败 (port={Port})", port);
                }

                _isServerRunning = true;
                var message = $"OSC Server configured on port {port}";
                _logger.Debug("OSC服务器配置完成 - {Message}", message);
                MessageReceived?.Invoke(this, new OscMessageReceivedEventArgs(message));
            }
        }

        public void StopOscServer()
        {
            lock (_serverLock)
            {
                _logger.Information("停止OSC服务器");
                // VRCOscLib 没有显式的 Stop 方法暴露给外部，通常随程序退出释放
                // 我们这里只标记状态
                _isServerRunning = false;
                _logger.Debug("OSC服务器已停止");
            }
        }

        public void SendChatbox(string text, bool direct = true, bool triggerNotificationSfx = true)
        {
            if (string.IsNullOrEmpty(text))
            {
                _logger.Warning("尝试发送空的聊天消息");
                return;
            }

            // VRChat chatbox (via OSC) is sensitive to newline conventions.
            // If we send CRLF (\r\n) and the receiver treats both \r and \n as line breaks,
            // users will observe "extra blank lines" between lines.
            text = NormalizeChatboxNewlines(text);

            _logger.Debug(
                "发送OSC聊天消息 - 长度: {Length}, 直接发送: {Direct}, 提示音: {TriggerNotificationSfx}",
                text.Length,
                direct,
                triggerNotificationSfx);
            _logger.Debug("OSC发送目标 - ip={Ip}, port={Port}", _config.OscIp, _config.OscPort);
            try
            {
                OscConnectionSettings.VrcIPAddress = _config.OscIp;
                OscConnectionSettings.SendPort = _config.OscPort;
            }
            catch { }
            // direct=true 表示不显示输入框，直接发送
            OscChatbox.SendMessage(text, direct, triggerNotificationSfx);
            _logger.Verbose("OSC聊天消息发送完成");
        }

        private static string NormalizeChatboxNewlines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            // Normalize to LF only.
            // - "\r\n" -> "\n"
            // - "\r"   -> "\n"
            return text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal);
        }

        public void SendAvatarParameter(string path, float value)
        {
            string paramName = CleanParamName(path);
            _logger.Verbose("发送OSC头像参数 - 路径: {Path}, 参数名: {ParamName}, 值: {Value}", path, paramName, value);
            OscParameter.SendAvatarParameter(paramName, value);
        }

        public void SendAvatarParameter(string path, bool value)
        {
            string paramName = CleanParamName(path);
            _logger.Verbose("发送OSC头像参数 - 路径: {Path}, 参数名: {ParamName}, 值: {Value}", path, paramName, value);
            OscParameter.SendAvatarParameter(paramName, value);
        }

        public void SendAvatarParameter(string path, int value)
        {
            string paramName = CleanParamName(path);
            _logger.Verbose("发送OSC头像参数 - 路径: {Path}, 参数名: {ParamName}, 值: {Value}", path, paramName, value);
            OscParameter.SendAvatarParameter(paramName, value);
        }

        /// <summary>
        /// 去除 VRChat 参数路径的前缀，因为 VRCOscLib 通常只需要参数名
        /// 例如 /avatar/parameters/MuteSelf -> MuteSelf
        /// </summary>
        private string CleanParamName(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                _logger.Warning("尝试清理空的参数路径");
                return string.Empty;
            }

            _logger.Verbose("清理参数路径 - 原始: {Original}", path);

            // 移除常见的 OSC 地址前缀
            string cleaned = path.Replace("/avatar/parameters/", "")
                                 .Replace("/input/", "");

            string result = cleaned.TrimStart('/');
            _logger.Verbose("参数路径清理结果 - 原始: {Original}, 清理后: {Cleaned}", path, result);

            return result;
        }

        public void SendInputButton(string inputName, bool press)
        {
            if (string.IsNullOrEmpty(inputName))
            {
                _logger.Warning("尝试发送空的输入按钮名称");
                return;
            }

            _logger.Debug("发送OSC输入按钮 - 名称: {InputName}, 按压: {Press}", inputName, press);

            try
            {
                // 使用反射查找 OscButtonInput 中的静态属性 (例如 Jump, Run)
                var prop = typeof(OscButtonInput).GetProperty(inputName, BindingFlags.Public | BindingFlags.Static);
                if (prop != null)
                {
                    _logger.Verbose("找到标准输入按钮 - 名称: {InputName}", inputName);

                    // 修复：OscButtonInput 是结构体(struct)，Reflection.GetValue 返回 object
                    // 需要使用 'is' 模式匹配来安全转换，不能使用 'as'
                    var objValue = prop.GetValue(null);
                    if (objValue is OscButtonInput buttonInput)
                    {
                        if (press)
                        {
                            buttonInput.Press();
                            _logger.Debug("输入按钮已按下 - 名称: {InputName}", inputName);
                        }
                        else
                        {
                            buttonInput.Release();
                            _logger.Debug("输入按钮已释放 - 名称: {InputName}", inputName);
                        }
                    }
                    else
                    {
                        _logger.Warning("转换OscButtonInput失败 - 名称: {InputName}", inputName);
                    }
                }
                else
                {
                    _logger.Debug("未找到标准输入按钮，作为参数发送 - 名称: {InputName}", inputName);
                    // 找不到标准输入按键时，尝试作为普通参数发送
                    SendAvatarParameter(inputName, press);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "发送OSC输入按钮失败 - 名称: {InputName}", inputName);
            }
        }

        public void SendInputAxis(string inputName, float value)
        {
            if (string.IsNullOrEmpty(inputName))
            {
                _logger.Warning("尝试发送空的输入轴名称");
                return;
            }

            _logger.Debug("发送OSC输入轴 - 名称: {InputName}, 值: {Value}", inputName, value);

            try
            {
                // 使用反射查找 OscAxisInput 中的静态属性 (例如 Vertical, Horizontal)
                var prop = typeof(OscAxisInput).GetProperty(inputName, BindingFlags.Public | BindingFlags.Static);
                if (prop != null)
                {
                    _logger.Verbose("找到标准输入轴 - 名称: {InputName}", inputName);

                    // 修复：OscAxisInput 是结构体，处理方式同上
                    var objValue = prop.GetValue(null);
                    if (objValue is OscAxisInput axisInput)
                    {
                        axisInput.Send(value);
                        _logger.Verbose("输入轴值已发送 - 名称: {InputName}, 值: {Value}", inputName, value);
                    }
                    else
                    {
                        _logger.Warning("转换OscAxisInput失败 - 名称: {InputName}", inputName);
                    }
                }
                else
                {
                    _logger.Debug("未找到标准输入轴 - 名称: {InputName}", inputName);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "发送OSC输入轴失败 - 名称: {InputName}", inputName);
            }
        }

        public void Dispose()
        {
            _logger.Debug("释放 OscService 资源");
            try
            {
                StopOscServer();
                _logger.Debug("OscService 资源已释放");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "释放 OscService 资源失败");
            }
        }
    }
}
