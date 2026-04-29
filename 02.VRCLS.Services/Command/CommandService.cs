using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Serilog;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
using VRCLS.Services.Audio;

namespace VRCLS.Services.Command
{
    /// <summary>
    /// Experimental keyword-action service.
    /// Matches recognized text against user-defined scripts and executes their actions.
    /// </summary>
    public class CommandService : ICommandService, IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<CommandService>();

        private readonly AppConfig _config;
        private readonly ScriptExecutor _scriptExecutor;
        private readonly CommandFeedbackService _feedbackService;
        private readonly List<ICommandHandler> _userCommandHandlers = new();
        private bool _disposed;

        public bool IsEnabled { get; set; } = true;

        public event EventHandler<CommandExecutedEventArgs>? CommandExecuted;
        public event EventHandler<CommandActionEventArgs>? ActionRequested
        {
            add { }
            remove { }
        }

        public CommandService(AppConfig config, IOscService oscService)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _scriptExecutor = new ScriptExecutor(oscService ?? throw new ArgumentNullException(nameof(oscService)));
            _feedbackService = new CommandFeedbackService();
            _logger.Debug("Experimental keyword action service initialized");
        }

        public bool ProcessText(string recognizedText, string? routeHint = null)
        {
            if (!IsEnabled || !_config.ControlEnabled || string.IsNullOrWhiteSpace(recognizedText))
            {
                return false;
            }

            var commandText = recognizedText.Trim();
            _logger.Debug("Processing experimental keyword action text: {Text}", commandText);

            foreach (var handler in _userCommandHandlers)
            {
                if (handler.Matches(commandText))
                {
                    _ = ExecuteCommandAsync(handler, commandText, routeHint);
                    return true;
                }
            }

            return false;
        }

        private async Task ExecuteCommandAsync(ICommandHandler handler, string triggerText, string? routeHint)
        {
            try
            {
                _logger.Information("Executing keyword action: {Command}", handler.CommandName);

                if (_config.CommandFeedbackEnabled)
                {
                    _feedbackService.PlayFeedback();
                }

                var result = await handler.ExecuteAsync(triggerText, routeHint);

                var args = new CommandExecutedEventArgs
                {
                    CommandName = handler.CommandName,
                    Description = handler.Description,
                    Success = result.Success,
                    Message = result.Message,
                    TriggerText = triggerText,
                    FeedbackText = result.FeedbackText
                };

                CommandExecuted?.Invoke(this, args);
                _logger.Debug("Keyword action finished: {Command}, success={Success}", handler.CommandName, result.Success);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Keyword action execution failed: {Command}", handler.CommandName);

                CommandExecuted?.Invoke(this, new CommandExecutedEventArgs
                {
                    CommandName = handler.CommandName,
                    Description = handler.Description,
                    Success = false,
                    Message = $"Execution failed: {ex.Message}",
                    TriggerText = triggerText
                });
            }
        }

        public void RegisterDefaultCommands()
        {
            _logger.Debug("Built-in voice commands are disabled; experimental keyword actions rely on user scripts only.");
        }

        public void LoadUserScripts(List<VoiceScript> scripts)
        {
            _userCommandHandlers.Clear();

            if (scripts == null || scripts.Count == 0)
            {
                _logger.Debug("No experimental keyword actions to load");
                return;
            }

            foreach (var script in scripts)
            {
                if (string.IsNullOrWhiteSpace(script.Action))
                {
                    _logger.Warning("Skip invalid keyword action because Action is empty");
                    continue;
                }

                var handler = new UserScriptCommandHandler(script, _scriptExecutor);
                _userCommandHandlers.Add(handler);

                _logger.Debug(
                    "Loaded experimental keyword action: {Action} (triggers: {Triggers})",
                    script.Action,
                    string.Join(", ", script.Text ?? new List<string>()));
            }

            _logger.Information("Loaded {Count} experimental keyword actions", _userCommandHandlers.Count);
        }

        public void ClearCommands()
        {
            _userCommandHandlers.Clear();
            _logger.Debug("Cleared experimental keyword actions");
        }

        public List<string> GetRegisteredCommands()
        {
            var commands = new List<string>();

            foreach (var handler in _userCommandHandlers)
            {
                commands.Add($"[Script] {handler.CommandName}");
            }

            return commands;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _feedbackService.Dispose();
            _logger.Debug("CommandService disposed");
        }
    }
}
