using System;
using System.Reflection;
using System.Linq;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        private int? _currentServerPort;
        private string _currentServerIp = string.Empty;
        private UdpClient? _oscServer;
        private CancellationTokenSource? _oscServerCts;
        private Task? _oscServerTask;

        // 当接收到外部 OSC 消息时触发。
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
                ValidateOscServerPort(port);
                var requestedIp = NormalizeOscServerIp(_config.OscServerIp);

                if (_isServerRunning && _currentServerPort == port)
                {
                    if (string.Equals(_currentServerIp, requestedIp, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Debug("OSC服务器已在运行，忽略重复启动请求");
                        return;
                    }
                }

                if (_isServerRunning)
                {
                    _logger.Information(
                        "OSC服务器已运行，重新绑定地址 - oldEndpoint={OldIp}:{OldPort}, newEndpoint={NewIp}:{NewPort}",
                        _currentServerIp,
                        _currentServerPort,
                        requestedIp,
                        port);
                }
                else
                {
                    _logger.Information("启动OSC服务器 - ip={Ip}, port={Port}", requestedIp, port);
                }

                ConfigureOscServer(requestedIp, port);
            }
        }

        public void RestartOscServer(int port)
        {
            lock (_serverLock)
            {
                ValidateOscServerPort(port);
                var requestedIp = NormalizeOscServerIp(_config.OscServerIp);

                if (_isServerRunning)
                {
                    _logger.Information(
                        "重启OSC服务器 - oldEndpoint={OldIp}:{OldPort}, newEndpoint={NewIp}:{NewPort}",
                        _currentServerIp,
                        _currentServerPort,
                        requestedIp,
                        port);
                }
                else
                {
                    _logger.Information("启动OSC服务器 - ip={Ip}, port={Port}", requestedIp, port);
                }

                ConfigureOscServer(requestedIp, port);
            }
        }

        public void StopOscServer()
        {
            lock (_serverLock)
            {
                _logger.Information("停止OSC服务器");
                StopOscServerLocked();
            }
        }

        private void ConfigureOscServer(string ip, int port)
        {
            StopOscServerLocked();

            var ipAddress = ParseOscServerIp(ip);
            var endpoint = new IPEndPoint(ipAddress, port);
            var server = new UdpClient(ipAddress.AddressFamily);
            var cts = new CancellationTokenSource();

            try
            {
                server.Client.Bind(endpoint);
            }
            catch
            {
                cts.Dispose();
                server.Dispose();
                throw;
            }

            // Ensure send side uses the latest config.
            try
            {
                OscConnectionSettings.VrcIPAddress = _config.OscIp;
                OscConnectionSettings.SendPort = _config.OscPort;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "设置 VRChat OSC 发送地址失败 (ip={Ip}, port={Port})", _config.OscIp, _config.OscPort);
            }

            _oscServer = server;
            _oscServerCts = cts;
            _oscServerTask = Task.Run(() => ReceiveOscMessagesAsync(server, cts.Token));
            _isServerRunning = true;
            _currentServerPort = port;
            _currentServerIp = ipAddress.ToString();
            var message = $"OSC Server listening on {_currentServerIp}:{port}";
            _logger.Information("OSC服务器监听已启动 - {Message}", message);
            MessageReceived?.Invoke(this, new OscMessageReceivedEventArgs(message));
        }

        private void StopOscServerLocked()
        {
            _isServerRunning = false;
            _currentServerPort = null;
            _currentServerIp = string.Empty;

            var cts = _oscServerCts;
            var server = _oscServer;
            _oscServerCts = null;
            _oscServer = null;
            _oscServerTask = null;

            try
            {
                cts?.Cancel();
            }
            catch
            {
            }

            try
            {
                server?.Close();
                server?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "释放 OSC UDP 服务端失败");
            }
            finally
            {
                cts?.Dispose();
            }

            _logger.Debug("OSC服务器已停止");
        }

        private async Task ReceiveOscMessagesAsync(UdpClient server, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await server.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.Debug(ex, "OSC UDP 接收已取消");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "OSC UDP 接收失败");
                    continue;
                }

                try
                {
                    if (TryParseOscPacket(result.Buffer, out var packet, out var error))
                    {
                        _logger.Debug(
                            "收到OSC消息 - remote={Remote}, address={Address}, values={Values}",
                            result.RemoteEndPoint,
                            packet.Address,
                            string.Join(", ", packet.Values.Select(FormatOscValue)));
                        MessageReceived?.Invoke(this, new OscMessageReceivedEventArgs(packet.Address, packet.Values));
                    }
                    else
                    {
                        _logger.Debug(
                            "忽略无法解析的OSC消息 - remote={Remote}, length={Length}, error={Error}",
                            result.RemoteEndPoint,
                            result.Buffer.Length,
                            error);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "处理 OSC 消息失败");
                }
            }
        }

        private static void ValidateOscServerPort(int port)
        {
            if (port < 1 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port), port, "OSC server port must be between 1 and 65535.");
            }
        }

        private static string NormalizeOscServerIp(string? value)
        {
            return ParseOscServerIp(value).ToString();
        }

        private static IPAddress ParseOscServerIp(string? value)
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim();
            if (!IPAddress.TryParse(normalized, out var ipAddress))
            {
                throw new FormatException($"Invalid OSC server IP address: {normalized}");
            }

            return ipAddress;
        }

        private static bool TryParseOscPacket(byte[] buffer, out OscPacket packet, out string error)
        {
            packet = default;
            error = string.Empty;

            if (buffer == null || buffer.Length == 0)
            {
                error = "empty packet";
                return false;
            }

            return TryParseOscPacket(buffer, 0, buffer.Length, out packet, out error);
        }

        private static bool TryParseOscPacket(byte[] buffer, int offset, int length, out OscPacket packet, out string error)
        {
            packet = default;
            error = string.Empty;

            if (!TryReadOscString(buffer, ref offset, length, out var address, out error))
            {
                return false;
            }

            if (string.Equals(address, "#bundle", StringComparison.Ordinal))
            {
                return TryParseFirstBundleMessage(buffer, offset, length, out packet, out error);
            }

            if (string.IsNullOrWhiteSpace(address) || !address.StartsWith("/", StringComparison.Ordinal))
            {
                error = $"invalid OSC address '{address}'";
                return false;
            }

            if (!TryReadOscString(buffer, ref offset, length, out var tags, out error))
            {
                return false;
            }

            if (string.IsNullOrEmpty(tags) || tags[0] != ',')
            {
                error = $"invalid OSC type tag '{tags}'";
                return false;
            }

            var values = new List<object?>();
            for (var i = 1; i < tags.Length; i++)
            {
                var tag = tags[i];
                if (tag == '[' || tag == ']')
                {
                    continue;
                }

                if (!TryReadOscValue(buffer, ref offset, length, tag, out var value, out error))
                {
                    return false;
                }

                values.Add(value);
            }

            packet = new OscPacket(address, values);
            return true;
        }

        private static bool TryParseFirstBundleMessage(byte[] buffer, int offset, int length, out OscPacket packet, out string error)
        {
            packet = default;
            error = string.Empty;

            if (!TryAdvance(ref offset, length, 8))
            {
                error = "bundle missing timetag";
                return false;
            }

            while (offset < length)
            {
                if (!TryReadInt32(buffer, ref offset, length, out var elementSize))
                {
                    error = "bundle missing element size";
                    return false;
                }

                if (elementSize <= 0 || offset + elementSize > length)
                {
                    error = $"invalid bundle element size {elementSize}";
                    return false;
                }

                if (TryParseOscPacket(buffer, offset, offset + elementSize, out packet, out error))
                {
                    return true;
                }

                offset += elementSize;
            }

            error = "bundle contains no OSC message";
            return false;
        }

        private static bool TryReadOscString(byte[] buffer, ref int offset, int length, out string value, out string error)
        {
            value = string.Empty;
            error = string.Empty;

            if (offset >= length)
            {
                error = "missing OSC string";
                return false;
            }

            var start = offset;
            while (offset < length && buffer[offset] != 0)
            {
                offset++;
            }

            if (offset >= length)
            {
                error = "unterminated OSC string";
                return false;
            }

            value = Encoding.UTF8.GetString(buffer, start, offset - start);
            offset++;
            AlignOscOffset(ref offset);
            if (offset > length)
            {
                error = "OSC string padding exceeds packet";
                return false;
            }

            return true;
        }

        private static bool TryReadOscValue(byte[] buffer, ref int offset, int length, char tag, out object? value, out string error)
        {
            value = null;
            error = string.Empty;

            switch (tag)
            {
                case 's':
                case 'S':
                    if (!TryReadOscString(buffer, ref offset, length, out var text, out error))
                    {
                        return false;
                    }
                    value = text;
                    return true;
                case 'i':
                    if (!TryReadInt32(buffer, ref offset, length, out var intValue))
                    {
                        error = "missing int32 value";
                        return false;
                    }
                    value = intValue;
                    return true;
                case 'h':
                    if (!TryReadInt64(buffer, ref offset, length, out var longValue))
                    {
                        error = "missing int64 value";
                        return false;
                    }
                    value = longValue;
                    return true;
                case 'f':
                    if (!TryReadInt32(buffer, ref offset, length, out var floatBits))
                    {
                        error = "missing float32 value";
                        return false;
                    }
                    value = BitConverter.Int32BitsToSingle(floatBits);
                    return true;
                case 'd':
                    if (!TryReadInt64(buffer, ref offset, length, out var doubleBits))
                    {
                        error = "missing float64 value";
                        return false;
                    }
                    value = BitConverter.Int64BitsToDouble(doubleBits);
                    return true;
                case 'T':
                    value = true;
                    return true;
                case 'F':
                    value = false;
                    return true;
                case 'N':
                    value = null;
                    return true;
                case 'I':
                    value = float.PositiveInfinity;
                    return true;
                case 'b':
                    if (!TryReadBlob(buffer, ref offset, length, out var blob, out error))
                    {
                        return false;
                    }
                    value = blob;
                    return true;
                case 'c':
                    if (!TryReadInt32(buffer, ref offset, length, out var charCode))
                    {
                        error = "missing ascii char value";
                        return false;
                    }
                    value = char.ConvertFromUtf32(charCode);
                    return true;
                case 'r':
                    if (!TryReadInt32(buffer, ref offset, length, out var color))
                    {
                        error = "missing color value";
                        return false;
                    }
                    value = $"#{color:X8}";
                    return true;
                case 'm':
                    if (!TryReadFixedBytes(buffer, ref offset, length, 4, out var midi))
                    {
                        error = "missing midi value";
                        return false;
                    }
                    value = midi;
                    return true;
                case 't':
                    if (!TryReadInt64(buffer, ref offset, length, out var timestamp))
                    {
                        error = "missing timetag value";
                        return false;
                    }
                    value = timestamp;
                    return true;
                default:
                    error = $"unsupported OSC type tag '{tag}'";
                    return false;
            }
        }

        private static bool TryReadBlob(byte[] buffer, ref int offset, int length, out byte[] blob, out string error)
        {
            blob = Array.Empty<byte>();
            error = string.Empty;

            if (!TryReadInt32(buffer, ref offset, length, out var blobLength))
            {
                error = "missing blob length";
                return false;
            }

            if (blobLength < 0 || offset + blobLength > length)
            {
                error = $"invalid blob length {blobLength}";
                return false;
            }

            blob = new byte[blobLength];
            Buffer.BlockCopy(buffer, offset, blob, 0, blobLength);
            offset += blobLength;
            AlignOscOffset(ref offset);
            if (offset > length)
            {
                error = "blob padding exceeds packet";
                return false;
            }

            return true;
        }

        private static bool TryReadFixedBytes(byte[] buffer, ref int offset, int length, int count, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (!TryAdvance(ref offset, length, count))
            {
                return false;
            }

            bytes = new byte[count];
            Buffer.BlockCopy(buffer, offset - count, bytes, 0, count);
            return true;
        }

        private static bool TryReadInt32(byte[] buffer, ref int offset, int length, out int value)
        {
            value = 0;
            if (!TryAdvance(ref offset, length, 4))
            {
                return false;
            }

            value = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(offset - 4, 4));
            return true;
        }

        private static bool TryReadInt64(byte[] buffer, ref int offset, int length, out long value)
        {
            value = 0;
            if (!TryAdvance(ref offset, length, 8))
            {
                return false;
            }

            value = BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(offset - 8, 8));
            return true;
        }

        private static bool TryAdvance(ref int offset, int length, int count)
        {
            if (count < 0 || offset + count > length)
            {
                return false;
            }

            offset += count;
            return true;
        }

        private static void AlignOscOffset(ref int offset)
        {
            var remainder = offset % 4;
            if (remainder != 0)
            {
                offset += 4 - remainder;
            }
        }

        private static string FormatOscValue(object? value)
        {
            return value switch
            {
                null => "null",
                byte[] bytes => $"blob[{bytes.Length}]",
                string text => text,
                _ => value.ToString() ?? string.Empty
            };
        }

        private readonly record struct OscPacket(string Address, IReadOnlyList<object?> Values);

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
