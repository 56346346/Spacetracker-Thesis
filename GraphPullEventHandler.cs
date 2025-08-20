using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Threading.Tasks;

namespace SpaceTracker;

public class GraphPullEventHandler : IExternalEventHandler
{
    private readonly GraphPuller _puller;
    private readonly Document _doc;
    private readonly string _sessionId;

    public GraphPullEventHandler(GraphPuller puller, Document doc, string sessionId)
    {
        _puller = puller;
        _doc = doc;
        _sessionId = sessionId;
    }

    public void Execute(UIApplication app)
    {
        if (_doc.IsReadOnly || _doc.IsModifiable)
        {
            Logger.LogToFile("Dokument nicht bereit", "sync.log");
            return;
        }

        _puller.PullRemoteChanges(_doc, _sessionId);
    }

    public string GetName() => "GraphPullEventHandler";
}