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
        internal static readonly SemaphoreSlim SolibriLock = new SemaphoreSlim(1, 1);


        private Neo4jConnector _neo4jConnector;
        private DatabaseUpdateHandler _databaseUpdateHandler;
        private GraphPuller _graphPuller;
        private GraphPullHandler _graphPullHandler;
        private AutoPullService _autoPullService;
        /// <summary>
        /// Provides access to the singleton <see cref="GraphPuller"/> instance
        /// so that other commands can trigger a pull via the same puller.
        /// </summary>
        internal static GraphPuller GraphPullerInstance { get; private set; }
        
        /// <summary>
        /// Provides access to the singleton <see cref="GraphPullHandler"/> instance
        /// so that other commands can trigger a pull via the same handler.
        /// </summary>
        internal static GraphPullHandler GraphPullHandlerInstance { get; private set; }
        
        // ADDED: Static instance for event-based change notification
        internal static Neo4jChangeNotifier ChangeNotifierInstance { get; private set; }
        
        // ADDED: Static instance for AutoPullService access  
        internal static AutoPullService AutoPullServiceInstance { get; private set; }
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
                try { return v.ToString(); }
                catch { return "ToString() failed"; }
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

            // Logger.LogToFile("STARTUP TRACE 1: OnStartup method called", "sync.log");

            var logDir = Path.Combine(
                           Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                           "SpaceTracker", "log");

            // Logger.LogToFile("STARTUP TRACE 2: Log directory path determined", "sync.log");

            // Stelle sicher, dass der Ordner existiert
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);
            // Nur Inhalte löschen, nicht den Ordner selbst

            // Logger.LogToFile("STARTUP TRACE 3: About to clear log files", "sync.log");

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
            
            //Logger.LogToFile("SpaceTracker startup: Initializing components", "sync.log");
            
            try
            {
                // Logger.LogToFile("STARTUP TRACE 5: Creating Neo4jConnector", "sync.log");
                using var loggerFactory = LoggerFactory.Create(b => b.AddDebug());
                _neo4jConnector = new Neo4jConnector(loggerFactory.CreateLogger<Neo4jConnector>());
                // Logger.LogToFile("STARTUP TRACE 6: Neo4jConnector created successfully", "sync.log");

                // Logger.LogToFile("STARTUP TRACE 7: Initializing CommandManager", "sync.log");
                CommandManager.Initialize(_neo4jConnector);
                // Logger.LogToFile("STARTUP TRACE 8: CommandManager initialized", "sync.log");

                // Logger.LogToFile("STARTUP TRACE 9: Setting up HTTP client services", "sync.log");
                var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
                services.AddHttpClient("solibri", c => c.BaseAddress = new Uri("http://localhost:10876/solibri/v1/"));
                var provider = services.BuildServiceProvider();
                var factory = provider.GetRequiredService<IHttpClientFactory>();
                // Logger.LogToFile("STARTUP TRACE 10: HTTP client factory created", "sync.log");
                
                // Logger.LogToFile("STARTUP TRACE 11: Initializing SolibriChecker", "sync.log");
                SolibriChecker.Initialize(factory, _neo4jConnector);
                // Logger.LogToFile("STARTUP TRACE 12: SolibriChecker initialized", "sync.log");
                
                // Logger.LogToFile("STARTUP TRACE 13: Creating SpaceExtractor", "sync.log");
                _extractor = new SpaceExtractor(CommandManager.Instance);
                // Logger.LogToFile("STARTUP TRACE 14: SpaceExtractor created", "sync.log");
                
                // Logger.LogToFile("STARTUP TRACE 15: Creating DatabaseUpdateHandler", "sync.log");
                _databaseUpdateHandler = new DatabaseUpdateHandler(_extractor);
                // Logger.LogToFile("STARTUP TRACE 16: DatabaseUpdateHandler created", "sync.log");
                
                // Logger.LogToFile("STARTUP TRACE 16.1: Initializing DatabaseUpdateHandler", "sync.log");
                _databaseUpdateHandler.Initialize();
                // Logger.LogToFile("STARTUP TRACE 16.2: DatabaseUpdateHandler initialized", "sync.log");
                
                // Logger.LogToFile("STARTUP TRACE 17: Creating GraphPuller", "sync.log");
                _graphPuller = new GraphPuller(_neo4jConnector);
                GraphPullerInstance = _graphPuller;
                // Logger.LogToFile("STARTUP TRACE 18: GraphPuller created", "sync.log");
                
                // Logger.LogToFile("STARTUP TRACE 19: Creating GraphPullHandler", "sync.log");
                _graphPullHandler = new GraphPullHandler();
                GraphPullHandlerInstance = _graphPullHandler;
                _graphPullEvent = ExternalEvent.Create(_graphPullHandler);
                _graphPullHandler.ExternalEvent = _graphPullEvent;
                // Logger.LogToFile("STARTUP TRACE 20: GraphPullHandler and ExternalEvent created", "sync.log");
                
                // Initialize AutoPullService for automatic pull operations
               // Logger.LogToFile("SpaceTracker startup: AutoPullService initialized", "sync.log");
                // Logger.LogToFile("STARTUP TRACE 21: Creating AutoPullService", "sync.log");
                _autoPullService = new AutoPullService(_neo4jConnector, _graphPuller);
                AutoPullServiceInstance = _autoPullService;
                // Logger.LogToFile("STARTUP TRACE 22: AutoPullService created", "sync.log");
                
                // Initialize event-based change notification system
               // Logger.LogToFile("STARTUP TRACE 23: Creating Neo4jChangeNotifier", "sync.log");
                ChangeNotifierInstance = new Neo4jChangeNotifier(_neo4jConnector.Driver);
               // Logger.LogToFile("SPACETRACKER INIT: Event-based change notification system initialized", "sync.log");
               // Logger.LogToFile("STARTUP TRACE 24: Neo4jChangeNotifier created", "sync.log");
                
               // Logger.LogToFile("SPACETRACKER INIT: AutoPullService initialization completed", "sync.log");
                
              //  Logger.LogToFile("STARTUP TRACE 25: Creating CommandManager instance", "sync.log");
                _cmdManager = CommandManager.Instance;
              //  Logger.LogToFile("STARTUP TRACE 26: CommandManager instance obtained", "sync.log");
                
              //  Logger.LogToFile("STARTUP TRACE 27: Creating IfcExportHandler", "sync.log");
                _exportHandler = new IfcExportHandler();
                _exportEvent = ExternalEvent.Create(_exportHandler);
               // Logger.LogToFile("STARTUP TRACE 28: IfcExportHandler and ExternalEvent created", "sync.log");
                
              //  Logger.LogToFile("STARTUP TRACE 29: Getting UIApplication", "sync.log");
                var uiapp = TryGetUIApplication(application);
             //   Logger.LogToFile("STARTUP TRACE 30: UIApplication obtained", "sync.log");
                
                // Set UIApplication reference for AutoPullService to access current document
                if (uiapp != null)
                {
              //      Logger.LogToFile("STARTUP TRACE 31: Setting UIApplication for AutoPullService", "sync.log");
                    AutoPullService.SetUIApplication(uiapp);
              //      Logger.LogToFile("SPACETRACKER INIT: UIApplication reference set for AutoPullService", "sync.log");
              //      Logger.LogToFile("STARTUP TRACE 32: UIApplication set successfully", "sync.log");
                }
                else
                {
                    Logger.LogToFile("SPACETRACKER INIT WARNING: UIApplication not available for AutoPullService", "sync.log");
                    Logger.LogToFile("STARTUP TRACE 32: UIApplication was null", "sync.log");
                }
            }
            catch (Exception ex)
            {
                Logger.LogToFile($"STARTUP TRACE ERROR: Exception during component initialization: {ex.Message}", "sync.log");
                Logger.LogCrash("OnStartup init", ex);
                return Result.Failed;
            }
            
          //  Logger.LogToFile("STARTUP TRACE 33: Starting background Solibri process task", "sync.log");
            _ = Task.Run(async () =>
            {
                try
                {
              //      Logger.LogToFile("STARTUP TRACE 34: Background Solibri task started", "sync.log");
                    SolibriProcessManager.EnsureStarted();
              //      Logger.LogToFile("STARTUP TRACE 35: SolibriProcessManager.EnsureStarted() completed", "sync.log");
                    var client = new SolibriApiClient(SolibriApiPort);
               //     Logger.LogToFile("STARTUP TRACE 36: SolibriApiClient created", "sync.log");
                    SolibriRulesetId = await client.ImportRulesetAsync(SolibriRulesetPath).ConfigureAwait(false);
               //     Logger.LogToFile("STARTUP TRACE 37: Solibri ruleset imported successfully", "sync.log");
                }
                catch (Exception ex)
                {
                    Logger.LogToFile($"STARTUP TRACE ERROR: Background Solibri task failed: {ex.Message}", "sync.log");
                    Logger.LogCrash("Ruleset-Import", ex);
                }
            });
          //  Logger.LogToFile("STARTUP TRACE 38: Background Solibri task queued", "sync.log");
            
          //  Logger.LogToFile("STARTUP TRACE 39: Setting up thread exception handler", "sync.log");
            System.Windows.Forms.Application.ThreadException += (sender, args) =>
          {
              Logger.LogToFile($"STARTUP TRACE THREAD EXCEPTION: UI Thread Exception occurred: {args.Exception.Message}", "sync.log");
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
          //  Logger.LogToFile("STARTUP TRACE 40: Thread exception handler set", "sync.log");

            // 1. Logging-Pfade in Benutzerverzeichnis verlegen
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string mainLogPath = Path.Combine(logDir, "SpaceTracker.log");
            string crashLogPath = Path.Combine(logDir, "SpaceTracker_crash.log");
            string assemblyCheckPath = Path.Combine(logDir, "SpaceTracker_Assembly_Check.log");

          //  Logger.LogToFile("STARTUP TRACE 41: Log paths determined", "sync.log");

            // 2. Ordnerstruktur sicher erstellen
            Directory.CreateDirectory(logDir);
          //  Logger.LogToFile("STARTUP TRACE 42: Log directory created", "sync.log");

            // 3. Zentralisierte Logging-Methode


            try
            {
             //   Logger.LogToFile("STARTUP TRACE 43: Starting main initialization try block", "sync.log");
                
                // 4. Debugger-Logging initialisieren
              //  Debug.WriteLine("[SpaceTracker] OnStartup initialisiert");
             //   Logger.LogToFile("STARTUP TRACE 44: Debug WriteLine executed", "sync.log");

                // 5. Assembly-Versionen protokollieren
                var revitApiVersion = typeof(Document).Assembly.GetName().Version;
                var revitUIVersion = typeof(UIApplication).Assembly.GetName().Version;
                var addinVersion = Assembly.GetExecutingAssembly().GetName().Version; ;
            //    Logger.LogToFile("STARTUP TRACE 45: Assembly versions checked", "sync.log");

                // 8. Ribbon-UI erstellen
             //   Logger.LogToFile("STARTUP TRACE 46: About to create Ribbon UI", "sync.log");
                CreateRibbonUI(application);
             //   Logger.LogToFile("STARTUP TRACE 47: Ribbon UI created successfully", "sync.log");

                // 9. Events registrieren
             //   Logger.LogToFile("STARTUP TRACE 48: About to register document events", "sync.log");
                RegisterDocumentEvents(application);
             //   Logger.LogToFile("Document-Events registriert");
             //   Logger.LogToFile("STARTUP TRACE 49: Document events registered", "sync.log");

             //   Logger.LogToFile("STARTUP TRACE 50: Setting up session sync file handling", "sync.log");
                string innerappDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string spaceTrackerDir = Path.Combine(innerappDataPath, "SpaceTracker");
                string syncFile = Path.Combine(spaceTrackerDir, $"last_sync_{CommandManager.Instance.SessionId}.txt"); 
                
                if (File.Exists(syncFile))
                {
                    try
                    {
                        string stamp = File.ReadAllText(syncFile);
                        CommandManager.Instance.LastSyncTime = DateTime.Parse(stamp);
                        Logger.LogToFile("STARTUP TRACE 51: Last sync time loaded from file", "sync.log");
                    }
                    catch { 
                        CommandManager.Instance.LastSyncTime = DateTime.MinValue; 
                        Logger.LogToFile("STARTUP TRACE 51: Failed to parse last sync time, using MinValue", "sync.log");
                    }
                }
                else
                {
                    CommandManager.Instance.LastSyncTime = DateTime.MinValue;
                    Logger.LogToFile("STARTUP TRACE 51: No sync file found, using MinValue", "sync.log");
                }

                // 10. Falls bereits ein Dokument geöffnet ist, initiale Treppen und andere Elemente übernehmen
               // Logger.LogToFile("STARTUP TRACE 52: About to get UIApplication again for document check", "sync.log");
                UIApplication uiApp = TryGetUIApplication(application);
              //  Logger.LogToFile("STARTUP TRACE 53: UIApplication obtained for document check", "sync.log");
                
                if (uiApp != null && uiApp.ActiveUIDocument != null)
                {
                //    Logger.LogToFile("STARTUP TRACE 54: Active document found, starting initialization", "sync.log");
                 //   Logger.LogToFile("STARTUP TRACE 55: About to call InitializeExistingElements", "sync.log");
                    InitializeExistingElements(uiApp.ActiveUIDocument.Document);
                 //   Logger.LogToFile("STARTUP TRACE 56: InitializeExistingElements completed", "sync.log");
                    
                //    Logger.LogToFile("STARTUP TRACE 57: About to trigger database push", "sync.log");
                    _databaseUpdateHandler.TriggerPush();
                 //   Logger.LogToFile("STARTUP TRACE 58: Database push triggered", "sync.log");
                    
                //    Logger.LogToFile("STARTUP TRACE 59: About to add session", "sync.log");
                    string key = uiApp.ActiveUIDocument.Document.PathName ?? uiApp.ActiveUIDocument.Document.Title;
                    SessionManager.AddSession(key, new Session(uiApp.ActiveUIDocument.Document));
                 //   Logger.LogToFile("STARTUP TRACE 60: Session added", "sync.log");

                    // CRITICAL FIX: Do NOT perform any Solibri operations in OnStartup
                    // This was causing the hanging - defer all initialization to documentOpened
                //    Logger.LogToFile("SPACETRACKER STARTUP: Initial Solibri import deferred to documentOpened event", "sync.log");
                //    Logger.LogToFile("STARTUP TRACE 61: Solibri initialization deferred", "sync.log");
                }
                else
                {
                    Logger.LogToFile("STARTUP TRACE 54: No active document found during startup", "sync.log");
                }

               // Logger.LogToFile("OnStartup erfolgreich abgeschlossen");
               /// Logger.LogToFile("STARTUP TRACE 62: OnStartup completed successfully", "sync.log");
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
            Logger.LogToFile("TRY GET UI APP TRACE 1: Starting TryGetUIApplication", "sync.log");
            try
            {
                Logger.LogToFile("TRY GET UI APP TRACE 2: Attempting primary constructor approach", "sync.log");
                var result = (UIApplication)Activator.CreateInstance(
                    typeof(UIApplication),
                    app.ControlledApplication);
                Logger.LogToFile("TRY GET UI APP TRACE 3: Primary constructor approach succeeded", "sync.log");
                return result;
            }
            catch (Exception ex)
            {
                Logger.LogToFile($"TRY GET UI APP TRACE ERROR: Primary approach failed: {ex.Message}", "sync.log");
                try
                {
                    Logger.LogToFile("TRY GET UI APP TRACE 4: Attempting reflection approach", "sync.log");
                    var ctrl = app.ControlledApplication;
                    Logger.LogToFile("TRY GET UI APP TRACE 5: Got ControlledApplication", "sync.log");
                    var prop = ctrl.GetType().GetProperty("Application");
                    Logger.LogToFile("TRY GET UI APP TRACE 6: Got Application property", "sync.log");
                    var baseApp = prop?.GetValue(ctrl);
                    Logger.LogToFile("TRY GET UI APP TRACE 7: Got base application value", "sync.log");
                    if (baseApp != null)
                    {
                        Logger.LogToFile("TRY GET UI APP TRACE 8: Creating UIApplication with base app", "sync.log");
                        var result = (UIApplication)Activator.CreateInstance(
                            typeof(UIApplication),
                            baseApp);
                        Logger.LogToFile("TRY GET UI APP TRACE 9: Reflection approach succeeded", "sync.log");
                        return result;
                    }
                    Logger.LogToFile("TRY GET UI APP TRACE ERROR: Base application was null", "sync.log");
                }
                catch (Exception ex2)
                {
                    Logger.LogToFile($"TRY GET UI APP TRACE ERROR: Reflection approach failed: {ex2.Message}", "sync.log");
                }
            }
            Logger.LogToFile("TRY GET UI APP TRACE FINAL: All approaches failed, returning null", "sync.log");
            return null;
        }
        private void CreateRibbonUI(UIControlledApplication application)
        {
            Logger.LogToFile("RIBBON UI TRACE 1: Starting CreateRibbonUI", "sync.log");
            
            // 1. SpaceTracker RibbonPanel sicherstellen (ggf. erstellen)
            Logger.LogToFile("RIBBON UI TRACE 2: Getting ribbon panels", "sync.log");
            IList<RibbonPanel> panels = application.GetRibbonPanels("Add-Ins") ?? new List<RibbonPanel>();
            Logger.LogToFile("RIBBON UI TRACE 3: Got ribbon panels list", "sync.log");
            
            _ribbonPanel = panels.FirstOrDefault(p => p.Name == "SpaceTracker")
                           ?? application.CreateRibbonPanel("SpaceTracker");
            Logger.LogToFile("RIBBON UI TRACE 4: SpaceTracker panel created/found", "sync.log");

            Logger.LogToFile("Erstelle Ribbon-UI");
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            Logger.LogToFile("RIBBON UI TRACE 5: Assembly directory obtained", "sync.log");
            
            // 2. Verhindern, dass Buttons doppelt angelegt werden
            Logger.LogToFile("RIBBON UI TRACE 6: Checking for existing export button", "sync.log");
            bool exportExists = _ribbonPanel.GetItems().OfType<PushButton>().Any(b => b.Name == "ExportButton");
            if (!exportExists)
            {
                Logger.LogToFile("RIBBON UI TRACE 7: Creating export button", "sync.log");
            // REMOVED: Manual Export button - replaced by automatic push system
            // Export functionality is now handled automatically by event-based system
            Logger.LogToFile("RIBBON UI TRACE 8: Manual export button removed (automatic push system active)", "sync.log");
            }

            // 3. Pull-Button (entfernte Änderungen holen)
            Logger.LogToFile("RIBBON UI TRACE 9: Checking for existing pull button", "sync.log");
            if (!_ribbonPanel.GetItems().OfType<PushButton>().Any(b => b.Name == "PullChangesButton"))
            {
                Logger.LogToFile("RIBBON UI TRACE 10: Creating pull button", "sync.log");
                var pullBtnData = new PushButtonData(
                    "PullChangesButton", "Pull Changes",
                    Assembly.GetExecutingAssembly().Location,
                    "SpaceTracker.GraphPullCommand"
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
                Logger.LogToFile("RIBBON UI TRACE 11: Pull button created", "sync.log");
            }
            // 6. Info-Button (zeigt Beschreibung der Funktionen)
            Logger.LogToFile("RIBBON UI TRACE 15: Checking for existing info button", "sync.log");
            if (!_ribbonPanel.GetItems().OfType<PushButton>().Any(b => b.Name == "InfoButton"))
            {
                Logger.LogToFile("RIBBON UI TRACE 16: Creating info button", "sync.log");
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
                Logger.LogToFile("RIBBON UI TRACE 17: Info button created", "sync.log");
            }

            // 7. AcknowledgeAll-Button (setzt alle ChangeLog-Einträge auf acknowledged)
            Logger.LogToFile("RIBBON UI TRACE 18: Checking for existing acknowledge all button", "sync.log");
            if (!_ribbonPanel.GetItems().OfType<PushButton>().Any(b => b.Name == "AcknowledgeAllButton"))
            {
                Logger.LogToFile("RIBBON UI TRACE 19: Creating acknowledge all button", "sync.log");
                var ackAllBtnData = new PushButtonData(
                    "AcknowledgeAllButton", "Acknowledge All",
                    Assembly.GetExecutingAssembly().Location,
                    "SpaceTracker.AcknowledgeAllCommand"
                );
                string ackAllIconPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Green.png");
                if (File.Exists(ackAllIconPath))
                {
                    var ackAllIcon = new BitmapImage();
                    ackAllIcon.BeginInit();
                    ackAllIcon.UriSource = new Uri(ackAllIconPath, UriKind.Absolute);
                    ackAllIcon.EndInit();
                    ackAllBtnData.LargeImage = ackAllIcon;
                }
                var ackAllBtn = _ribbonPanel.AddItem(ackAllBtnData) as PushButton;
                if (ackAllBtn != null)
                    ackAllBtn.ToolTip = "Setzt alle ChangeLog-Einträge in Neo4j auf acknowledged (verhindert Pull-Loops)";
                Logger.LogToFile("RIBBON UI TRACE 20: Acknowledge all button created", "sync.log");
            }
            
            Logger.LogToFile("RIBBON UI TRACE 21: CreateRibbonUI completed successfully", "sync.log");
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
            // Removed CommandManager.Instance.Dispose() to prevent race condition with active Neo4j operations
            application.ControlledApplication.DocumentOpened -= documentOpened;
            application.ControlledApplication.DocumentChanged -= documentChangedHandler;
            application.ControlledApplication.DocumentCreated -= documentCreated;
            application.ControlledApplication.DocumentClosing -= documentClosing;
            
            // Dispose AutoPullService
            Logger.LogToFile("SPACETRACKER SHUTDOWN: Disposing AutoPullService", "sync.log");
            _autoPullService?.Dispose();
            AutoPullServiceInstance = null;
            Logger.LogToFile("SPACETRACKER SHUTDOWN: AutoPullService disposed", "sync.log");
            
            // Dispose ChangeNotifier
            Logger.LogToFile("SPACETRACKER SHUTDOWN: Disposing ChangeNotifier", "sync.log");
            ChangeNotifierInstance?.Dispose();
            ChangeNotifierInstance = null;
            Logger.LogToFile("SPACETRACKER SHUTDOWN: ChangeNotifier disposed", "sync.log");
            
            // Removed _neo4jConnector?.Dispose() to prevent race condition with active transactions
            GraphPullerInstance = null;
            GraphPullHandlerInstance = null;
            return Result.Succeeded;

        }

        /// <summary>
        /// Checks if an element was created by SpaceTracker to prevent feedback loops
        /// </summary>
        private bool IsSpaceTrackerElement(Element element)
        {
            try
            {
                var commentsParam = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                
                if (commentsParam == null)
                {
                    // Manche Element-Typen (z.B. MEPCurve) haben möglicherweise keinen Comments-Parameter
                    Logger.LogToFile($"ELEMENT FILTER: Element {element.Id.Value} ({element.GetType().Name}, category {element.Category?.Name}) has no Comments parameter - treating as non-SpaceTracker element", "sync.log");
                    return false;
                }
                
                var comments = commentsParam.AsString() ?? "";
                
                // Element stammt von SpaceTracker wenn es SpaceTracker-Marker hat
                bool isFromSpaceTracker = comments.Contains("SpaceTracker:ElementId=") || 
                                         comments.Contains("SpaceTracker:PulledFrom=");
                
                if (isFromSpaceTracker)
                {
                    Logger.LogToFile($"ELEMENT FILTER: Skipping SpaceTracker element {element.Id.Value} - already managed by SpaceTracker", "sync.log");
                }
                
                return isFromSpaceTracker;
            }
            catch (Exception ex)
            {
                Logger.LogToFile($"ELEMENT FILTER WARNING: Exception checking SpaceTracker status for element {element.Id.Value}: {ex.Message}", "sync.log");
                return false;
            }
        }

        /// <summary>
        /// Triggers a pull of the latest changes for all open sessions.
        /// </summary>
        private void PullChanges()
        {
            Logger.LogToFile($"PULL CHANGES: Starting PullChanges() for {SessionManager.OpenSessions.Count} open sessions", "sync.log");
            
            int sessionIndex = 0;
            foreach (var openSession in SessionManager.OpenSessions.Values)
            {
                sessionIndex++;
                Logger.LogToFile($"PULL CHANGES {sessionIndex}/{SessionManager.OpenSessions.Count}: Calling PullRemoteChanges for document '{openSession.Document.Title}'", "sync.log");
                _graphPuller.PullRemoteChanges(openSession.Document, CommandManager.Instance.SessionId);
            }
            
            Logger.LogToFile($"PULL CHANGES COMPLETED: Finished pulling changes for all {sessionIndex} sessions", "sync.log");
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
            var startTime = DateTime.Now;
            
            // KRITISCH: Während Pull-Operationen keine automatischen Pushes
            if (CommandManager.Instance.IsPullInProgress)
            {
                Logger.LogToFile("PUSH SUPPRESSED: Document change detected during pull operation - skipping automatic push", "sync.log");
                return;
            }
            
            try
            {
                Document doc = e.GetDocument();
                if (doc == null || doc.IsLinked) 
                {
                    Logger.LogToFile("DOCUMENT CHANGE SKIPPED: Document is null or linked", "sync.log");
                    return;
                }

                Logger.LogToFile($"DOCUMENT CHANGE DETECTED: Document '{doc.Title}' changed at {startTime:yyyy-MM-dd HH:mm:ss.fff}", "sync.log");

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

                Logger.LogToFile($"DOCUMENT CHANGE ANALYSIS: Added={addedIds.Count}, Modified={modifiedIds.Count}, Deleted={deletedIds.Count}", "sync.log");

                // 3. Early Exit bei keinen relevanten Änderungen

                // 4. Elemente aus Dokument holen (mit Null-Check) - Filterung für SpaceTracker-Elemente
                var addedElements = GetAddedElements(e, doc)
                    .Where(el => filter.PassesFilter(el) && !IsSpaceTrackerElement(el))
                    .ToList();
                var modifiedElements = GetModifiedElements(e, doc)
                    .Where(el => filter.PassesFilter(el) && !IsSpaceTrackerElement(el))
                    .ToList();

                // Log element types for debugging
                foreach (var el in addedElements)
                {
                    Logger.LogToFile($"DOCCHANGE ADDED: Element {el.Id} of type {el.GetType().Name} and category {el.Category?.Name} ({el.Category?.Id.Value})", "sync.log");
                    
                    // Special logging for ProvisionalSpaces
                    if (el is FamilyInstance fi && el.Category?.Id.Value == (int)BuiltInCategory.OST_GenericModel)
                    {
                        bool isProvisionalSpace = ParameterUtils.IsProvisionalSpace(fi);
                        Logger.LogToFile($"DOCCHANGE ADDED: GenericModel {el.Id} - IsProvisionalSpace: {isProvisionalSpace}", "sync.log");
                    }
                }
                foreach (var el in modifiedElements) 
                {
                    Logger.LogToFile($"DOCCHANGE MODIFIED: Element {el.Id} of type {el.GetType().Name} and category {el.Category?.Name} ({el.Category?.Id.Value})", "sync.log");
                }
                
                if (addedElements.Count == 0 &&
                 modifiedElements.Count == 0 &&
                 deletedIds.Count == 0 &&
                 addedIds.Count == 0 &&
                 modifiedIds.Count == 0)
                {
                    Logger.LogToFile("DOCUMENT CHANGE SKIPPED: No relevant changes detected", "sync.log");
                    return;
                }

                Logger.LogToFile($"DOCUMENT CHANGE PROCESSING: Processing {addedElements.Count} added, {modifiedElements.Count} modified, {deletedIds.Count} deleted native elements (SpaceTracker elements filtered out)", "sync.log");

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
                Logger.LogToFile("DOCUMENT CHANGE ENQUEUE: Enqueueing changes to DatabaseUpdateHandler", "sync.log");
                _databaseUpdateHandler.EnqueueChange(changeData);
                
                // Direkt nach dem Einreihen einen Push anstoßen, damit die
                // Änderungen ohne manuelle Aktion nach Neo4j gelangen
                Logger.LogToFile("DOCUMENT CHANGE PUSH: Triggering immediate push of changes", "sync.log");
                _databaseUpdateHandler.TriggerPush();
                
                try
                {
                    string sessionId = CommandManager.Instance.SessionId;
                    Logger.LogToFile($"DOCUMENT CHANGE LOGGING: Creating event-based ChangeLog entries for session {sessionId} ({addedElements.Count} added, {modifiedElements.Count} modified, {deletedIds.Count} deleted)", "sync.log");
                    
                    // Get all other active sessions as targets for change notifications
                    var allSessions = SessionManager.OpenSessions.Keys.ToList();
                    var targetSessions = allSessions.Where(s => s != sessionId).ToList();
                    
                    Logger.LogToFile($"DOCUMENT CHANGE TARGETS: Found {targetSessions.Count} target sessions for notifications: [{string.Join(", ", targetSessions)}]", "sync.log");
                    
                    // DISABLED: Event-based ChangeLog creation completely removed to prevent conflicts with UpdateGraph system
                    // All ChangeLog creation is now handled by UpdateGraph -> CreateChangeLogForElement system
                    Logger.LogToFile("DOCUMENT CHANGE LOGGING: Event-based ChangeLog creation disabled - handled by UpdateGraph system", "sync.log");
                    
                    Logger.LogToFile($"DOCUMENT CHANGE LOGGING COMPLETE: All event-based ChangeLog entries created for session {sessionId}", "sync.log");
                    Logger.LogToFile("DOCUMENT CHANGE AUTO-PULL NOTE: Event-based automatic pull triggers are now active", "sync.log");
                    
                    Logger.LogToFile("DOCUMENT CHANGE SOLIBRI: Starting Solibri element checks", "sync.log");
                    var ids = addedElements.Concat(modifiedElements).Select(e => e.Id).Distinct();
                    foreach (var cid in ids)
                        await SolibriChecker.CheckElementAsync(cid, doc);
                }
                catch (Exception ex)
                {
                    Logger.LogToFile($"DOCUMENT CHANGE ERROR: Error during ChangeLog creation or Solibri checks - {ex.Message}", "sync.log");
                    Logger.LogCrash("RealtimeSync", ex);
                }
                
                Logger.LogToFile("DOCUMENT CHANGE FINAL PULL: Triggering pull requests for all open sessions", "sync.log");
                foreach (var openSession in SessionManager.OpenSessions.Values)
                {
                    // Trigger pull asynchronously via external event
                    Logger.LogToFile($"DOCUMENT CHANGE SESSION PULL: Requesting pull for session document '{openSession.Document.Title}'", "sync.log");
                    _graphPullHandler.RequestPull(openSession.Document);
                }
                
                var totalDuration = DateTime.Now - startTime;
                Logger.LogToFile($"DOCUMENT CHANGE COMPLETED: Finished processing document changes in {totalDuration.TotalMilliseconds:F0}ms", "sync.log");
            }
            catch (Exception ex)
            {
                var totalDuration = DateTime.Now - startTime;
                Debug.WriteLine($"[Critical Error] documentChanged: {ex.Message}");
                Logger.LogToFile($"DOCUMENT CHANGE CRITICAL ERROR: Failed after {totalDuration.TotalMilliseconds:F0}ms - {ex.Message}", "sync.log");
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
                Logger.LogToFile($"DOCUMENT CREATED: Document '{e.Document.Title}' created", "sync.log");
                
                InitializeExistingElements(e.Document);
                // Nach dem Initialisieren bereits vorhandener Elemente direkt
                // die aktuellen Befehle an Neo4j senden
                _databaseUpdateHandler.TriggerPush();
                PullChanges();

                string key = e.Document.PathName ?? e.Document.Title;
                SessionManager.AddSession(key, new Session(e.Document));
                
                // CRITICAL FIX: Move Solibri initialization to background to prevent hanging
                Logger.LogToFile("DOCUMENT CREATED: Starting background Solibri initialization", "sync.log");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        Logger.LogToFile("BACKGROUND SOLIBRI CREATE: Starting background Solibri operations", "sync.log");
                        
                        // Give time for document to fully initialize
                        await Task.Delay(2000);
                        
                        ImportInitialSolibriModel(e.Document);
                        
                        Logger.LogToFile("BACKGROUND SOLIBRI CREATE: Background Solibri operations completed", "sync.log");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogToFile($"BACKGROUND SOLIBRI CREATE ERROR: {ex.Message}", "sync.log");
                        Logger.LogCrash("Background Solibri initialization - DocumentCreated", ex);
                    }
                });
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
                // CRITICAL FIX: Check if graph is empty FIRST to prevent hanging
                Logger.LogToFile($"DOCUMENT OPENED: Document '{doc.Title}' opened, checking Neo4j graph state", "sync.log");
                
                // Prüfen, ob der Neo4j-Graph bereits Daten enthält (z.B. Building-Knoten)
                const string checkQuery = "MATCH (n) RETURN count(n) AS nodeCount";
                var records = _neo4jConnector.RunReadQueryAsync(checkQuery).GetAwaiter().GetResult();
                long nodeCount = records.FirstOrDefault()?["nodeCount"].As<long>() ?? 0;
                
                Logger.LogToFile($"DOCUMENT OPENED: Neo4j graph contains {nodeCount} nodes", "sync.log");
                
                if (nodeCount == 0)
                {
                    // Neo4j-Graph ist leer: initialen Graph aus Revit-Daten erzeugen und pushen
                    Logger.LogToFile("DOCUMENT OPENED: Graph is empty - creating initial graph from Revit data", "sync.log");
                    _extractor.CreateInitialGraph(doc);  // alle vorhandenen Elemente ins Queue einreihen
                    
                    // Änderungen in einem Batch an Neo4j senden (Push)
                    if (!CommandManager.Instance.cypherCommands.IsEmpty)
                    {
                        // Befehle kopieren, damit die Queue sofort wieder benutzt werden kann
                        var cmds = CommandManager.Instance.cypherCommands.ToList();
                        
                        Logger.LogToFile($"PUSH TRIGGERED: Document '{doc.Title}' has {cmds.Count} pending commands to push", "sync.log");

                        // Asynchron pushen, da die Methode bereits async ist und await verwendet werden kann
                        try
                        {
                            Logger.LogToFile($"PUSH EXECUTING: Calling PushChangesAsync for session {CommandManager.Instance.SessionId}", "sync.log");
                            _neo4jConnector.PushChangesAsync(
                                cmds,
                                CommandManager.Instance.SessionId, doc).GetAwaiter().GetResult();
                            
                            Logger.LogToFile("PUSH SUCCESS: Commands pushed successfully, clearing command queue", "sync.log");
                            CommandManager.Instance.cypherCommands = new ConcurrentQueue<string>();
                            CommandManager.Instance.PersistSyncTime();
                            
                            Logger.LogToFile("PUSH CLEANUP: Starting background cleanup of obsolete ChangeLogs", "sync.log");
                            // CRITICAL FIX: Run cleanup in background to prevent deadlock
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    Logger.LogToFile("BACKGROUND CLEANUP: Starting obsolete ChangeLogs cleanup", "sync.log");
                                    await _neo4jConnector.CleanupObsoleteChangeLogsAsync().ConfigureAwait(false);
                                    Logger.LogToFile("BACKGROUND CLEANUP: Obsolete ChangeLogs cleanup completed", "sync.log");
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogCrash("Background ChangeLog Cleanup", ex);
                                }
                            });

                            Logger.LogToFile("DOCUMENT OPENED: Push operations completed, continuing with background tasks", "sync.log");

                            // CRITICAL FIX: Move Solibri initialization to background to prevent hanging
                            Logger.LogToFile("DOCUMENT OPENED: Starting background Solibri initialization", "sync.log");
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    Logger.LogToFile("BACKGROUND SOLIBRI: Starting background Solibri operations", "sync.log");
                                    
                                    // Give time for main initialization to complete
                                    await Task.Delay(1000);
                                    
                                    // Import initial Solibri model in background
                                    ImportInitialSolibriModel(doc);
                                    
                                    Logger.LogToFile("BACKGROUND SOLIBRI: Initial model import completed", "sync.log");
                                    
                                    // REMOVED: Solibri validation - handled manually by user
                                    /*
                                    // Nach initialem Push die Regeln prüfen und Ampel aktualisieren
                                    Logger.LogToFile("BACKGROUND SOLIBRI: Running Solibri validation", "sync.log");
                                    // FIXED: Use async/await instead of GetAwaiter().GetResult() to prevent deadlock
                                    var errs = await SolibriRulesetValidator.Validate(doc).ConfigureAwait(false);
                                    var sev = errs.Count == 0 ? Severity.Info : errs.Max(err => err.Severity);
                                    
                                    Logger.LogToFile($"BACKGROUND SOLIBRI: Validation completed with {errs.Count} errors, severity: {sev}", "sync.log");
                                    
                                    // Note: UI updates would need ExternalEvent for thread safety
                                    // For now, just log the result
                                    */
                                    
                                    Logger.LogToFile("BACKGROUND SOLIBRI: Background Solibri operations completed", "sync.log");
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogToFile($"BACKGROUND SOLIBRI ERROR: {ex.Message}", "sync.log");
                                    Logger.LogCrash("Background Solibri initialization", ex);
                                }
                            });

                        }
                        catch (Exception ex)
                        {
                            Logger.LogToFile($"PUSH FAILED: Error during push operation - {ex.Message}", "sync.log");
                            Logger.LogCrash("DocumentOpened - Push Operation", ex);
                        }
                    }
                    else
                    {
                        Logger.LogToFile("PUSH SKIPPED: No pending commands in queue", "sync.log");
                    }
                }
                else
                {
                    // Neo4j-Graph enthält bereits Daten - nur Pull ausführen
                    Logger.LogToFile("DOCUMENT OPENED: Graph contains data - triggering pull operation", "sync.log");
                    
                    // After loading the model trigger a pull to ensure latest changes
                    Logger.LogToFile("AUTO PULL: Triggering automatic pull after document load", "sync.log");
                    _graphPuller?.PullRemoteChanges(doc, CommandManager.Instance.SessionId);
                    
                    // CRITICAL FIX: Move Solibri operations to background for existing graphs too
                    Logger.LogToFile("DOCUMENT OPENED: Starting background Solibri check for existing graph", "sync.log");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            Logger.LogToFile("BACKGROUND SOLIBRI: Starting background Solibri check", "sync.log");
                            
                            // Give time for pull to complete
                            await Task.Delay(2000);
                            
                            // Trigger Solibri consistency check after pull
                            Logger.LogToFile("BACKGROUND SOLIBRI: Starting Solibri consistency check after pull", "sync.log");
                            var solibriClient = new SolibriApiClient(SpaceTrackerClass.SolibriApiPort);
                            await solibriClient.CheckModelAsync(SpaceTrackerClass.SolibriModelUUID, SpaceTrackerClass.SolibriRulesetId);
                            await solibriClient.WaitForCheckCompletionAsync(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2));
                            
                            Logger.LogToFile("BACKGROUND SOLIBRI: Solibri check completed", "sync.log");
                        }
                        catch (Exception ex)
                        {
                            Logger.LogToFile($"BACKGROUND SOLIBRI CHECK ERROR: {ex.Message}", "sync.log");
                            Logger.LogCrash("Background Solibri check", ex);
                        }
                    });
                }
                
                // Session management (always executed)
                string key = doc.PathName ?? doc.Title;
                SessionManager.AddSession(key, new Session(doc));
                Logger.LogToFile($"DOCUMENT OPENED: Session added for '{key}'", "sync.log");
                
                Logger.LogToFile("DOCUMENT OPENED EVENT: *** COMPLETED SUCCESSFULLY *** All operations finished", "sync.log");
            }
            catch (Exception ex)
            {
                Logger.LogCrash("DocumentOpened", ex);
                Debug.WriteLine($"[SpaceTracker] Fehler bei documentOpened: {ex.Message}");
                Logger.LogToFile("DOCUMENT OPENED EVENT: *** FAILED *** Exception occurred", "sync.log");
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