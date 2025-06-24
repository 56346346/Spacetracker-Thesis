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
using SpaceTracker.Utilities;
using Autodesk.Revit.ApplicationServices;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;
using System.Runtime.Versioning;




[assembly: SupportedOSPlatform("windows")]



namespace SpaceTracker
{
    public class SpaceTrackerClass : IExternalApplication
    {
        private RibbonPanel _ribbonPanel;


        private Neo4jConnector _neo4jConnector;
        private DatabaseUpdateHandler _databaseUpdateHandler;
                private GraphPuller _graphPuller;


        public const int SolibriApiPort = 10876;


        private readonly Dictionary<ElementId, ElementMetadata> _elementCache =
       new Dictionary<ElementId, ElementMetadata>();
        private SpaceExtractor _extractor;
        private CommandManager _cmdManager;


        public const string SolibriModelUUID = "441081f9-7562-4a10-8d2e-7dd3add07eee";
        public static string SolibriRulesetId;

        public static PushButton StatusIndicatorButton;
        internal static ImageSource GreenIcon, YellowIcon, RedIcon;

        // Ampel-Status Enum
        public enum StatusColor { Green, Yellow, Red }

        // Methode zum Aktualisieren des Ampel-Icons
        public static void SetStatusIndicator(StatusColor status)
        {
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
                    StatusIndicatorButton.ToolTip = "Status: Externe Änderungen vorhanden (Gelb)";
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
        public static void PerformConsistencyCheck(bool showDialogs)
        {
            var cmdMgr = CommandManager.Instance;
            var connector = cmdMgr.Neo4jConnector;
            var parameters = new { session = cmdMgr.SessionId };
            string query = "MATCH (c:ChangeLog) " +
                           "WHERE c.sessionId <> $session AND c.acknowledged = false " +
                           "RETURN c.elementId AS id, c.type AS type, c.sessionId AS session";

            List<IRecord> records;
            try
            {
                records = Task.Run(() => connector.RunReadQueryAsync(query, parameters)).Result;
            }
            catch (Exception ex)
            {
                if (showDialogs)
                    Autodesk.Revit.UI.TaskDialog.Show("Consistency Check", $"Fehler bei der Abfrage: {ex.Message}");
                return;
            }

            var localPendingIds = new HashSet<long>();
            var localChangeType = new Dictionary<long, string>();
            foreach (var cmd in cmdMgr.cypherCommands.ToArray())
            {
                long id = -1;
                int idx = cmd.IndexOf("ElementId");
                if (idx >= 0)
                {
                    string sub = cmd.Substring(idx);
                    sub = new string(sub.SkipWhile(ch => !char.IsDigit(ch) && ch != '-').ToArray());
                    string num = new string(sub.TakeWhile(ch => char.IsDigit(ch) || ch == '-').ToArray());
                    if (long.TryParse(num, out var parsedId))
                        id = parsedId;
                }
                if (id < 0) continue;
                localPendingIds.Add(id);
                string lType;
                if (cmd.Contains("DELETE", StringComparison.OrdinalIgnoreCase))
                    lType = "Delete";
                else if (cmd.Contains("MERGE", StringComparison.OrdinalIgnoreCase) && !cmd.Contains("MATCH", StringComparison.OrdinalIgnoreCase)) lType = "Insert";
                else
                    lType = "Modify";
                localChangeType[id] = lType;
            }

            bool conflict = false;
            bool remoteChanges = records.Count > 0;
            var conflictDetails = new List<string>();

            foreach (var rec in records)
            {
                long id = rec["id"].As<long>();
                string rType = rec["type"].As<string>();
                if (localPendingIds.Contains(id))
                {
                    string lType = localChangeType.TryGetValue(id, out var tmp) ? tmp : "Modify";
                    if (!(rType == "Delete" && lType == "Delete"))
                    {
                        conflict = true;
                        string rDesc = (rType == "Delete") ? "gelöscht" : (rType == "Insert" ? "eingefügt" : "geändert");
                        string lDesc = (lType == "Delete") ? "gelöscht" : (lType == "Insert" ? "eingefügt" : "geändert");
                        conflictDetails.Add($"Element {id}: extern {rDesc}, lokal {lDesc}");
                    }
                }
            }

            if (conflict)
            {
                SetStatusIndicator(StatusColor.Red);
                if (showDialogs)
                {
                    string detailText = conflictDetails.Count > 0
                        ? string.Join("\n", conflictDetails)
                        : "Siehe Änderungsprotokoll für Details.";
                    Autodesk.Revit.UI.TaskDialog.Show("Consistency Check",
                        $"*** Konflikt erkannt! ***\n" +
                        $"Einige Elemente wurden sowohl lokal als auch von einem anderen Nutzer geändert.\n" +
                        $"{detailText}\n\nBitte Konflikte manuell lösen.");
                }
            }
            else if (remoteChanges)
            {
                SetStatusIndicator(StatusColor.Yellow);
                if (showDialogs)
                {
                    int count = records.Count;
                    Autodesk.Revit.UI.TaskDialog.Show("Consistency Check",
                        $"Es liegen {count} neue Änderungen von anderen Nutzern vor.\n" +
                        $"Keine direkten Konflikte mit lokalen Änderungen erkannt.\n" +
                        $"Sie können einen Pull durchführen, um diese zu übernehmen.");
                }
            }
            else
            {
                SetStatusIndicator(StatusColor.Green);
                if (showDialogs)
                {
                    string note = localPendingIds.Count > 0
                        ? "\n(Hinweis: Es gibt ungesicherte lokale Änderungen, bitte Push ausführen.)"
                        : string.Empty;
                    Autodesk.Revit.UI.TaskDialog.Show("Consistency Check", "Das lokale Modell ist konsistent mit dem Neo4j-Graph." + note);
                }

                if (localPendingIds.Count == 0)
                {
                    cmdMgr.LastSyncTime = DateTime.Now;
                    cmdMgr.PersistSyncTime();
                    try
                    {
                        Task.Run(() => connector.UpdateSessionLastSyncAsync(cmdMgr.SessionId, cmdMgr.LastSyncTime)).Wait();
                        Task.Run(() => connector.CleanupObsoleteChangeLogsAsync()).Wait();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ConsistencyCheck] Cleanup failed: {ex.Message}");
                    }
                }
            }
        }





        #region register events

        /// <summary>
        /// Catch startup and mount event handlers
        /// </summary>
        /// <param name="application"></param>
        /// <returns></returns>
        public Result OnStartup(UIControlledApplication application)
        {
            Logger.LogToFile("OnStartup begin", "assembly.log");

            try
            {

                RegisterGlobalExceptionHandlers();


                using var loggerFactory = LoggerFactory.Create(b => b.AddDebug());
                _neo4jConnector = new Neo4jConnector(loggerFactory.CreateLogger<Neo4jConnector>());

                CommandManager.Initialize(_neo4jConnector);
                _extractor = new SpaceExtractor(CommandManager.Instance);
                _databaseUpdateHandler = new DatabaseUpdateHandler(_extractor);
                                _graphPuller = new GraphPuller(_neo4jConnector);
                _cmdManager = CommandManager.Instance;
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
                    string rulesetPath = "C:/Users/Public/Solibri/SOLIBRI/Regelsaetze/RegelnThesis/DeltaRuleset.cset";
                    SolibriRulesetId = await client.ImportRulesetAsync(rulesetPath).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("Ruleset-Import", ex);
                }
            });
            System.Windows.Forms.Application.ThreadException += (sender, args) =>
           {
               File.AppendAllText(
                   Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                       "SpaceTracker",
                       "crash.log"),
                   $"{DateTime.Now:O} UI Thread Exception: {args.Exception}\n"
               );
           };

            // 1. Logging-Pfade in Benutzerverzeichnis verlegen
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string logDir = Path.Combine(appDataPath, "SpaceTracker");
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
                }

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
                Logger.LogToFile(errorDetails, "assembly.log");

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

        private static void RegisterGlobalExceptionHandlers()
        {
            SolibriProcessManager.Port = SolibriApiPort;
            _ = Task.Run(() =>
            {
                try
                {
                    SolibriProcessManager.EnsureStarted();
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("Solibri Start", ex);
                }
            });
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

            // 3. Push-Button (lokale Änderungen übertragen)
            if (!_ribbonPanel.GetItems().OfType<PushButton>().Any(b => b.Name == "PushChangesButton"))
            {
                var pushBtnData = new PushButtonData(
                    "PushChangesButton", "Push Changes",
                    Assembly.GetExecutingAssembly().Location,
                    "SpaceTracker.PushCommand"
                );
                // (Optional: Icon für Push laden, z.B. Push.png im Verzeichnis)
                string pushIconPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Push.png");
                if (File.Exists(pushIconPath))
                {
                    var pushIcon = new BitmapImage();
                    pushIcon.BeginInit();
                    pushIcon.UriSource = new Uri(pushIconPath, UriKind.Absolute);
                    pushIcon.EndInit();
                    pushBtnData.LargeImage = pushIcon;
                }
                var pushBtn = _ribbonPanel.AddItem(pushBtnData) as PushButton;
                pushBtn.ToolTip = "Überträgt lokale Änderungen zum Neo4j-Graph (Push)";
            }

            // 4. Pull-Button (entfernte Änderungen holen)
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

            // 5. Consistency-Check-Button (Ampelanzeige für Status)
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
            ctrl.DocumentChanged += documentChanged;
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
                    DeletedElementIds = new List<ElementId>()
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
                if (!_elementCache.ContainsKey(el.Id))
                {
                    _elementCache[el.Id] = new ElementMetadata
                    {
                        Id = el.Id,
                        Type = el.GetType().Name,
                        Name = el.Name ?? "Unnamed"
                    };
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


        /// <summary>
        /// remove mounted events
        /// </summary>
        /// <param name="application"></param>
        /// <returns></returns>
        public Result OnShutdown(UIControlledApplication application)
        {
            CommandManager.Instance.Dispose();
            application.ControlledApplication.DocumentOpened -= documentOpened;
            application.ControlledApplication.DocumentChanged -= documentChanged;
            application.ControlledApplication.DocumentCreated -= documentCreated;
            _neo4jConnector?.Dispose();
            SolibriProcessManager.Stop();
            return Result.Succeeded;

        }

        #endregion

        #region Event handler

        private void documentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                Document doc = e.GetDocument();
                if (doc == null || doc.IsLinked) return;

                var filter = new LogicalOrFilter(new List<ElementFilter>{
    new ElementCategoryFilter(BuiltInCategory.OST_Walls),
    new ElementCategoryFilter(BuiltInCategory.OST_Rooms),
    new ElementCategoryFilter(BuiltInCategory.OST_Doors),
    new ElementCategoryFilter(BuiltInCategory.OST_Levels),
    new ElementCategoryFilter(BuiltInCategory.OST_Stairs),
    new ElementCategoryFilter(BuiltInCategory.OST_GenericModel)
                });

                // 2. Änderungen identifizieren
                var addedIds = e.GetAddedElementIds(filter);
                var deletedIds = e.GetDeletedElementIds();
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
                    ModifiedElements = modifiedElements
                };

                // 7. Änderungen zur Verarbeitung einreihen
                _databaseUpdateHandler.EnqueueChange(changeData);
                // Direkt nach dem Einreihen einen Push anstoßen, damit die
                // Änderungen ohne manuelle Aktion nach Neo4j gelangen
                _databaseUpdateHandler.TriggerPush();
                _graphPuller?.RequestPull(doc, Environment.UserName);


            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Critical Error] documentChanged: {ex.Message}");
                Logger.LogToFile($"DocumentChanged Exception: {ex}\n", "crash");
            }
        }



        private void documentCreated(object sender, DocumentCreatedEventArgs e)
        {
            try
            {
                InitializeExistingElements(e.Document);
                // Nach dem Initialisieren bereits vorhandener Elemente direkt
                // die aktuellen Befehle an Neo4j senden
                _databaseUpdateHandler.TriggerPush();

            }
            catch (Exception ex)
            {
                Logger.LogCrash("DocumentCreated", ex);
            }
        }

        private async void documentOpened(object sender, DocumentOpenedEventArgs e)
        {
            Document doc = e.Document;
            try
            {
                // Prüfen, ob der Neo4j-Graph bereits Daten enthält (z.B. Building-Knoten)
                const string checkQuery = "MATCH (n) RETURN count(n) AS nodeCount";
                var records = await _neo4jConnector.RunReadQueryAsync(checkQuery);
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
                            await _neo4jConnector.PushChangesAsync(
                                changes,
                                CommandManager.Instance.SessionId,
  Environment.UserName, doc).ConfigureAwait(false);
                            CommandManager.Instance.cypherCommands = new ConcurrentQueue<string>();
                            CommandManager.Instance.PersistSyncTime();
                            await _neo4jConnector.CleanupObsoleteChangeLogsAsync().ConfigureAwait(false);

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
                        // Neo4j-Graph enthält bereits Daten -> keine Überschreibung, Status-Ampel auf Gelb setzen
                        Debug.WriteLine("[SpaceTracker] Vorhandene Graph-Daten erkannt - bitte Pull/Check durchführen.");
                        SpaceTrackerClass.SetStatusIndicator(SpaceTrackerClass.StatusColor.Yellow);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogCrash("DocumentOpened", ex);
                Debug.WriteLine($"[SpaceTracker] Fehler bei documentOpened: {ex.Message}");
            }
        }

        #endregion
    }




}