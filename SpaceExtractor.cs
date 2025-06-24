using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Events;
using System.Linq;
using System.Globalization;


namespace SpaceTracker
{
    public class SpaceExtractor
    {


        private readonly CommandManager _cmdManager;

        private static int _wallCounter = 0;

        private readonly Dictionary<(string baseLevel, string topLevel), int> _stairCounters
          = new Dictionary<(string baseLevel, string topLevel), int>();

        private string GenerateStairName(string baseLevelName, string topLevelName)
        {
            var key = (baseLevelName, topLevelName);
            if (!_stairCounters.TryGetValue(key, out var count))
            {
                count = 0;
            }
            count++;
            _stairCounters[key] = count;
            return $"Treppe {baseLevelName} {topLevelName} {count}";
        }



        /// <summary>
        /// Dflt constructor
        /// </summary>
        public SpaceExtractor(CommandManager cmdManager)
        {

            _cmdManager = cmdManager;
        }

        private void ProcessWalls(Document doc, Level level)
        {
            var wallFilter = new ElementLevelFilter(level.Id);
            var collector = new FilteredElementCollector(doc).OfClass(typeof(Wall)).WherePasses(wallFilter);

            foreach (Wall wall in collector)
            {
                ProcessWall(wall, doc);
            }
        }



        private void ProcessWall(Element wall, Document doc)
        {
            if (wall.LevelId == ElementId.InvalidElementId) return;
            try
            {
                // 1. Neo4j Cypher-Query
                WallType wallType = doc.GetElement(wall.GetTypeId()) as WallType;
                string wallName = wallType?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_NAME)?.AsString() ?? wallType?.Name ?? "Unbekannter Typ";
                if (string.IsNullOrWhiteSpace(wallName)) wallName = wallType?.Name;
                if (string.IsNullOrWhiteSpace(wallName)) wallName = wall.Name;
                if (string.IsNullOrWhiteSpace(wallName)) wallName = "Unbenannt";

                Element levelElement = doc.GetElement(wall.LevelId);
                string levelName = doc.GetElement(wall.LevelId)?.Name ?? "Unbekanntes Level";
                int currentCount = Interlocked.Increment(ref _wallCounter);
                string neo4jId = $"WALL-{currentCount:D4}";
                string cy =
   $"MERGE (w:Wall {{ElementId: {wall.Id.Value}}}) " +
   $"SET w.Neo4jId = '{neo4jId}', w.Level = '{EscapeString(levelName)}', w.Type = '{EscapeString(wallName)}' " +
   $"WITH w " +
   $"MATCH (l:Level {{ElementId: {wall.LevelId.Value}}}) " +
   $"MERGE (l)-[:CONTAINS]->(w)";


                _cmdManager.cypherCommands.Enqueue(cy);
                Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);


            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Wall Processing Error] {ex.Message}");
            }
        }

        private void ProcessDoor(Element door, Document doc)
        {
            try
            {
                // 1. Neo4j Cypher-Query
                string doorName = door.get_Parameter(BuiltInParameter.DOOR_NUMBER)?.AsString() ?? "Unbenannt";
                FamilyInstance doorInstance = door as FamilyInstance;
                Element hostWall = doorInstance?.Host;
                var sym = doc.GetElement(door.GetTypeId()) as FamilySymbol;
                if(doorInstance!=null)
                    _ = DoorSerializer.ToNode(doorInstance);


                string doorType = sym?.Name ?? (door as FamilyInstance)?.Symbol?.Name ?? "Unbekannter Typ";


                if (hostWall != null)
                {
                    // 1) Tür mit Wand und Level verknüpfen
                    string cy =
                        $"MATCH (l:Level {{ElementId: {door.LevelId.Value}}}), " +
                        $"      (w:Wall  {{ElementId: {hostWall.Id.Value}}}) " +
                        $"MERGE (d:Door {{ElementId: {door.Id.Value}}}) " +
                        $"SET d.Name = '{EscapeString(doorName)}', d.Type = '{EscapeString(doorType)}' " +
                        $"MERGE (l)-[:CONTAINS]->(d)-[:CONTAINED_IN]->(w)";
                    _cmdManager.cypherCommands.Enqueue(cy);
                    Debug.WriteLine("[Neo4j] Cypher erzeugt (Door mit Level+Wall): " + cy);
                }
                else
                {
                    // 2) Kein Host-Wall → nur Door und Level
                    string cy =
                        $"MATCH (l:Level {{ElementId: {door.LevelId.Value}}}) " +
                        $"MERGE (d:Door {{ElementId: {door.Id.Value}}}) " +
                        $"SET d.Name = '{EscapeString(doorName)}', d.Type = '{EscapeString(doorType)}' " +
                        $"MERGE (l)-[:CONTAINS]->(d)";
                    _cmdManager.cypherCommands.Enqueue(cy);
                    Debug.WriteLine("[Neo4j] Cypher erzeugt (Door nur mit Level): " + cy);
                }


            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Door Processing Error] {ex.Message}");
            }
        }

        private void ProcessProvisionalSpace(FamilyInstance inst, Document doc)
        {
            try
            {
                var ifc = GetIfcExportClass(inst);
            if (!(ifc.Equals("IfcBuildingElementProxy", StringComparison.OrdinalIgnoreCase)
                  && inst.Name.Contains("ProvSpaceVoid", StringComparison.OrdinalIgnoreCase)))
                return;
                var host = inst.Host as Wall;
                var data = ProvisionalSpaceSerializer.ToNode(inst);
                var inv = CultureInfo.InvariantCulture;
                string cyNode =
                    $"MERGE (p:ProvisionalSpace {{guid:'{data["guid"]}'}}) " +
                    $"SET p.name = '{EscapeString(data["name"].ToString())}', " +
                       $"p.width = {((double)data["width"]).ToString(inv)}, p.height = {((double)data["height"]).ToString(inv)}, " +
                    $"p.thickness = {((double)data["thickness"]).ToString(inv)}, " +
                    $"p.level = '{EscapeString(data["level"].ToString())}', " +
                      $"p.revitId = {data["revitId"]}, p.ifcType = 'IfcOpeningElement', " +
                    $"p.category = '{EscapeString(data.GetValueOrDefault("category", "").ToString())}', " +
                    $"p.familyName = '{EscapeString(data.GetValueOrDefault("familyName", "").ToString())}', " +
                    $"p.phaseCreated = {data.GetValueOrDefault("phaseCreated", -1)}, " +
                    $"p.phaseDemolished = {data.GetValueOrDefault("phaseDemolished", -1)}, " +
                    $"p.bbMinX = {((double)data.GetValueOrDefault("bbMinX", 0.0)).ToString(inv)}, " +
                    $"p.bbMinY = {((double)data.GetValueOrDefault("bbMinY", 0.0)).ToString(inv)}, " +
                    $"p.bbMinZ = {((double)data.GetValueOrDefault("bbMinZ", 0.0)).ToString(inv)}, " +
                    $"p.bbMaxX = {((double)data.GetValueOrDefault("bbMaxX", 0.0)).ToString(inv)}, " +
                    $"p.bbMaxY = {((double)data.GetValueOrDefault("bbMaxY", 0.0)).ToString(inv)}, " +
                    $"p.bbMaxZ = {((double)data.GetValueOrDefault("bbMaxZ", 0.0)).ToString(inv)}";
                _cmdManager.cypherCommands.Enqueue(cyNode);
                if (host != null)
                {
                    string cyRel =
                        $"MATCH (w:Wall {{ElementId:{host.Id.Value}}}), (p:ProvisionalSpace {{guid:'{data["guid"]}'}}) " +
                        "MERGE (w)-[:HAS_PROV_SPACE]->(p)";
                    _cmdManager.cypherCommands.Enqueue(cyRel);
                    Debug.WriteLine("[Neo4j] Cypher erzeugt (ProvSpace Rel): " + cyRel);
                }

                Debug.WriteLine("[Neo4j] Cypher erzeugt (ProvSpace Node): " + cyNode);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProvisionalSpace Error] {ex.Message}");
            }
        }

        private void ProcessPipe(MEPCurve pipe, Document doc)
        {
            try
            {
                var data = PipeSerializer.ToNode(pipe);
                                var inv = CultureInfo.InvariantCulture;

                string cyNode =
                    $"MERGE (p:Pipe {{uid:'{data["uid"]}'}}) " +
                    $"SET p.elementId = {data["elementId"]}, p.levelId = {data["levelId"]}, " +
                     $"p.x1 = {((double)data["x1"]).ToString(inv)}, p.y1 = {((double)data["y1"]).ToString(inv)}, p.z1 = {((double)data["z1"]).ToString(inv)}, " +
                    $"p.x2 = {((double)data["x2"]).ToString(inv)}, p.y2 = {((double)data["y2"]).ToString(inv)}, p.z2 = {((double)data["z2"]).ToString(inv)}, " +
                    $"p.diameter_mm = {((double)data["diameter"]).ToString(inv)}";
                _cmdManager.cypherCommands.Enqueue(cyNode);
                                Debug.WriteLine("[Neo4j] Cypher erzeugt (Pipe Node): " + cyNode);


                BoundingBoxXYZ bbPipe = pipe.get_BoundingBox(null);
                if (bbPipe == null) return;

                double tol = UnitConversion.ToFt(100); // 100 mm tolerance
                var psCollector = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_GenericModel)
                    .OfClass(typeof(FamilyInstance));

                foreach (FamilyInstance ps in psCollector.Cast<FamilyInstance>())
                {
                    var ifcPs = GetIfcExportClass(ps);
   if (!(ifcPs.Equals("IfcBuildingElementProxy", StringComparison.OrdinalIgnoreCase)
                          && ps.Name.Contains("ProvSpaceVoid", StringComparison.OrdinalIgnoreCase)))                        continue;
                    var psLevel = doc.GetElement(ps.LevelId) as Level;
                    if (psLevel != null && Math.Abs(psLevel.Elevation - bbPipe.Min.Z) > tol)
                        continue;
                    BoundingBoxXYZ bbPs = ps.get_BoundingBox(null);
                    if (bbPs == null) continue;
                    bool contained =
                        bbPipe.Min.X >= bbPs.Min.X && bbPipe.Max.X <= bbPs.Max.X &&
                        bbPipe.Min.Y >= bbPs.Min.Y && bbPipe.Max.Y <= bbPs.Max.Y;
                    if (contained)
                    {
                        string cyRel =
                            $"MATCH (pi:Pipe {{uid:'{data["uid"]}'}}), (ps:ProvisionalSpace {{guid:'{ps.UniqueId}'}}) " +
                            "MERGE (pi)-[:CONTAINED_IN]->(ps)";
                        _cmdManager.cypherCommands.Enqueue(cyRel);
                        Debug.WriteLine("[Neo4j] Cypher erzeugt (Pipe->ProvSpace): " + cyRel);

                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Pipe Processing Error] {ex.Message}");
            }
        }



        private void ProcessRoom(Element room, Document doc)
        {
            if (room.LevelId == ElementId.InvalidElementId) return;
            try
            {
                // 1. Neo4j Cypher-Query
                string room_Name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Unbenannt";
                string roomName = string.IsNullOrWhiteSpace(room_Name) ? $"Unnamed_{room.Id}" : room_Name;
                string levelName = doc.GetElement(room.LevelId)?.Name ?? "Unbekannt";

                string cy = $"MERGE (r:Room {{ElementId: {room.Id.Value}}}) SET r.Name = '{EscapeString(roomName)}', r.Level = '{EscapeString(levelName)}' WITH r MATCH (l:Level {{ElementId: {room.LevelId.Value}}}) MERGE (l)-[:CONTAINS]->(r)";


                _cmdManager.cypherCommands.Enqueue(cy);
                Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Room Processing Error] {ex.Message}");
            }
        }



        private static string EscapeString(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            return input
                .Replace("\\", "")        // Backslash entfernen
                .Replace("'", "''")
                .Replace("\"", "'");      // für Cypher (doppelte Anführungszeichen → einfach)
        }

        private static string GetIfcExportClass(Element elem)
        {
            return ParameterUtils.GetIfcEntity(elem);
        }




        /// <summary>
        /// Extracts the existing situation from a model 
        /// </summary>
        /// <param name="doc"></param>
        public void CreateInitialGraph(Document doc)
        {


            // create stopwatch to measure the elapsed time
            Stopwatch timer = new Stopwatch();
            timer.Start();
            Debug.WriteLine("#--------#\nTimer started.\n#--------#");

            string buildingName = "Teststraße 21";
            string buildingNameEsc = EscapeString(buildingName);
            string cyBuilding = $"MERGE (b:Building {{Name: \"{buildingNameEsc}\"}})";
            _cmdManager.cypherCommands.Enqueue(cyBuilding);
            Debug.WriteLine("[Neo4j] Cypher erzeugt (Building): " + cyBuilding);

            // 1. Alle Level einlesen


            // Get all level
            FilteredElementCollector collector = new FilteredElementCollector(doc);
            IList<Level> levels = collector.OfClass(typeof(Level)).Cast<Level>().ToList();

            // Iterate over all level
            foreach (Level lvl in levels)
            {
                Debug.WriteLine($"Level: {lvl.Name}, ID: {lvl.Id}");
                string levelName = EscapeString(lvl.Name);
                string cy = $"MERGE (l:Level{{Name: \"{levelName}\", ElementId: {lvl.Id}}})";
                _cmdManager.cypherCommands.Enqueue(cy);
                Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);

                string cyRel =
            $"MATCH (b:Building {{Name: \"{buildingNameEsc}\"}}), " +
            $"      (l:Level    {{ElementId: {lvl.Id}}}) " +
            $"MERGE (b)-[:CONTAINS]->(l)";
                _cmdManager.cypherCommands.Enqueue(cyRel);
                Debug.WriteLine("[Neo4j] Cypher erzeugt (Building contains Level): " + cyRel);

                // get all Elements of type Room in the current level
                ElementLevelFilter lvlFilter = new ElementLevelFilter(lvl.Id);

                IList<Element> rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .WherePasses(lvlFilter)
                    .ToElements();
                // Iterate over all rooms in that level
                foreach (var element in rooms)
                {
                    var room = (Room)element;

                    if (room.LevelId == null || room.LevelId.Value == -1)
                    {
                        Debug.WriteLine($"[WARN] Raum {room.Id} hat kein gültiges Level – wird übersprungen.");
                        continue;
                    }
                    string escapedRoomName = EscapeString(room.Name);
                    Debug.WriteLine($"Room: {escapedRoomName}, ID: {room.Id}");

                    cy = $"MERGE (r:Room {{ElementId: {room.Id.Value}}}) " +
            $"SET r.Name = '{EscapeString(room.Name)}', r.Level = '{EscapeString(levelName)}' " +
            $"WITH r MATCH (l:Level {{ElementId: {room.LevelId.Value}}}) " +
            $"MERGE (l)-[:CONTAINS]->(r)";

                    _cmdManager.cypherCommands.Enqueue(cy);
                    Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);


                    IList<IList<BoundarySegment>> boundaries = room.GetBoundarySegments(new SpatialElementBoundaryOptions());

                    foreach (IList<BoundarySegment> b in boundaries)
                    {
                        foreach (BoundarySegment s in b)
                        {
                            ElementId neighborId = s.ElementId;
                            if (neighborId.Value == -1)
                            {
                                Debug.WriteLine("Something went wrong when extracting Element ID " + neighborId);
                                continue;
                            }

                            Element neighbor = doc.GetElement(neighborId);

                            if (neighbor is Wall wall)
                            {
                                string wallName = wall.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString()
                                                  ?? wall.Name ?? "Unbenannt";
                                string escapedWallName = EscapeString(wallName);

                                Debug.WriteLine($"\tNeighbor Type: Wall - ID: {wall.Id}, Name: {escapedWallName}");

                                cy = "MATCH (r:Room{ElementId:" + room.Id + "}) " +
     "MATCH (l:Level{ElementId:" + neighbor.LevelId + "}) " +
     "MERGE (w:Wall{ElementId:" + wall.Id + "}) " +
     "SET w.Name = \"" + escapedWallName + "\" " +
     "MERGE (l)-[:CONTAINS]->(w)-[:BOUNDS]->(r)";
                                _cmdManager.cypherCommands.Enqueue(cy);
                                Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);

                            }
                            else
                            {
                                Debug.WriteLine("\tNeighbor Type: Undefined - ID: " + neighbor.Id);
                            }
                        }
                    }
                }
                ProcessWalls(doc, lvl);
                var stairFilter = new ElementLevelFilter(lvl.Id);
                foreach (Element e in new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Stairs)
                    .WherePasses(stairFilter)
                    .WhereElementIsNotElementType())
                {
                    ProcessStair(e, doc);
                }

                var doorCollector = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors).OfClass(typeof(FamilyInstance)).WherePasses(lvlFilter);

                var doors = doorCollector.ToElements();

                // Iterate over all door at current level
                foreach (var door in doors)
                {
                    var inst = (FamilyInstance)door;
                    var wall = inst.Host;
                    Debug.WriteLine($"Door ID: {door.Id}, HostId: {wall.Id}");

                    string doorName = EscapeString(inst.Name);
                    cy = $"MATCH (w:Wall{{ElementId:{wall.Id}}}) " +
                         $"MATCH (l:Level{{ElementId:{door.LevelId}}}) " +
                         $"MERGE (d:Door{{ElementId:{inst.Id.Value}, Name: \"{doorName}\"}}) " +
                         $"MERGE (l)-[:CONTAINS]->(d)-[:CONTAINED_IN]->(w)";
                    _cmdManager.cypherCommands.Enqueue(cy);
                    Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);

                }


                var provCollector = new FilteredElementCollector(doc)
                                 .OfCategory(BuiltInCategory.OST_GenericModel)
                                 .OfClass(typeof(FamilyInstance))
                                 .WherePasses(lvlFilter);

                foreach (FamilyInstance inst in provCollector)
                {
                    var ifc = GetIfcExportClass(inst);
                    if (ifc == "IfcOpeningElement")
                    {
                        ProcessProvisionalSpace(inst, doc);
                    }
                }
                
                var pipeCollector = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_PipeCurves)
                    .OfClass(typeof(MEPCurve))
                    .WherePasses(lvlFilter);

                foreach (MEPCurve pipe in pipeCollector)
                {
                    ProcessPipe(pipe, doc);

                }
                            }
            foreach (Element stair in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Stairs)
                .WhereElementIsNotElementType())
            {
                ProcessStair(stair, doc);
            }

            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceTracker");
            Directory.CreateDirectory(baseDir); // falls noch nicht vorhanden

            var cyPath = Path.Combine(baseDir, "neo4j_cypher.txt");
            File.WriteAllText(cyPath, string.Join("\n", _cmdManager.cypherCommands));


            // print out the elapsed time and stop the timer
            Debug.WriteLine($"#--------#\nTimer stopped: {timer.ElapsedMilliseconds}ms\n#--------#");
            timer.Stop();
        }

        public async Task UpdateGraphAsync(
        Document doc,
        List<ElementId> EnqueuedElementIds,
        List<ElementId> deletedElementIds,
        List<ElementId> modifiedElementIds)
        {
            await Task.Run(() =>
        {
            try
            {
                // Gelöschte Elemente
                foreach (var id in deletedElementIds)
                {
                    string cyDelete = $"MATCH (n {{ElementId: {id.Value}}}) DETACH DELETE n";
                    _cmdManager.cypherCommands.Enqueue(cyDelete);
                    Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cyDelete);
                }

                // Neue/modifizierte Elemente
                ProcessElements(doc, EnqueuedElementIds.Concat(modifiedElementIds).ToList());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Neo4j-Error] {ex.Message}");
            }
        });
        }

        private void ProcessElements(Document doc, IReadOnlyCollection<ElementId> elementIds)
        {
            foreach (var id in elementIds)
            {
                var element = doc.GetElement(id);
                if (element.Category != null)
                {
                    var bic = (BuiltInCategory)element.Category.Id.Value;
                    switch (bic)
                    {
                        case BuiltInCategory.OST_Rooms:
                            ProcessRoom(element, doc);
                            break;
                        case BuiltInCategory.OST_Walls:
                            ProcessWall(element, doc);
                            break;
                        case BuiltInCategory.OST_GenericModel when element is FamilyInstance fi:
                            ProcessProvisionalSpace(fi, doc);
                            break;
                        case BuiltInCategory.OST_PipeCurves:
                            if (element is MEPCurve pipe)
                                ProcessPipe(pipe, doc);
                            break;
                        case BuiltInCategory.OST_Doors:
                            ProcessDoor(element, doc);
                            break;
                        case BuiltInCategory.OST_Stairs:

                            ProcessStair(element, doc);
                            break;
                        default:
                            Debug.WriteLine($"[Neo4j] Ignoriere Kategorie: {bic}");
                            break;
                    }


                }
            }
        }
        /// <summary>
        /// Verarbeitet eine einzelne Treppen-Instanz und verbindet sie mit den Basis- und Ober-Ebenen.
        /// </summary>
        private void ProcessStair(Element stairElem, Document doc)
        {
            var baseParam = stairElem.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM);
            ElementId baseLevelId = (baseParam != null && baseParam.AsElementId() != ElementId.InvalidElementId)
                  ? baseParam.AsElementId()
                  : stairElem.LevelId;

            var topParam = stairElem.get_Parameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM);
            ElementId topLevelId = (topParam != null && topParam.AsElementId() != ElementId.InvalidElementId)
                ? topParam.AsElementId()
                : stairElem.LevelId;

            // 2) Revit-Level-Instanzen
            var baseLevel = doc.GetElement(baseLevelId) as Level;
            var topLevel = doc.GetElement(topLevelId) as Level;
            if (baseLevel == null || topLevel == null)
            {
                Debug.WriteLine($"[Stair Processing] Levels not found for stair {stairElem.Id}; base={baseLevelId}, top={topLevelId}");
                return;  // ohne beide Ebenen keine Relationship
            }
            // 3) Lesbarer Name für die Treppe
            string stairName = GenerateStairName(baseLevel.Name, topLevel.Name);
            // 4) Cypher-Statement: Node MERGE + Beziehungen
            string cy =
                $"MERGE (s:Stair {{ElementId: {stairElem.Id.Value}}}) " +
                $"SET s.Name = '{EscapeString(stairName)}' " +
                $"WITH s " +
                $"MATCH (b:Level {{ElementId: {baseLevelId.Value}}}), (t:Level {{ElementId: {topLevelId.Value}}}) " +
                $"MERGE (b)-[:CONNECTS_TO]->(s) " +
                $"MERGE (s)-[:CONNECTS_TO]->(t)";

            _cmdManager.cypherCommands.Enqueue(cy);
            Debug.WriteLine("[Neo4j] Cypher erzeugt (Stair-Verbindungen): " + cy);
        }


        public string ExportIfcSubset(Document doc, List<ElementId> elementsToExport)
        {
            // 1. Temporäre 3D-Ansicht erstellen und Elemente isolieren
            View3D view = null;
            using (var tx = new Transaction(doc, "Temp IFC View"))
            {
                tx.Start();
                view = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .FirstOrDefault(v => !v.IsTemplate && v.CanBePrinted);
                if (view == null)
                    throw new InvalidOperationException("Keine 3D-View gefunden!");

                view.IsolateElementsTemporary(elementsToExport);
                tx.Commit();
            }

            // 2. IFC-Export-Optionen setzen
            var ifcExportOptions = new IFCExportOptions
            {
                FileVersion = IFCVersion.IFC4,
                FilterViewId = view.Id,
                ExportBaseQuantities = true
            };

            // 3. Exportieren
            // 3. Exportieren in ein sitzungsspezifisches Temp-Verzeichnis
            var sessionDir = Path.Combine(Path.GetTempPath(), CommandManager.Instance.SessionId);
            Directory.CreateDirectory(sessionDir);
            var tempIfcPath = Path.Combine(sessionDir, $"change_{Guid.NewGuid()}.ifc");

            // Der Export ändert das Dokument und muss daher in einer Transaction
            // ausgeführt werden.
            using (var txExport = new Transaction(doc, "Export IFC Subset"))
            {
                txExport.Start();
                doc.Export(Path.GetDirectoryName(tempIfcPath), Path.GetFileName(tempIfcPath), ifcExportOptions);
                txExport.Commit();
            }

            // 4. Isolation zurücksetzen
            using (var tx = new Transaction(doc, "Unisolate IFC View"))
            {
                tx.Start();
                view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                tx.Commit();
            }

            return tempIfcPath;
        }



        // Deletes all previously existing data (convenient for debugging)


        public void UpdateGraph(Document doc, List<Element> EnqueuedElements, List<ElementId> deletedElementIds, List<Element> modifiedElements)
        {
            Debug.WriteLine(" Starting to update Graph...\n");
            // Reset stair numbering for each update run
            _stairCounters.Clear();
            string cy;

            // delete nodes
            foreach (ElementId id in deletedElementIds)
            {
                Debug.WriteLine($"Deleting Node with ID: {id}");
                int intId = (int)id.Value;
                string cyDel = $"MATCH (n {{ElementId: {id}}}) DETACH DELETE n";
                _cmdManager.cypherCommands.Enqueue(cyDel);
                Debug.WriteLine("[Neo4j] Node deletion Cypher: " + cyDel);


                Element e = doc.GetElement(id);
                if (e == null)
                {
                    Debug.WriteLine($"[Warning] Gelöschtes Element {id} nicht mehr im Doc vorhanden, SQL überspringe.");
                    continue;
                }

                Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cyDel);


            }
            // Diese Syntax ist perfekt
            foreach (Element e in modifiedElements)
            {
                ElementId id = e.Id;


                // change properties
                int intId = (int)e.Id.Value;
                if (e is FamilyInstance fi && fi.Category.Id.Value == (int)BuiltInCategory.OST_Doors)
                {
                    // Tür-Typ und Name aktualisieren
                    var sym = doc.GetElement(fi.GetTypeId()) as FamilySymbol;
                    string doorType = sym?.Name ?? "Unbekannter Typ";
                    cy = $"MATCH (d:Door {{ElementId: {intId}}}) " +
                         $"SET d.Name = '{EscapeString(e.Name)}', d.Type = '{EscapeString(doorType)}'";
                }
                else if (e is Room)
                {
                    // Raum-Name aktualisieren
                    cy = $"MATCH (r:Room {{ElementId: {intId}}}) " +
                         $"SET r.Name = '{EscapeString(e.Name)}'";
                }
                else if (e is Wall)
                {
                    // Wand-Name aktualisieren
                    cy = $"MATCH (w:Wall {{ElementId: {intId}}}) " +
                         $"SET w.Name = '{EscapeString(e.Name)}'";
                }
                else if (e is Level)
                {
                    cy = $"MATCH (l:Level {{ElementId: {intId}}}) " +
                         $"SET l.Name = '{EscapeString(e.Name)}'";
                }
                else
                {
                    // unbekannter Typ überspringen
                    continue;
                }

                _cmdManager.cypherCommands.Enqueue(cy);
                Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);

                // change relationships
                if (typeof(Room).IsAssignableFrom(e.GetType()))
                {
                    Debug.WriteLine($"Modifying Node with ID: {id} and Name: {e.Name}");



                    Room room = e as Room;
                    // get all boundaries
                    IList<IList<BoundarySegment>> boundaries
                    = room.GetBoundarySegments(new SpatialElementBoundaryOptions());

                    foreach (IList<BoundarySegment> b in boundaries)
                    {
                        // Iterate over all elements adjacent to current room
                        foreach (BoundarySegment s in b)
                        {
                            // get neighbor element
                            ElementId neighborId = s.ElementId;
                            if (neighborId.Value == -1)
                            {
                                Debug.WriteLine(" Something went wrong when extracting Element ID " + neighborId);
                                continue;
                            }

                            Element neighbor = doc.GetElement(neighborId);
                            var levelId = neighbor.LevelId;

                            if (neighbor is Wall wall)
                            {

                                if (wall.LevelId == ElementId.InvalidElementId)
                                {
                                    Debug.WriteLine($"[WARN] Wall {wall.Id} has invalid LevelId.");
                                    continue; // Überspringen
                                }
                                string escapedWallName = EscapeString(wall.Name);
                                cy = "MATCH (r:Room{ElementId: " + room.Id + "})" +
        " MATCH (w:Wall{ElementId: " + wall.Id + "})" +
        " MATCH (l:Level{ElementId: " + wall.LevelId.Value + "})" +
        " MERGE (l)-[:CONTAINS]->(w)-[:BOUNDS]->(r)";
                                _cmdManager.cypherCommands.Enqueue(cy);
                                Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);




                                Debug.WriteLine($"Modified Room with ID: {id} and Name: {e.Name}");


                            }
                        }
                    }
                }
                if (typeof(Wall).IsAssignableFrom(e.GetType()))
                {
                    Debug.WriteLine($"Modifying Node with ID: {id} and Name: {e.Name}");


                    // get the room
                    IList<Element> rooms = getRoomFromWall(doc, e as Wall);


                    foreach (Element element in rooms)
                    {
                        var room = (Room)element;
                        var levelId = room.LevelId;
                        cy = " MATCH (w:Wall{ElementId: " + id + "}) " +
                             " MATCH (r:Room{ElementId: " + room.Id + "})" +
                             " MATCH (l:Level{ElementId: " + levelId + "})" +
                             " MERGE (l)-[:CONTAINS]->(w)-[:BOUNDS]->(r)";
                        _cmdManager.cypherCommands.Enqueue(cy);
                        Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);


                        Debug.WriteLine($"Modified Wall with ID: {id} and Name: {e.Name} ");
                    }
                }

                if (typeof(Level).IsAssignableFrom(e.GetType()))
                {
                    Debug.WriteLine($"Modifying Node with ID: {id} and Name: {e.Name}");


                    ElementLevelFilter lvlFilter = new ElementLevelFilter(id);
                    FilteredElementCollector collector = new FilteredElementCollector(doc);
                    IList<Element> elementsOnLevel = collector.WherePasses(lvlFilter).ToElements();

                    foreach (Element element in elementsOnLevel)
                    {
                        if (typeof(Wall).IsAssignableFrom(element.GetType()))
                        {
                            cy = " MATCH (l:Level{ElementId: " + id + "}) " +
                                 " MATCH (w:Wall{ElementId: " + element.Id + "}) " +
                                 " MERGE (l)-[:CONTAINS]->(w)";
                            _cmdManager.cypherCommands.Enqueue(cy);
                            Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);


                        }
                        else if (typeof(Room).IsAssignableFrom(element.GetType()))
                        {
                            cy = " MATCH (l:Level{ElementId: " + id + "}) " +
                                 " MATCH (r:Room{ElementId: " + element.Id + "}) " +
                                 " MERGE (l)-[:CONTAINS]->(r)";
                            _cmdManager.cypherCommands.Enqueue(cy);
                            Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);


                        }

                        Debug.WriteLine($"Modified Level with ID: {id} and Name: {e.Name}");
                    }
                }
            }

            foreach (var e in EnqueuedElements)
            {
                switch (e)
                {
                    case Room room:
                        ProcessRoom(room, doc);
                        break;
                    case Wall wall:
                        ProcessWall(wall, doc);
                        break;
                    case FamilyInstance fi when fi.Category.Id.Value == (int)BuiltInCategory.OST_Doors:
                        ProcessDoor(fi, doc);
                        break;
                    case Element st when st.Category.Id.Value == (int)BuiltInCategory.OST_Stairs:
                        // Directly process the stair element. Level information
                        // will be resolved inside ProcessStair.
                        ProcessStair(st, doc);
                        break;
                }
            }

            //Enqueue nodes
            var EnqueuedElementIds = EnqueuedElements.Select(e => e.Id).ToList();
            foreach (ElementId id in EnqueuedElementIds)
            {
                Element e = doc.GetElement(id);

                if (typeof(Room).IsAssignableFrom(e.GetType()))
                {
                    var room = (Room)e;

                    // capture result
                    Debug.WriteLine($"Room: {room.Name}, ID: {room.Id}");

                    cy = " MATCH (l:Level{ElementId:" + room.LevelId + "}) " +
                         " MERGE (r:Room{Name: \"" + room.Name + "\", ElementId: " + room.Id + "}) " +
                         " MERGE (l)-[:CONTAINS]->(r)";
                    _cmdManager.cypherCommands.Enqueue(cy);
                    Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);

                    // get all boundaries
                    IList<IList<BoundarySegment>> boundaries
                    = room.GetBoundarySegments(new SpatialElementBoundaryOptions());


                    foreach (IList<BoundarySegment> b in boundaries)
                    {
                        // Iterate over all elements adjacent to current room
                        foreach (BoundarySegment s in b)
                        {

                            // get neighbor element
                            ElementId neighborId = s.ElementId;
                            if (neighborId.Value == -1)
                            {
                                Debug.WriteLine(" Something went wrong when extracting Element ID " + neighborId);
                                continue;
                            }

                            Element neighbor = doc.GetElement(neighborId);

                            if (neighbor is Wall)
                            {
                                Debug.WriteLine($"\tNeighbor Type: Wall - ID: {neighbor.Id}");

                                cy = " MATCH (r:Room{ElementId:" + room.Id + "}) " +
                                     " MATCH (l:Level{ElementId:" + neighbor.LevelId + "}) " +
                                     " MERGE (w:Wall{ElementId: " + neighbor.Id + ", Name: \"" + neighbor.Name + "\"}) " +
                                     " MERGE (l)-[:CONTAINS]->(w)-[:BOUNDS]->(r)";
                                _cmdManager.cypherCommands.Enqueue(cy);
                                Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);
                            }
                            else
                            {
                                Debug.WriteLine("\tNeighbor Type: Undefined - ID: " + neighbor.Id);
                            }
                        }
                    }
                }
                if (typeof(Wall).IsAssignableFrom(e.GetType()))
                {
                    var wall = (Wall)e;
                    Debug.WriteLine($"Room: {wall.Name}, ID: {wall.Id}");

                    cy = " MERGE (w:Wall{ElementId: " + wall.Id + "})";
                    _cmdManager.cypherCommands.Enqueue(cy);
                    Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);
                }


            }




        }

        public static IList<Element> getRoomFromWall(Document doc, Wall wall)
        {
            BoundingBoxXYZ wall_bb = wall.get_BoundingBox(null);
            Outline outl = new Outline(wall_bb.Min, wall_bb.Max);
            ElementFilter bbfilter = new BoundingBoxIntersectsFilter(outl);

            IList<Element> rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WherePasses(bbfilter).ToElements();

            return rooms;



        }

    }
}