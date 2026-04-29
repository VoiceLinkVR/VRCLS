using System;
using System.IO;
using System.Threading.Tasks;
using VRCLS.Services.Audio;
using VRCLS.Services.Recognition;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;

namespace VRCLS.ConsoleTest
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("VRCLS 服务端链路测试程序");
            Console.WriteLine("========================\n");

            // 1. 初始化音频采集
            Console.WriteLine("步骤 1: 初始化音频服务");
            IAudioService audioService = new AudioCaptureService();

            // 2. 初始化 VAD (服务端模式必须使用 VAD 来截取整句)
            Console.WriteLine("步骤 2: 初始化 VAD 模型");
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var vadModelPath = Path.Combine(baseDir, "models", "silero_vad.onnx");

            // 路径回退检查（用于调试环境）
            if (!File.Exists(vadModelPath))
            {
                vadModelPath = "D:\\git\\gitlab\\VRCLS-CSharp\\tests\\VRCLS.ConsoleTest\\models\\silero_vad.onnx";
            }

            if (!File.Exists(vadModelPath))
            {
                Console.WriteLine($"错误: 找不到 VAD 模型文件: {vadModelPath}");
                Console.WriteLine("服务端模式必须启用 VAD 以切分语音段。");
                return;
            }

            IVadService vadService;
            try
            {
                vadService = new VadService(vadModelPath);
                Console.WriteLine("✓ VAD 服务已就绪");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"VAD 初始化失败: {ex.Message}");
                return;
            }

            // 3. 配置服务端连接信息
            Console.WriteLine("\n步骤 3: 配置服务端连接");
            var config = new AppConfig
            {
                // 请确保这些配置与你的服务端设置一致
                ApiIp = "192.168.2.198",
                ApiPort = 8980,
                BaseUrl = "http://192.168.2.198:8980",
                UserInfo = new UserInfo
                {
                    Username = "testuser", // 服务端默认管理员账号
                    Password = "abc123!" // 替换为你设置的密码
                },
                SourceLanguage = "zh",
                TargetTranslationLanguage = "en"
            };
            Console.WriteLine($"目标服务器: {config.BaseUrl?.TrimEnd('/')}/api");

            // 4. 初始化网络识别服务
            IRecognitionService recognitionService = new FastApiRecognitionService(config, "ConsoleTest");

            Console.WriteLine("\n步骤 4: 尝试登录服务端...");
            // 手动调用 Start (内部会调用 LoginAsync) 来验证连接
            recognitionService.Start();

            // 简单等待一下登录结果（实际应用中是异步的）
            await Task.Delay(1000);

            // 5. 创建音频管道 (连接 Capture -> VAD -> Network Service)
            Console.WriteLine("步骤 5: 创建音频管道");
            // 注意：这里传入了 vadService，Pipeline 会自动启用 VAD 逻辑
            // VAD 检测到说话结束(SpeechEnded)时，会将整段音频发给 recognitionService
            var pipeline = new AudioPipelineService(audioService, vadService, recognitionService);

            // 订阅识别结果事件
            pipeline.TextRecognized += (sender, result) =>
            {
                Console.WriteLine($"\n>>> [服务端返回] {result.Text}");
                Console.Write("等待语音输入... "); // 提示符
            };

            // 订阅音量变化（用于视觉反馈）
            pipeline.VolumeLevelChanged += (sender, e) =>
            {
                // 简单的音量条
                if (e.Level > 0.01f) // 设置一个显示阈值，避免噪音刷屏
                {
                    var barLength = (int)(e.Level * 50);
                    var bar = new string('#', Math.Min(barLength, 50));
                    Console.Write($"\r[音量] {bar,-20}");
                }
            };

            // 6. 开始运行
            Console.WriteLine("\n步骤 6: 开始录音测试");
            Console.WriteLine("请对着麦克风说话。系统将检测语音结束，打包并上传到服务端。");
            Console.WriteLine("按任意键停止录音...\n");

            pipeline.StartCapture(new AudioCaptureRequest
            {
                DeviceId = "default",
                IsLoopback = false
            });

            Console.ReadKey();

            Console.WriteLine("\n\n正在停止...");
            pipeline.StopCapture();

            if (recognitionService is IDisposable disposable)
            {
                disposable.Dispose();
            }

            Console.WriteLine("测试完成!");
            await Task.Delay(500); // 等待清理
        }
    }
}
