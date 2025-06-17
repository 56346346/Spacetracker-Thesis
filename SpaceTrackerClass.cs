using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.Exceptions;
using Autodesk.Revit.UI;
using System.Reflection;
using Neo4j.Driver;
using System.Windows.Forms;
using System.IO;
using SQLitePCL;
using SpaceTracker;
using System.Windows.Media;           // für ImageSource
using System.Windows.Media.Imaging;
using System.Net.Http;
using System.Collections.Concurrent;
using System.Threading;







namespace SpaceTracker
{
    public class SpaceTrackerClass : IExternalApplication
    {
        private RibbonPanel _ribbonPanel;
        private SQLiteConnector _sqliteConnector;

        private Neo4jConnector _neo4jConnector;
        private DatabaseUpdateHandler _databaseUpdateHandler;
        private ExternalEvent _databaseUpdateEvent;

         private Process _solibriProcess;
        private string _rulesetId;
        public const int SolibriApiPort = 10876;


        private readonly Dictionary<ElementId, ElementMetadata> _elementCache =
       new Dictionary<ElementId, ElementMetadata>();
        private SpaceExtractor _extractor;
        private CommandManager _cmdManager;

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
                    StatusIndicatorButton.ToolTip = "Status: Konsistent (Grün)";
                    break;
                case StatusColor.Yellow:
                    StatusIndicatorButton.LargeImage = YellowIcon;
                    StatusIndicatorButton.ToolTip = "Status: Externe Änderungen vorhanden (Gelb)";
                    break;
                case StatusColor.Red:
                    StatusIndicatorButton.LargeImage = RedIcon;
                    StatusIndicatorButton.ToolTip = "Status: Inkonsistenzen erkannt (Rot)";
                    break;
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
            //  SQLitePCL.Batteries.Init();
            RegisterGlobalExceptionHandlers();

            _sqliteConnector = new SQLiteConnector();
            _neo4jConnector = new Neo4jConnector();

            CommandManager.Initialize(_neo4jConnector, _sqliteConnector);
            StartSolibriRestApi();

            // Instanz abrufen

            _extractor = new SpaceExtractor(CommandManager.Instance);
            _databaseUpdateHandler = new DatabaseUpdateHandler(_sqliteConnector, _extractor);
              _databaseUpdateHandler.Initialize();
              _databaseUpdateEvent = ExternalEvent.Create(_databaseUpdateHandler);
            _cmdManager = CommandManager.Instance;

          




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
                var addinVersion = Assembly.GetExecutingAssembly().GetName().Version;

                Logger.LogToFile($"RevitAPI Version: {revitApiVersion}", "assembly.log");
                Logger.LogToFile($"RevitAPIUI Version: {revitUIVersion}", "assembly.log");
                Logger.LogToFile($"Add-In Version: {addinVersion}", "assembly.log");
                Logger.LogToFile("OnStartup initialisiert", "assembly.log");

                // 6. Datenbankverbindungen initialisieren
                Logger.LogToFile("Initialisiere Datenbankverbindungen");


                Logger.LogToFile("SQLite-Verbindung erfolgreich");

                // 7. Event-Handler erstellen


                Logger.LogToFile("Handler erfolgreich initialisiert");

                // 8. Ribbon-UI erstellen
                CreateRibbonUI(application);



                // 9. Events registrieren
                application.ControlledApplication.DocumentCreated += documentCreated;
                application.ControlledApplication.DocumentOpened += documentOpened;
                application.ControlledApplication.DocumentChanged += documentChanged;
                Logger.LogToFile("Document-Events registriert");

                Logger.LogToFile("OnStartup erfolgreich abgeschlossen");
                string innerappDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string spaceTrackerDir = Path.Combine(innerappDataPath, "SpaceTracker");
                string syncFile = Path.Combine(spaceTrackerDir, "last_sync.txt");
                if (File.Exists(syncFile))
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

        private void RegisterGlobalExceptionHandlers()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                Logger.LogCrash("Unhandled", ex);
            };
            System.Windows.Forms.Application.ThreadException += (s, e) =>
            {
                Logger.LogCrash("UI Thread", e.Exception);
            };
        }

        private void StartSolibriRestApi()
        {
            var solibriExe = @"C:\Program Files\Solibri\SOLIBRI\Solibri.exe"; // ggf. anpassen
            var startInfo = new ProcessStartInfo(solibriExe, $"--rest-api-server-port={SolibriApiPort}")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _solibriProcess = Process.Start(startInfo);
            // Optional: Warten bis API erreichbar ist
            WaitForSolibriApiAvailable();
        }

        private void WaitForSolibriApiAvailable()
        {
            var http = new HttpClient();
            for (int i = 0; i < 30; i++) // max 30 Sekunden warten
            {
                try
                {
                    var resp = http.GetAsync($"http://localhost:{SolibriApiPort}/status").Result;
                    if (resp.IsSuccessStatusCode) return;
                }
                catch { }
                Thread.Sleep(1000);
            }
            throw new Exception("Solibri REST API nicht erreichbar!");
        }


        private void CreateRibbonUI(UIControlledApplication application)
        {
            // 1. SpaceTracker RibbonPanel sicherstellen (ggf. erstellen)
            IList<RibbonPanel> panels = application.GetRibbonPanels("Add-Ins") ?? new List<RibbonPanel>();
            _ribbonPanel = panels.FirstOrDefault(p => p.Name == "SpaceTracker")
                           ?? application.CreateRibbonPanel("SpaceTracker");

            Logger.LogToFile("Erstelle Ribbon-UI");

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
                string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
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
            if (!_ribbonPanel.GetItems().OfType<PushButton>().Any(b => b.Name == "ConsistencyCheckButton"))
            {
                var checkBtnData = new PushButtonData(
                    "ConsistencyCheckButton", "Consistency Check",
                    Assembly.GetExecutingAssembly().Location,
                    "SpaceTracker.ConsistencyCheckCommand"
                );
                // Ampel-Icons laden (Grün, Gelb, Rot)
                string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string greenIconPath = Path.Combine(assemblyDir, "Green.png");
                string yellowIconPath = Path.Combine(assemblyDir, "Yellow.png");
                string redIconPath = Path.Combine(assemblyDir, "Red.png");
                // BitmapImages erstellen
                var greenIcon = new BitmapImage(); greenIcon.BeginInit(); greenIcon.UriSource = new Uri(greenIconPath, UriKind.Absolute); greenIcon.EndInit();
                var yellowIcon = new BitmapImage(); yellowIcon.BeginInit(); yellowIcon.UriSource = new Uri(yellowIconPath, UriKind.Absolute); yellowIcon.EndInit();
                var redIcon = new BitmapImage(); redIcon.BeginInit(); redIcon.UriSource = new Uri(redIconPath, UriKind.Absolute); redIcon.EndInit();
                checkBtnData.LargeImage = greenIcon; // initial auf Grün (angenommen konsistent)
                var checkBtn = _ribbonPanel.AddItem(checkBtnData) as PushButton;
                checkBtn.ToolTip = "Prüft die Konsistenz zwischen lokalem Modell und Neo4j-Graph (Ampelanzeige)";
                // Referenzen für dynamische Ampelanzeige speichern
                SpaceTrackerClass.StatusIndicatorButton = checkBtn;
                SpaceTrackerClass.GreenIcon = greenIcon;
                SpaceTrackerClass.YellowIcon = yellowIcon;
                SpaceTrackerClass.RedIcon = redIcon;
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
     new ElementCategoryFilter(BuiltInCategory.OST_Stairs)

});

                using (Transaction tx = new Transaction(doc, "Initialize Existing Elements"))
                {
                    if (tx.Start() == TransactionStatus.Started)
                    {
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


                        tx.Commit();
                    }
                }
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
                if (_elementCache.ContainsKey(delId))
                    _elementCache.Remove(delId);
            }
        }
        private List<Element> GetAddedElements(DocumentChangedEventArgs e, Document doc)
        {
            return e.GetAddedElementIds()
                   .Select(id => doc.GetElement(id))
                   .Where(el => el != null)
                   .ToList();
        }

        private List<Element> GetModifiedElements(DocumentChangedEventArgs e, Document doc)
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
            _sqliteConnector?.Dispose();
            if (_solibriProcess != null && !_solibriProcess.HasExited)
             _solibriProcess.Kill();
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
    new ElementCategoryFilter(BuiltInCategory.OST_Levels)
                });

                // 2. Änderungen identifizieren
                var addedIds = e.GetAddedElementIds(filter);
                var deletedIds = e.GetDeletedElementIds();
                var modifiedIds = e.GetModifiedElementIds(filter);

                // 3. Early Exit bei keinen relevanten Änderungen


                // 4. Elemente aus Dokument holen (mit Null-Check)
                var addedElements = GetAddedElements(e, doc).Where(el => filter.PassesFilter(el)).ToList();

                var modifiedElements = GetModifiedElements(e, doc).Where(el => filter.PassesFilter(el)).ToList();
                if (!addedElements.Any() && !deletedIds.Any() && !modifiedElements.Any()) return;

                if (!addedIds.Any() && !deletedIds.Any() && !modifiedIds.Any()) return;


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
                var records = Task.Run(async () =>
                    await _neo4jConnector.RunReadQueryAsync(checkQuery)).GetAwaiter().GetResult();
                long nodeCount = records.FirstOrDefault()?["nodeCount"].As<long>() ?? 0;
                if (nodeCount == 0)
                {
                    // Neo4j-Graph ist leer: initialen Graph aus Revit-Daten erzeugen und pushen
                    Debug.WriteLine("[SpaceTracker] Neuer Graph - initialer Upload der Modelldaten.");
                    _extractor.CreateInitialGraph(doc);  // alle vorhandenen Elemente ins Queue einreihen
                                                         // Änderungen in einem Batch an Neo4j senden (Push)
                    if (CommandManager.Instance.cypherCommands.Count > 0)
                    {
                        // Befehle kopieren, damit die Queue sofort wieder benutzt werden kann
                        var cmds = CommandManager.Instance.cypherCommands.ToList();

                        // Asynchron pushen, um die Revit-Oberfläche nicht zu blockieren
                        Task.Run(async () =>
                        {

                            try
                            {
                                await _neo4jConnector.PushChangesAsync(
                                    cmds,
                                    CommandManager.Instance.SessionId,
                                    Environment.UserName).ConfigureAwait(false);

                                CommandManager.Instance.cypherCommands = new ConcurrentQueue<string>();
                                CommandManager.Instance.PersistSyncTime();
                                await _neo4jConnector.CleanupObsoleteChangeLogsAsync().ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                Logger.LogCrash("DocumentOpened", ex);
                            }
                        });
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