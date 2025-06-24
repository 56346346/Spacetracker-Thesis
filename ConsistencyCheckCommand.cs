using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System;
using System.Collections.Generic;
using Neo4j.Driver;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;

namespace SpaceTracker
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ConsistencyCheckCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
             var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc != null)
            {
                SpaceTrackerClass.PerformConsistencyCheck(doc, true);
                return Result.Succeeded;
            }
            else
            {
                SpaceTrackerClass.SetStatusIndicator(SpaceTrackerClass.StatusColor.Red);
                return Result.Failed;
            }
        }
    }
}
