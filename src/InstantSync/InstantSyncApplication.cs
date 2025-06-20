using System;
using Autodesk.Revit.UI;
using System.Threading;
using InstantSync.Core.Commands;
using InstantSync.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InstantSync
{
    /// <summary>
    /// Application entry for Instant Sync add-in.
    /// </summary>
    public class InstantSyncApplication : IExternalApplication
    {
        private IHost? _host;
        private ExternalEvent? _pushEvent;
        private PushService? _pushService;

        /// <inheritdoc />
        public Result OnStartup(UIControlledApplication application)
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((ctx, services) =>
                {
                    services.AddSingleton<PushService>();
                    services.AddHostedService(sp => sp.GetRequiredService<PushService>());
                    services.AddSingleton<PullCommand>();
                    services.AddLogging(cfg => cfg.AddDebug());
                })
                .Build();

            _pushService = _host.Services.GetRequiredService<PushService>();
            _pushEvent = ExternalEvent.Create(new PushEventHandler(_pushService));

            RibbonPanel panel = application.CreateRibbonPanel("Instant Sync");
            PushButtonData pushData = new PushButtonData("Push", "\u0394 Push", typeof(PushEventHandler).Assembly.Location, typeof(PushEventHandler).FullName);
            PushButtonData pullData = new PushButtonData("Pull", "\u0394 Pull", typeof(PullCommand).Assembly.Location, typeof(PullCommand).FullName);
            panel.AddItem(pushData);
            panel.AddItem(pullData);

            return Result.Succeeded;
        }

        /// <inheritdoc />
        public Result OnShutdown(UIControlledApplication application)
        {
            _host?.Dispose();
            return Result.Succeeded;
        }

        private class PushEventHandler : IExternalEventHandler
        {
            private readonly PushService _service;

            public PushEventHandler(PushService service)
            {
                _service = service;
            }

            public void Execute(UIApplication app)
            {
                _service.FlushImmediately(CancellationToken.None).GetAwaiter().GetResult();
            }

            public string GetName() => nameof(PushEventHandler);
        }
    }
}
