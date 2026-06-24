using System;
using System.Windows;
using System.Windows.Threading;
using VRCLS.Core.Models;
using VRCLS.Services.Audio;

namespace VRCLS.UI.ViewModels
{
    public partial class MainViewModel
    {
        private DispatcherTimer? _healthMonitorTimer;
        private DateTime _lastMicHealthRestartUtc = DateTime.MinValue;
        private DateTime _lastDesktopHealthRestartUtc = DateTime.MinValue;

        private static readonly TimeSpan PipelineHealthInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan HealthRestartCooldown = TimeSpan.FromSeconds(15);

        private void InitializeHealthMonitor()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            if (_healthMonitorTimer == null)
            {
                _healthMonitorTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                {
                    Interval = PipelineHealthInterval
                };
                _healthMonitorTimer.Tick += OnHealthMonitorTick;
            }

            _healthMonitorTimer.Stop();
            _healthMonitorTimer.Start();
        }

        private void DisposeHealthMonitor()
        {
            if (_healthMonitorTimer == null)
            {
                return;
            }

            _healthMonitorTimer.Stop();
            _healthMonitorTimer.Tick -= OnHealthMonitorTick;
            _healthMonitorTimer = null;
        }

        private void OnHealthMonitorTick(object? sender, EventArgs e)
        {
            CheckPipelineHealth(_micPipeline, isMic: true);
            CheckPipelineHealth(_desktopPipeline, isMic: false);
        }

        private void CheckPipelineHealth(AudioPipelineService? pipeline, bool isMic)
        {
            if (pipeline == null)
            {
                return;
            }

            HealthReport report;
            try
            {
                report = pipeline.CheckHealth();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Pipeline health check failed to execute.");
                return;
            }

            if (report.Status == HealthStatus.Healthy)
            {
                return;
            }

            if (report.Status == HealthStatus.Degraded)
            {
                _logger.Debug("Pipeline degraded ({Source}): {Message}", isMic ? "Mic" : "Desktop", report.Message ?? "N/A");
                return;
            }

            var now = DateTime.UtcNow;
            if (isMic)
            {
                if (now - _lastMicHealthRestartUtc < HealthRestartCooldown)
                {
                    return;
                }

                _lastMicHealthRestartUtc = now;
            }
            else
            {
                if (now - _lastDesktopHealthRestartUtc < HealthRestartCooldown)
                {
                    return;
                }

                _lastDesktopHealthRestartUtc = now;
            }

            var source = isMic ? "麦克风" : "桌面音频";
            var message = report.Message ?? "音频管线状态异常";
            _logger.Warning("Pipeline unhealthy ({Source}): {Message}. Scheduling restart.", source, message);

            QueuePipelineRestart(restartMic: isMic, restartDesktop: !isMic);
        }
    }
}
