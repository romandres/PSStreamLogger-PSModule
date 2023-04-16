using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace PSStreamLoggerModule
{

    [Cmdlet(VerbsLifecycle.Invoke, "CommandWithLogging")]
    public class InvokeCommandWithLoggingCmdlet : PSCmdlet, IDisposable
    {
        [Parameter(Mandatory = true)]
        public ScriptBlock? ScriptBlock { get; set; }

        [Parameter(Mandatory = true)]
        public Logger[]? Loggers { get; set; }

        [Parameter]
        public RunMode RunMode { get; set; } = RunMode.NewScope;

        [Parameter]
        public SwitchParameter DisableStreamConfiguration { get; set; }

        private LogEventLevel minimumLogLevel;
        
        private ILoggerFactory? loggerFactory;

        private Microsoft.Extensions.Logging.ILogger? scriptLogger;

        private bool disposed;

        private DataRecordLogger? dataRecordLogger;

        private PowerShellExecutor? powerShellExecutor;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    loggerFactory?.Dispose();

                    if (Loggers is object)
                    {
                        foreach (var logger in Loggers)
                        {
                            logger.SerilogLogger.Dispose();
                        }
                    }

                    powerShellExecutor?.Dispose();
                }

                disposed = true;
            }
        }

        protected override void BeginProcessing()
        {
            PrepareLogging();
        }

        protected override void EndProcessing()
        {
            Func<Collection<PSObject>> exec;

            var streamConfiguration = !DisableStreamConfiguration.IsPresent ? new PSStreamConfiguration(minimumLogLevel) : null;

            if (RunMode != RunMode.NewRunspace)
            {
                StringBuilder logLevelCommandBuilder = new StringBuilder();
                
                if (streamConfiguration is object)
                {
                    foreach (var streamConfigurationItem in streamConfiguration.StreamConfiguration)
                    {
                        logLevelCommandBuilder.Append($"${streamConfigurationItem.Key} = \"{streamConfigurationItem.Value}\"; ");
                    }
                }

                exec = () =>
                {
                    return InvokeCommand.InvokeScript($"{logLevelCommandBuilder}& {{ {ScriptBlock} {Environment.NewLine}}} *>&1 | PSStreamLogger\\Out-PSStreamLogger -DataRecordLogger $input[0]", RunMode == RunMode.NewScope, PipelineResultTypes.Output, new List<object>() { dataRecordLogger! });
                };
            }
            else
            {
                string currentPath = SessionState.Path.CurrentLocation.Path;
                powerShellExecutor = new PowerShellExecutor(dataRecordLogger!, streamConfiguration, currentPath);

                exec = () =>
                {
                    return powerShellExecutor.Execute(ScriptBlock!.ToString());
                };
            }

            try
            {
                var output = exec.Invoke();
                WriteObject(output, true);
            }
            catch (RuntimeException ex)
            {
                dataRecordLogger!.LogRecord(ex.ErrorRecord);
                throw;
            }
        }

        private void PrepareLogging()
        {
            loggerFactory = new LoggerFactory();
            
            minimumLogLevel = Serilog.Events.LogEventLevel.Information;
            foreach (var logger in Loggers!)
            {
                if (logger.MinimumLogLevel < minimumLogLevel)
                {
                    minimumLogLevel = logger.MinimumLogLevel;
                }

                loggerFactory.AddSerilog(logger.SerilogLogger);
            }

            scriptLogger = loggerFactory.CreateLogger("PSScriptBlock");
            dataRecordLogger = new DataRecordLogger(scriptLogger, RunMode == RunMode.NewRunspace ? 0 : 2);
        }
    }
}
