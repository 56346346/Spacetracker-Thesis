using System;
using System.Windows;

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Microsoft.Extensions.Logging;
using Autodesk.Revit.DB.Events;
using Microsoft.Extensions.DependencyInjection;
using Autodesk.Revit.Exceptions;
using Autodesk.Revit.UI;
using System.Reflection;
using Newtonsoft.Json;
using Neo4j.Driver;
using System.Windows.Forms;
using System.IO;
using SpaceTracker;
using System.Windows.Media;           // für ImageSource
using System.Windows.Media.Imaging;
using System.Net.Http;
using System.Collections.Concurrent;
using System.Threading;
using Autodesk.Revit.ApplicationServices;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;
using System.Runtime.Versioning;
using static System.Environment;

[assembly: SupportedOSPlatform("windows")]


namespace SpaceTracker
{
    public class SpaceTrackerClass : IExternalApplication
    {
        private static readonly string _logDir =
          Path.Combine(GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceTracker", "log");
        private static readonly string _logPath =
            Path.Combine(_logDir, nameof(SpaceTrackerClass) + ".log");
        private static readonly object _logLock = new object();

        private RibbonPanel _ribbonPanel;


        private Neo4jConnector _neo4jConnector;
        private DatabaseUpdateHandler _databaseUpdateHandler;
        private GraphPuller _graphPuller;
        private PullEventHandler _pullEventHandler;
        private PullScheduler _pullScheduler;
        private ExternalEvent _graphPullEvent;

        public const int SolibriApiPort = 10876;

        // Statischer Konstruktor stellt sicher, dass das Log-Verzeichnis existiert
        static SpaceTrackerClass()
        {
            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
        private readonly Dictionary<ElementId, ElementMetadata> _elementCache =
       new Dictionary<ElementId, ElementMetadata>();
        private SpaceExtractor _extractor;
        private CommandManager _cmdManager;
        private static IfcExportHandler _exportHandler;
        private static ExternalEvent _exportEvent;
        private static void LogMethodCall(string methodName, IDictionary<string, object> args)
        {
            // Remove Document instances to avoid WorksharingCentralGUID errors
            var safeArgs = args
                .Where(kv => !(kv.Value is Autodesk.Revit.DB.Document))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            string SerializeSafe(object v)
            {
                if (v == null) return "null";
                if (v is string) return $"\"{v}\"";
                try { return JsonConvert.SerializeObject(v); }
                catch { return v.ToString(); }
            }

            var line = methodName + "(" +
                string.Join(", ",
                    safeArgs.Select(kv => $"{kv.Key}={SerializeSafe(kv.Value)}")
                ) + ")";
            lock (_logLock)
            {
                using var fs = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(fs) { AutoFlush = true };
                writer.WriteLine(line);
            }
        }

        // Holds the currently loaded Solibri model UUID. This is initialized
        // with the default value but may change if Solibri creates a new model
        // (e.g. when the existing one was removed).
        // Wird erst beim initialen Import gesetzt und danach bei jeder
        // Prüfung aktualisiert.
        public static string SolibriModelUUID = string.Empty; public const string SolibriRulesetPath = "C:/Users/Public/Solibri/SOLIBRI/Regelsaetze/RegelnThesis/DeltaRuleset.cset";
        public static string SolibriRulesetId;

        public static PushButton StatusIndicatorButton;
        internal static ImageSource GreenIcon, YellowIcon, RedIcon;

        // Ampel-Status Enum
        public enum StatusColor { Green, Yellow, Red }

        // Methode zum Aktualisieren des Ampel-Icons
        public static void SetStatusIndicator(StatusColor status)
        {
            LogMethodCall(nameof(SetStatusIndicator), new Dictionary<string, object>
            {
                { "status", status }
            });
            if (StatusIndicatorButton == null) return;
            switch (status)
            {
                case StatusColor.Green:
                    StatusIndicatorButton.LargeImage = GreenIcon;
                    StatusIndicatorButton.Image = GreenIcon;
                    StatusIndicatorButton.ToolTip = "Status: Konsistent (Grün)";
                    break;
                case StatusColor.Yellow:
                    StatusIndicatorButton.LargeImage = YellowIcon;
                    StatusIndicatorButton.Image = YellowIcon;
                    StatusIndicatorButton.ToolTip = "Status: Warnungen vorhanden (Gelb)";
                    break;
                case StatusColor.Red:
                    StatusIndicatorButton.LargeImage = RedIcon;
                    StatusIndicatorButton.Image = RedIcon;
                    StatusIndicatorButton.ToolTip = "Status: Inkonsistenzen erkannt (Rot)";
                    break;
            }
        }

        // Updates the consistency checker after a ruleset validation. Executed
        // on the UI thread to immediately reflect the validation result.
        public static void UpdateConsistencyCheckerButton(Severity severity)
        {
            LogMethodCall(nameof(UpdateConsistencyCheckerButton), new Dictionary<string, object>
            {
                { "severity", severity }
            });
            switch (severity)
            {
                case Severity.Error:
                    SetStatusIndicator(StatusColor.Red);
                    break;
                case Severity.Warning:
                    SetStatusIndicator(StatusColor.Yellow);
                    break;
                default:
                    SetStatusIndicator(StatusColor.Green);
                    break;
            }
        }

        // Runs a consistency check against Neo4j and updates the status
        // indicator. Dialog messages can be suppressed with the showDialogs
        // flag.
        // Vergleicht lokale Änderungen mit dem Graphen und setzt die Ampel. Optionale Dialoge informieren den Nutzer.
        public static void PerformConsistencyCheck(Document doc, bool showDialogs)
        {
            LogMethodCall(nameof(PerformConsistencyCheck), new Dictionary<string, object>
            {
                { "doc", doc },
                { "showDialogs", showDialogs }
            });
            var cmdMgr = CommandManager.Instance;
            var connector = cmdMgr.Neo4jConnector;


            //Solibri-Check ausführen
            // und die Status-Ampel entsprechend der höchsten gefundenen
            // Fehlerstufe setzen
            try
            {
                var errs = SolibriRulesetValidator.Validate(doc);
                var sev = errs.Count == 0 ? Severity.Info : errs.Max(e => e.Severity);
                UpdateConsistencyCheckerButton(sev);

                if (showDialogs)
                {
                    int errCount = errs.Count(e => e.Severity == Severity.Error);
                    int warnCount = errs.Count(e => e.Severity == Severity.Warning);
                    Autodesk.Revit.UI.TaskDialog.Show(
                        "Consistency Check",
                        $"Solibri-Prüfung abgeschlossen: {errCount} Fehler, {warnCount} Warnungen.");
                }

                var mappingIssues = ValidateElementMappings(doc, connector);
                if (mappingIssues.Count > 0 && showDialogs)
                {
                    Autodesk.Revit.UI.TaskDialog.Show(
                        "Consistency Check",
                        "Nicht zugeordnete Datensätze gefunden:\n" + string.Join("\n", mappingIssues));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ConsistencyCheck] Solibri validation failed: {ex.Message}");
            }
        }
        private static List<string> ValidateElementMappings(Document doc, Neo4jConnector connector)
        {
            const string cypher =
                           "MATCH (n) WHERE n.elementId IS NOT NULL AND n.elementId >= 0 " +
                           "RETURN labels(n) AS labels, n.elementId AS id";

            var records = Task.Run(() => connector.RunReadQueryAsync(cypher)).Result;
            var issues = new List<string>();
            var seen = new HashSet<string>();

            foreach (var r in records)
            {
                long id = r["id"].As<long>();
                var labels = string.Join(',', r["labels"].As<List<object>>());
                string key = $"{id}:{labels}";

                if (!seen.Add(key))
                    continue; // avoid duplicate messages for the same node
                if (doc.GetElement(new ElementId((int)id)) == null)
                    issues.Add($"Missing element {id} for node [{labels}]");
            }
            return issues;
        }

        /// <summary>
        /// Applies graphical overrides to elements according to their severity.
        /// "RED" and "YELLOW" entries will be colored, "GREEN" removes overrides.
        /// </summary>
        public static void MarkElementsBySeverity(Dictionary<ElementId, string> severityMap)
        {
            LogMethodCall(nameof(MarkElementsBySeverity), new Dictionary<string, object>
            {
                { "severityMap", severityMap }
            });
            if (severityMap == null || severityMap.Count == 0)
                return;

            var session = SessionManager.OpenSessions.Values.FirstOrDefault();
            var doc = session?.Document;
            if (doc == null)
                return;

            var view = doc.ActiveView;
            using var tx = new Transaction(doc, "Mark Issue Elements");
            tx.Start();
            foreach (var kvp in severityMap)
            {
                var ogs = new OverrideGraphicSettings();
                if (kvp.Value.Equals("RED", StringComparison.OrdinalIgnoreCase))
                {
                    var color = new Autodesk.Revit.DB.Color(255, 0, 0);
                    ogs.SetProjectionLineColor(color);
                    ogs.SetCutLineColor(color);
                }
                else if (kvp.Value.Equals("YELLOW", StringComparison.OrdinalIgnoreCase))
                {
                    var color = new Autodesk.Revit.DB.Color(255, 255, 0);
                    ogs.SetProjectionLineColor(color);
                    ogs.SetCutLineColor(color);
                }
                view.SetElementOverrides(kvp.Key, ogs);
            }
            tx.Commit();
        }

        internal static IfcExportHandler ExportHandler => _exportHandler;
        internal static ExternalEvent ExportEvent => _exportEvent;

        public static void RequestIfcExport(Document doc, List<ElementId> ids)
        {
            LogMethodCall(nameof(RequestIfcExport), new Dictionary<string, object>
            {
                { "doc", doc },
                { "ids", ids }
            });
            _exportHandler.Document = doc;
            _exportHandler.ElementIds = ids;
            if (!_exportEvent.IsPending)
                _exportEvent.Raise();
        }


        #region register events
        // Wird beim Laden des Add-Ins aufgerufen und richtet alle Komponenten ein.
        public Result OnStartup(UIControlledApplication application)
        {
            LogMethodCall(nameof(OnStartup), new Dictionary<string, object>
            {
                { "application", application.GetType().Name }
            });

            var logDir = Path.Combine(
                           Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                           "SpaceTracker", "log");

            // Stelle sicher, dass der Ordner existiert
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);
            // Nur Inhalte löschen, nicht den Ordner selbst

            foreach (var file in Directory.GetFiles(logDir))
            {
                // Truncate statt Delete, um Sperrkonflikte zu vermeiden
                using (var fs = new FileStream(
                    file,
                    FileMode.Truncate,
                    FileAccess.Write,
                    FileShare.ReadWrite))
                { }
            }
            try
            {
                using var loggerFactory = LoggerFactory.Create(b => b.AddDebug());
                _neo4jConnector = new Neo4jConnector(loggerFactory.CreateLogger<Neo4jConnector>());

                CommandManager.Initialize(_neo4jConnector);

                var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
                services.AddHttpClient("solibri", c => c.BaseAddress = new Uri("http://localhost:10876/solibri/v1/"));
                var provider = services.BuildServiceProvider();
                var factory = provider.GetRequiredService<IHttpClientFactory>();
                SolibriChecker.Initialize(factory, _neo4jConnector);
                _extractor = new SpaceExtractor(CommandManager.Instance);
                _databaseUpdateHandler = new DatabaseUpdateHandler(_extractor);
                _graphPuller = new GraphPuller(_neo4jConnector);
                var graphPullHandler = new GraphPullHandler();
                _graphPullEvent = ExternalEvent.Create(graphPullHandler);
                _cmdManager = CommandManager.Instance;
                _pullEventHandler = new PullEventHandler();
                _exportHandler = new IfcExportHandler();
                _exportEvent = ExternalEvent.Create(_exportHandler);
                var uiapp = TryGetUIApplication(application);

            }
            catch (Exception ex)
            {
                Logger.LogCrash("OnStartup init", ex);
                return Result.Failed;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    SolibriProcessManager.EnsureStarted();
                    var client = new SolibriApiClient(SolibriApiPort);
                    SolibriRulesetId = await client.ImportRulesetAsync(SolibriRulesetPath).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("Ruleset-Import", ex);
                }
            });
            System.Windows.Forms.Application.ThreadException += (sender, args) =>
          {
              var crashPath = Path.Combine(
                  Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                  "SpaceTracker",
                  "crash.log");
              lock (_logLock)
              {
                  using var stream = new FileStream(
                      crashPath,
                      FileMode.Append,
                      FileAccess.Write,
                      FileShare.ReadWrite);
                  using var writer = new StreamWriter(stream) { AutoFlush = true };
                  writer.WriteLine($"{DateTime.Now:O} UI Thread Exception: {args.Exception}");
              }
          };

            // 1. Logging-Pfade in Benutzerverzeichnis verlegen
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string mainLogPath = Path.Combine(logDir, "SpaceTracker.log");
            string crashLogPath = Path.Combine(logDir, "SpaceTracker_crash.log");
            string assemblyCheckPath = Path.Combine(logDir, "SpaceTracker_Assembly_Check.log");

            // 2. Ordnerstruktur sicher erstellen
            Directory.CreateDirectory(logDir);

            // 3. Zentralisierte Logging-Methode


            try
            {
                // 4. Debugger-Logging initialisieren
                Debug.WriteLine("[SpaceTracker] OnStartup initialisiert");

                // 5. Assembly-Versionen protokollieren
                var revitApiVersion = typeof(Document).Assembly.GetName().Version;
                var revitUIVersion = typeof(UIApplication).Assembly.GetName().Version;
                var addinVersion = Assembly.GetExecutingAssembly().GetName().Version; ;

                // 8. Ribbon-UI erstellen
                CreateRibbonUI(application);

                // 9. Events registrieren
                RegisterDocumentEvents(application);
                Logger.LogToFile("Document-Events registriert");

                string innerappDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string spaceTrackerDir = Path.Combine(innerappDataPath, "SpaceTracker");
                string syncFile = Path.Combine(spaceTrackerDir, $"last_sync_{CommandManager.Instance.SessionId}.txt"); if (File.Exists(syncFile))
                {
                    try
                    {
                        string stamp = File.ReadAllText(syncFile);
                        CommandManager.Instance.LastSyncTime = DateTime.Parse(stamp);
                    }
                    catch { CommandManager.Instance.LastSyncTime = DateTime.MinValue; }
                }
                else
                {
                    CommandManager.Instance.LastSyncTime = DateTime.MinValue;
                }

                // 10. Falls bereits ein Dokument geöffnet ist, initiale Treppen und andere Elemente übernehmen
                UIApplication uiApp = TryGetUIApplication(application);
                if (uiApp != null && uiApp.ActiveUIDocument != null)
                {
                    InitializeExistingElements(uiApp.ActiveUIDocument.Document);
                    _databaseUpdateHandler.TriggerPush();
                    string key = uiApp.ActiveUIDocument.Document.PathName ?? uiApp.ActiveUIDocument.Document.Title;
                    SessionManager.AddSession(key, new Session(uiApp.ActiveUIDocument.Document));

                    ImportInitialSolibriModel(uiApp.ActiveUIDocument.Document);

                }

                // --- AutoPull: Periodisches Pull im Leerlauf (1 s) ---
                var autoPullHandler = new AutoPullHandler();
                var autoPullEvent = ExternalEvent.Create(autoPullHandler);
                if (uiApp != null)
                    _pullScheduler = new PullScheduler(autoPullEvent, uiApp);


                Logger.LogToFile("OnStartup erfolgreich abgeschlossen");
                return Result.Succeeded;
            }
            catch (Exception e)
            {
                var fullStack = e.StackTrace ?? string.Empty;
                var snippet = fullStack.Length <= 500
                 ? fullStack
                  : fullStack.Substring(0, 500);
                // 10. Detaillierte Fehlerprotokollierung
                string errorDetails = $"KRITISCHER FEHLER:\n" +
     $"Message: {e.Message}\n" +
     $"Stack: {snippet}\n" +
     $"Inner: {e.InnerException?.Message}";

                Logger.LogToFile(errorDetails, "crash.log");
                Debug.WriteLine("[SpaceTracker] KRITISCHER FEHLER: " + e.Message);
                return Result.Failed;
            }
        }

        private static UIApplication TryGetUIApplication(UIControlledApplication app)
        {
            try
            {
                return (UIApplication)Activator.CreateInstance(
                    typeof(UIApplication),
                    app.ControlledApplication);
            }
            catch
            {
                try
                {
                    var ctrl = app.ControlledApplication;
                    var prop = ctrl.GetType().GetProperty("Application");
                    var baseApp = prop?.GetValue(ctrl);
                    if (baseApp != null)
                    {
                        return (UIApplication)Activator.CreateInstance(
                            typeof(UIApplication),
                            baseApp);
                    }
                }
                catch
                {
                }
            }
            return null;
        }
        private void CreateRibbonUI(UIControlledApplication application)
        {
            // 1. SpaceTracker RibbonPanel sicherstellen (ggf. erstellen)
            IList<RibbonPanel> panels = application.GetRibbonPanels("Add-Ins") ?? new List<RibbonPanel>();
            _ribbonPanel = panels.FirstOrDefault(p => p.Name == "SpaceTracker")
                           ?? application.CreateRibbonPanel("SpaceTracker");

            Logger.LogToFile("Erstelle Ribbon-UI");
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            // 2. Verhindern, dass Buttons doppelt angelegt werden
            bool exportExists = _ribbonPanel.GetItems().OfType<PushButton>().Any(b => b.Name == "ExportButton");
            if (!exportExists)
            {
                // Existierenden Export-Button hinzufügen (falls noch nicht)
                var exportBtnData = new PushButtonData(
                    "ExportButton", "Export to Neo4j",
                    Assembly.GetExecutingAssembly().Location,
                    "SpaceTracker.ExportCommand"
                );

                string iconPath = Path.Combine(assemblyDir, "Logo.png");
                var exportIcon = new BitmapImage();
                exportIcon.BeginInit();
                exportIcon.UriSource = new Uri(iconPath, UriKind.Absolute);
                exportIcon.EndInit();
                exportBtnData.LargeImage = exportIcon;
                var exportBtn = _ribbonPanel.AddItem(exportBtnData) as PushButton;
                exportBtn.ToolTip = "Export all data to Neo4j";
            }

            // 3. Pull-Button (entfernte Änderungen holen)

            if (!_ribbonPanel.GetItems().OfType<PushButton>().Any(b => b.Name == "PullChangesButton"))
            {
                var pullBtnData = new PushButtonData(
                    "PullChangesButton", "Pull Changes",
                    Assembly.GetExecutingAssembly().Location,
                    "SpaceTracker.PullCommand"
                );
                string pullIconPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Pull.png");
                if (File.Exists(pullIconPath))
                {
                    var pullIcon = new BitmapImage();
                    pullIcon.BeginInit();
                    pullIcon.UriSource = new Uri(pullIconPath, UriKind.Absolute);
                    pullIcon.EndInit();
                    pullBtnData.LargeImage = pullIcon;
                }
                var pullBtn = _ribbonPanel.AddItem(pullBtnData) as PushButton;
                pullBtn.ToolTip = "Holt neueste Änderungen vom Neo4j-Graph in das lokale Modell (Pull)";
            }

            // 4. Consistency-Check-Button (Ampelanzeige für Status)
            bool checkExists = _ribbonPanel.GetItems().OfType<PushButton>().Any(b => b.Name == "ConsistencyCheckButton");
            if (!checkExists)
            {
                var checkBtnData = new PushButtonData(
                     "ConsistencyCheckButton", "Consistency Check",
                     Assembly.GetExecutingAssembly().Location,
                     "SpaceTracker.ConsistencyCheckCommand"
                 );

                // Ampel-Icons laden (Grün, Gelb, Rot)

                string greenIconPath = Path.Combine(assemblyDir, "Green.png");
                string yellowIconPath = Path.Combine(assemblyDir, "Yellow.png");
                string redIconPath = Path.Combine(assemblyDir, "Red.png");

                BitmapImage greenIcon = null, yellowIcon = null, redIcon = null;

                if (File.Exists(greenIconPath))
                {
                    greenIcon = new BitmapImage();
                    greenIcon.BeginInit();
                    greenIcon.UriSource = new Uri(greenIconPath, UriKind.Absolute);
                    greenIcon.EndInit();
                    checkBtnData.LargeImage = greenIcon; // initial auf Grün (angenommen konsistent)
                }

                if (File.Exists(yellowIconPath))
                {
                    yellowIcon = new BitmapImage();
                    yellowIcon.BeginInit();
                    yellowIcon.UriSource = new Uri(yellowIconPath, UriKind.Absolute);
                    yellowIcon.EndInit();
                }

                if (File.Exists(redIconPath))
                {
                    redIcon = new BitmapImage();
                    redIcon.BeginInit();
                    redIcon.UriSource = new Uri(redIconPath, UriKind.Absolute);
                    redIcon.EndInit();
                }

                var checkBtn = _ribbonPanel.AddItem(checkBtnData) as PushButton;
                checkBtn.ToolTip = "Prüft die Konsistenz zwischen lokalem Modell und Neo4j-Graph (Ampelanzeige)";

                // Referenzen für dynamische Ampelanzeige speichern
                SpaceTrackerClass.StatusIndicatorButton = checkBtn;
                SpaceTrackerClass.GreenIcon = greenIcon;
                SpaceTrackerClass.YellowIcon = yellowIcon;
                SpaceTrackerClass.RedIcon = redIcon;
            }

            // 6. Info-Button (zeigt Beschreibung der Funktionen)
            if (!_ribbonPanel.GetItems().OfType<PushButton>().Any(b => b.Name == "InfoButton"))
            {
                var infoBtnData = new PushButtonData(
                    "InfoButton", "Info",
                    Assembly.GetExecutingAssembly().Location,
                    "SpaceTracker.InfoCommand"
                );
                string infoIconPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Info.png");
                if (File.Exists(infoIconPath))
                {
                    var infoIcon = new BitmapImage();
                    infoIcon.BeginInit();
                    infoIcon.UriSource = new Uri(infoIconPath, UriKind.Absolute);
                    infoIcon.EndInit();
                    infoBtnData.LargeImage = infoIcon;
                }
                var infoBtn = _ribbonPanel.AddItem(infoBtnData) as PushButton;
                if (infoBtn != null)
                    infoBtn.ToolTip = "Zeigt eine kurze Beschreibung der SpaceTracker-Buttons";
            }
        }
        private void RegisterDocumentEvents(UIControlledApplication app)
        {
            var ctrl = app.ControlledApplication;
            ctrl.DocumentCreated += documentCreated;
            ctrl.DocumentOpened += documentOpened;
            ctrl.DocumentChanged += documentChangedHandler;
            ctrl.DocumentClosing += documentClosing;
        }
        private void InitializeExistingElements(Document doc)
        {
            try
            {
                // Sicherheitschecks
                if (doc == null || doc.IsLinked) return;

                var filter = new LogicalOrFilter(new List<ElementFilter>
{
    new ElementCategoryFilter(BuiltInCategory.OST_Walls),
    new ElementCategoryFilter(BuiltInCategory.OST_Rooms),
    new ElementCategoryFilter(BuiltInCategory.OST_Levels),
    new ElementCategoryFilter(BuiltInCategory.OST_Doors),
      new ElementCategoryFilter(BuiltInCategory.OST_PipeCurves),
    new ElementCategoryFilter(BuiltInCategory.OST_PipeSegments),
    new ElementCategoryFilter(BuiltInCategory.OST_Stairs),
    new ElementCategoryFilter(BuiltInCategory.OST_GenericModel)

});

                // Reine Leseoperationen benötigen keine Transaktion
                var elements = new FilteredElementCollector(doc)
                    .WherePasses(filter)
                    .WhereElementIsNotElementType()
                    .ToList();

                var changeData = new ChangeData
                {
                    AddedElements = elements,
                    ModifiedElements = new List<Element>(),
                    DeletedElementIds = new List<ElementId>(),
                    DeletedUids = new List<string>()
                };
                _databaseUpdateHandler.EnqueueChange(changeData);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Init Error] {ex.Message}");
            }
        }

        private void UpdateElementCache(
        List<Element> addedElements,
        List<Element> modifiedElements,
        List<ElementId> deletedIds)
        {
            foreach (var element in addedElements.Concat(modifiedElements))
            {
                var el = element;
                if (!_elementCache.TryGetValue(el.Id, out var meta))
                {
                    _elementCache[el.Id] = new ElementMetadata
                    {
                        Id = el.Id,
                        Type = el.GetType().Name,
                        Name = el.Name ?? "Unnamed",
                        Uid = ParameterUtils.GetNeo4jUid(el)
                    };
                }
                else
                {
                    meta.Name = el.Name ?? meta.Name;
                    meta.Uid = ParameterUtils.GetNeo4jUid(el);
                }
            }
            foreach (var delId in deletedIds)
            {
                _elementCache.Remove(delId);
            }
        }
        private static List<Element> GetAddedElements(DocumentChangedEventArgs e, Document doc)
        {
            return e.GetAddedElementIds()
                   .Select(id => doc.GetElement(id))
                   .Where(el => el != null)
                   .ToList();
        }

        private static List<Element> GetModifiedElements(DocumentChangedEventArgs e, Document doc)
        {
            return e.GetModifiedElementIds()
                   .Select(id => doc.GetElement(id))
                   .Where(el => el != null)
                   .ToList();
        }
        // Aufräumarbeiten beim Beenden von Revit.
        public Result OnShutdown(UIControlledApplication application)
        {
            LogMethodCall(nameof(OnShutdown), new Dictionary<string, object>
            {
                { "application", application }
            });
            CommandManager.Instance.Dispose();
            application.ControlledApplication.DocumentOpened -= documentOpened;
            application.ControlledApplication.DocumentChanged -= documentChangedHandler;
            application.ControlledApplication.DocumentCreated -= documentCreated;
            application.ControlledApplication.DocumentClosing -= documentClosing;
            _neo4jConnector?.Dispose();
            _pullScheduler?.Dispose();
            return Result.Succeeded;

        }

        /// <summary>
        /// Triggers a pull of the latest changes for all open sessions.
        /// </summary>
        private void PullChanges()
        {
            foreach (var openSession in SessionManager.OpenSessions.Values)
            {
                _pullEventHandler?.RequestPull(openSession.Document);
            }
        }

        // Exportiert das gesamte aktuelle Modell nach IFC und importiert es in
        // Solibri, wenn noch kein Modell geladen wurde.
        private void ImportInitialSolibriModel(Document doc)
        {
            try
            {
                if (doc == null || doc.IsLinked)
                    return;

                if (!string.IsNullOrEmpty(SolibriModelUUID))
                    return;

                var allIds = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Select(e => e.Id)
                    .ToList();

                RequestIfcExport(doc, allIds);
                string ifcPath = ExportHandler.ExportedPath;
                if (string.IsNullOrWhiteSpace(ifcPath) || !File.Exists(ifcPath))
                {
                    Logger.LogToFile("IFC-Export fehlgeschlagen. Versuche erneut.", "solibri.log");
                    RequestIfcExport(doc, allIds);
                    ifcPath = ExportHandler.ExportedPath;
                    if (string.IsNullOrWhiteSpace(ifcPath) || !File.Exists(ifcPath))
                    {
                        Logger.LogToFile("IFC-Export weiterhin fehlgeschlagen, breche Solibri-Import ab.", "solibri.log");
                        return;
                    }
                }
                var client = new SolibriApiClient(SolibriApiPort);

                if (string.IsNullOrEmpty(SolibriRulesetId))
                    SolibriRulesetId = client
                        .ImportRulesetAsync(SolibriRulesetPath)
                        .GetAwaiter().GetResult();

                SolibriModelUUID = client
                    .ImportIfcAsync(ifcPath)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Solibri Initial Import", ex);
            }
        }



        #endregion

        #region Event handler

        private async Task documentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                Document doc = e.GetDocument();
                if (doc == null || doc.IsLinked) return;

                var filter = new LogicalOrFilter(new List<ElementFilter>{
    new ElementCategoryFilter(BuiltInCategory.OST_Walls),
    new ElementCategoryFilter(BuiltInCategory.OST_Rooms),
    new ElementCategoryFilter(BuiltInCategory.OST_Doors),
     new ElementCategoryFilter(BuiltInCategory.OST_PipeCurves),
    new ElementCategoryFilter(BuiltInCategory.OST_PipeSegments),
    new ElementCategoryFilter(BuiltInCategory.OST_Levels),
    new ElementCategoryFilter(BuiltInCategory.OST_Stairs),
    new ElementCategoryFilter(BuiltInCategory.OST_GenericModel)
                });

                // 2. Änderungen identifizieren
                var addedIds = e.GetAddedElementIds(filter);
                var deletedIds = e.GetDeletedElementIds();
                var deletedUids = deletedIds
                   .Select(id => _elementCache.TryGetValue(id, out var meta) ? meta.Uid : null)
                   .Where(uid => !string.IsNullOrEmpty(uid))
                   .ToList();
                var modifiedIds = e.GetModifiedElementIds(filter);

                // 3. Early Exit bei keinen relevanten Änderungen


                // 4. Elemente aus Dokument holen (mit Null-Check)
                var addedElements = GetAddedElements(e, doc).Where(el => filter.PassesFilter(el)).ToList();

                var modifiedElements = GetModifiedElements(e, doc).Where(el => filter.PassesFilter(el)).ToList();
                if (addedElements.Count == 0 &&
                 modifiedElements.Count == 0 &&
                 deletedIds.Count == 0 &&
                 addedIds.Count == 0 &&
                 modifiedIds.Count == 0)
                    return;


                // 5. Element-Cache aktualisieren
                UpdateElementCache(addedElements, modifiedElements, deletedIds.ToList());

                // 6. ChangeData erstellen
                var changeData = new ChangeData
                {
                    AddedElements = addedElements,
                    DeletedElementIds = deletedIds.ToList(),
                    DeletedUids = deletedUids,
                    ModifiedElements = modifiedElements
                };

                // 7. Änderungen zur Verarbeitung einreihen
                _databaseUpdateHandler.EnqueueChange(changeData);
                // Direkt nach dem Einreihen einen Push anstoßen, damit die
                // Änderungen ohne manuelle Aktion nach Neo4j gelangen
                _databaseUpdateHandler.TriggerPush();
                try
                {
                    string sessionId = CommandManager.Instance.SessionId;
                    foreach (var el in addedElements)
                    {
                        await _neo4jConnector.CreateLogChangeAsync(el.Id.Value, ChangeType.Add, sessionId);
                    }
                    foreach (var el in modifiedElements)
                    {
                        await _neo4jConnector.CreateLogChangeAsync(el.Id.Value, ChangeType.Modify, sessionId);
                    }
                    foreach (var id in deletedIds)
                    {
                        await _neo4jConnector.CreateLogChangeAsync(id.Value, ChangeType.Delete, sessionId);
                    }
                    PullChanges();
                    var ids = addedElements.Concat(modifiedElements).Select(e => e.Id).Distinct();
                    foreach (var cid in ids)
                        await SolibriChecker.CheckElementAsync(cid, doc);
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("RealtimeSync", ex);
                }
                foreach (var openSession in SessionManager.OpenSessions.Values)
                {
                    // trigger pull command via external event to keep sessions in sync
                    _pullEventHandler?.RequestPull(openSession.Document);
                }
                _graphPullEvent.Raise();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Critical Error] documentChanged: {ex.Message}");
                Logger.LogToFile($"DocumentChanged Exception: {ex}\n", "crash");
            }
        }

    // Wrapper for event subscription as DocumentChangedEventHandler expects a void return type
        private async void documentChangedHandler(object sender, DocumentChangedEventArgs e)
        {
            await documentChanged(sender, e);
        }


        private void documentCreated(object sender, DocumentCreatedEventArgs e)
        {
            try
            {
                InitializeExistingElements(e.Document);
                // Nach dem Initialisieren bereits vorhandener Elemente direkt
                // die aktuellen Befehle an Neo4j senden
                _databaseUpdateHandler.TriggerPush();
                PullChanges();

                string key = e.Document.PathName ?? e.Document.Title;
                SessionManager.AddSession(key, new Session(e.Document));

                ImportInitialSolibriModel(e.Document);

            }
            catch (Exception ex)
            {
                Logger.LogCrash("DocumentCreated", ex);
            }
        }

        private void documentOpened(object sender, DocumentOpenedEventArgs e)
        {
            Document doc = e.Document;
            try
            {
                // Prüfen, ob der Neo4j-Graph bereits Daten enthält (z.B. Building-Knoten)
                const string checkQuery = "MATCH (n) RETURN count(n) AS nodeCount";
                var records = _neo4jConnector.RunReadQueryAsync(checkQuery).GetAwaiter().GetResult();
                long nodeCount = records.FirstOrDefault()?["nodeCount"].As<long>() ?? 0;
                if (nodeCount == 0)
                {
                    // Neo4j-Graph ist leer: initialen Graph aus Revit-Daten erzeugen und pushen
                    Debug.WriteLine("[SpaceTracker] Neuer Graph - initialer Upload der Modelldaten.");
                    _extractor.CreateInitialGraph(doc);  // alle vorhandenen Elemente ins Queue einreihen
                                                         // Änderungen in einem Batch an Neo4j senden (Push)
                    if (!CommandManager.Instance.cypherCommands.IsEmpty)
                    {
                        // Befehle kopieren, damit die Queue sofort wieder benutzt werden kann
                        var cmds = CommandManager.Instance.cypherCommands.ToList();

                        // Asynchron pushen, da die Methode bereits async ist und await verwendet werden kann
                        try
                        {
                            var changes = new List<(string Command, string Path)>();
                            foreach (var c in cmds)
                            {
                                string cache = ChangeCacheHelper.WriteChange(c);
                                changes.Add((c, cache));
                            }
                            _neo4jConnector.PushChangesAsync(
                                changes,

                                CommandManager.Instance.SessionId, doc).GetAwaiter().GetResult();
                            CommandManager.Instance.cypherCommands = new ConcurrentQueue<string>();
                            CommandManager.Instance.PersistSyncTime();
                            _neo4jConnector.CleanupObsoleteChangeLogsAsync().GetAwaiter().GetResult();

                            // Nach initialem Push die Regeln prüfen und Ampel aktualisieren
                            var errs = SolibriRulesetValidator.Validate(doc);
                            var sev = errs.Count == 0 ? Severity.Info : errs.Max(e => e.Severity);
                            SpaceTrackerClass.UpdateConsistencyCheckerButton(sev);

                        }
                        catch (Exception ex)
                        {
                            Logger.LogCrash("DocumentOpened", ex);
                        }
                    }
                    else
                    {
                        // Neo4j-Graph enthält bereits Daten

                    }
                    // After loading the model trigger a pull to ensure latest changes
                    _graphPuller?.PullRemoteChanges(doc, CommandManager.Instance.SessionId).GetAwaiter().GetResult();
                   // Trigger Solibri consistency check after pull
                    var solibriClient = new SolibriApiClient(SpaceTrackerClass.SolibriApiPort);
                    solibriClient.CheckModelAsync(SpaceTrackerClass.SolibriModelUUID, SpaceTrackerClass.SolibriRulesetId)
                                 .GetAwaiter().GetResult();
                    solibriClient.WaitForCheckCompletionAsync(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2))
                                 .GetAwaiter().GetResult();
                    PullChanges();

                    string key = doc.PathName ?? doc.Title;
                    SessionManager.AddSession(key, new Session(doc));

                    ImportInitialSolibriModel(doc);

                }
            }
            catch (Exception ex)
            {
                Logger.LogCrash("DocumentOpened", ex);
                Debug.WriteLine($"[SpaceTracker] Fehler bei documentOpened: {ex.Message}");
            }

        }

        private void documentClosing(object sender, DocumentClosingEventArgs e)
        {
            try
            {
                _databaseUpdateHandler.TriggerPush();
                string key = e.Document.PathName ?? e.Document.Title;
                if (SessionManager.OpenSessions.TryGetValue(key, out var session))
                {
                    SessionManager.RemoveSession(key);
                }
            }
            catch (Exception ex)
            {
                Logger.LogCrash("DocumentClosing", ex);
            }
        }
        #endregion
    }
}