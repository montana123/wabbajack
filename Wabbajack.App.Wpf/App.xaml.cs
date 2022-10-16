﻿using System;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using NLog.Targets;
using Orc.FileAssociation;
using ReactiveUI;
using Wabbajack.CLI.Builder;
using Wabbajack.DTOs;
using Wabbajack.DTOs.Interventions;
using Wabbajack.Interventions;
using Wabbajack.LoginManagers;
using Wabbajack.Models;
using Wabbajack.Paths.IO;
using Wabbajack.Services.OSIntegrated;
using Wabbajack.UserIntervention;
using Wabbajack.Util;
using WebView2.Runtime.AutoInstaller;

namespace Wabbajack
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App
    {
        private IHost _host;

        private void OnStartup(object sender, StartupEventArgs e)
        {
            WebView2AutoInstaller.CheckAndInstallAsync(false, false).Wait();
            
            RxApp.MainThreadScheduler = new DispatcherScheduler(Dispatcher.CurrentDispatcher);
            _host = Host.CreateDefaultBuilder(Array.Empty<string>())
                .ConfigureLogging(AddLogging)
                .ConfigureServices((host, services) =>
                {
                    ConfigureServices(services);
                })
                .Build();

            var args = e.Args;

            
            RxApp.MainThreadScheduler.Schedule(0, (_, _) =>
            {
                if (args.Length > 0)
                {
                    var builder = _host.Services.GetRequiredService<CommandLineBuilder>();
                    builder.Run(e.Args).ContinueWith(async x =>
                    {
                        Environment.Exit(await x);
                    });
                    return Disposable.Empty;
                }
                else
                {
                    var mainWindow = _host.Services.GetRequiredService<MainWindow>();
                    mainWindow!.Show();
                    return Disposable.Empty;
                }
            });
        }

        private void AddLogging(ILoggingBuilder loggingBuilder)
        {
            var config = new NLog.Config.LoggingConfiguration();

            var logFolder = KnownFolders.LauncherAwarePath.Combine("logs");
            if (!logFolder.DirectoryExists())
                logFolder.CreateDirectory();
            
            var fileTarget = new FileTarget("file")
            {
                FileName = logFolder.Combine("Wabbajack.current.log").ToString(),
                ArchiveFileName = logFolder.Combine("Wabbajack.{##}.log").ToString(),
                ArchiveOldFileOnStartup = true,
                MaxArchiveFiles = 10,
                Layout = "${processtime} [${level:uppercase=true}] (${logger}) ${message:withexception=true}",
                Header = "############ Wabbajack log file - ${longdate} ############"
            };
            
            var consoleTarget = new ConsoleTarget("console");
        
            var uiTarget = new LogStream
            {
                Name = "ui",
                Layout = "${message:withexception=false}",
            };
            
            loggingBuilder.Services.AddSingleton(uiTarget);

            config.AddRuleForAllLevels(fileTarget);
            config.AddRuleForAllLevels(consoleTarget);
            config.AddRuleForAllLevels(uiTarget);

            loggingBuilder.ClearProviders();
            loggingBuilder.SetMinimumLevel(LogLevel.Trace);
            loggingBuilder.AddNLog(config);
        }

        private static IServiceCollection ConfigureServices(IServiceCollection services)
        {
            services.AddOSIntegrated();

            // Orc.FileAssociation
            services.AddSingleton<IApplicationRegistrationService>(new ApplicationRegistrationService());

            services.AddSingleton<CefService>();
            services.AddSingleton<IUserInterventionHandler, UserIntreventionHandler>();
            
            services.AddTransient<MainWindow>();
            services.AddTransient<MainWindowVM>();
            services.AddTransient<BrowserWindow>();
            services.AddSingleton<SystemParametersConstructor>();
            services.AddSingleton<LauncherUpdater>();
            services.AddSingleton<ResourceMonitor>();

            services.AddSingleton<MainSettings>();
            services.AddTransient<CompilerVM>();
            services.AddTransient<InstallerVM>();
            services.AddTransient<ModeSelectionVM>();
            services.AddTransient<ModListGalleryVM>();
            services.AddTransient<CompilerVM>();
            services.AddTransient<InstallerVM>();
            services.AddTransient<SettingsVM>();
            services.AddTransient<WebBrowserVM>();
            
            // Login Handlers
            services.AddTransient<VectorPlexusLoginHandler>();
            services.AddTransient<NexusLoginHandler>();
            services.AddTransient<LoversLabLoginHandler>();
            
            // Login Managers
            services.AddAllSingleton<INeedsLogin, LoversLabLoginManager>();
            services.AddAllSingleton<INeedsLogin, NexusLoginManager>();
            services.AddAllSingleton<INeedsLogin, VectorPlexusLoginManager>();
            services.AddSingleton<ManualDownloadHandler>();
            services.AddSingleton<ManualBlobDownloadHandler>();
            
            // Verbs
            services.AddSingleton<CommandLineBuilder>();
            services.AddCLIVerbs();
            
            return services;
        }

        private void OnExit(object sender, ExitEventArgs e)
        {
            using (_host)
            {
                _host.StopAsync();
            }
            base.OnExit(e);
        }
    }
}
