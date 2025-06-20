namespace Autodesk.Revit.UI
{
    public enum Result { Succeeded, Failed, Cancelled }

    public interface IExternalApplication
    {
        Result OnStartup(UIControlledApplication application);
        Result OnShutdown(UIControlledApplication application);
    }

    public class UIControlledApplication
    {
        public RibbonPanel CreateRibbonPanel(string name) => new RibbonPanel();
    }

    public class UIApplication
    {
        public UIDocument ActiveUIDocument => new UIDocument();
    }

    public class UIDocument
    {
        public Autodesk.Revit.DB.Document Document => new Autodesk.Revit.DB.Document();
    }

    public class RibbonPanel
    {
        public void AddItem(PushButtonData data) { }
    }

    public class PushButtonData
    {
        public PushButtonData(string name, string text, string assembly, string className) { }
    }

    public interface IExternalCommand
    {
        Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements);
    }

    public class ExternalCommandData
    {
        public UIApplication Application => new UIApplication();
    }

    public interface IExternalEventHandler
    {
        void Execute(UIApplication app);
        string GetName();
    }

    public class ExternalEvent
    {
        private ExternalEvent(IExternalEventHandler handler) { }
        public static ExternalEvent Create(IExternalEventHandler handler) => new ExternalEvent(handler);
    }

    namespace Forms
    {
        public class OpenFileDialog
        {
            public bool Multiselect { get; set; }
            public string Filter { get; set; } = string.Empty;
            public bool ShowDialog() => true;
            public string[] GetFileNames() => System.Array.Empty<string>();
        }
    }
}