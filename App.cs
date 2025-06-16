using Autodesk.Revit.UI;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using System;
using SpaceTracker;

namespace SpaceTracker
{
    public class App : IExternalApplication
    {
        public static ExternalEvent UpdateEvent;
        public static DatabaseUpdateHandler UpdateHandler;

        public Result OnStartup(UIControlledApplication application)
        {
            var sqliteConnector = CommandManager.Instance.SqlConnector;
            var extractor = new SpaceExtractor(CommandManager.Instance);
            UpdateHandler = new DatabaseUpdateHandler(sqliteConnector, extractor);
            UpdateEvent = ExternalEvent.Create(UpdateHandler);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            UpdateHandler = null;
            UpdateEvent = null;
            return Result.Succeeded;
        }
    }
}