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

    public async void Execute(UIApplication app)
    {
        if (_doc.IsReadOnly || _doc.IsModifiable)
        {
            Logger.LogToFile("Dokument nicht bereit", "sync.log");
            return;
        }

        await _puller.PullRemoteChanges(_doc, _sessionId);

        var solibriClient = new SolibriApiClient(SpaceTrackerClass.SolibriApiPort);
        _ = Task.Run(async () =>
        {
            Logger.LogToFile("Starte Solibri Check (Graph Pull)", "solibri.log");
            try
            {
                var results = await solibriClient
                    .RunRulesetCheckAsync(SpaceTrackerClass.SolibriModelUUID)
                    .ConfigureAwait(false);

                var status = SpaceTrackerClass.StatusColor.Green;
                foreach (var clash in results)
                {
                    var sev = clash.Severity?.Trim().ToUpperInvariant();
                    if (sev == "ROT" || sev == "RED" || sev == "ERROR" ||
                        sev == "HIGH" || sev == "CRITICAL")
                    {
                        status = SpaceTrackerClass.StatusColor.Red;
                        break;
                    }
                    if (sev == "GELB" || sev == "YELLOW" || sev == "WARNING" ||
                        sev == "MEDIUM")
                    {
                        if (status != SpaceTrackerClass.StatusColor.Red)
                            status = SpaceTrackerClass.StatusColor.Yellow;
                    }
                }
                SpaceTrackerClass.SetStatusIndicator(status);
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Solibri Modellprüfung (GraphPull)", ex);
            }

            Logger.LogToFile("Solibri Check (Graph Pull) abgeschlossen", "solibri.log");
        });
    }

    public string GetName() => "GraphPullEventHandler";
}