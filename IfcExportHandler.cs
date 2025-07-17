using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SpaceTracker
{
    public class IfcExportHandler : IExternalEventHandler
    {
        public Document Document { get; set; }
        public List<ElementId> ElementIds { get; set; }
        public string ExportedPath { get; private set; }

        public void Execute(UIApplication app)
        {
            try
            {
                ExportedPath = new SpaceExtractor(CommandManager.Instance)
                    .ExportIfcSubset(Document, ElementIds);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("IFC Export Fehler", ex.Message);
            }
        }

        public string GetName() => "IFC Export Handler";
    }
}