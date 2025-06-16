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


namespace SpaceTracker
{
    public class SpaceExtractor
    {


        private readonly CommandManager _cmdManager;

        private static int _wallCounter = 0;

        private readonly Dictionary<(string baseLevel, string topLevel), int> _stairCounters
    = new Dictionary<(string, string), int>();


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

        public string ExportIfcSubset(Document doc, List<ElementId> elementsToExport)
        {
            // 1. Temporäre 3D-View isolieren
            View3D view;
            using (var tx = new Transaction(doc, "Tmp Isolate"))
            {
                tx.Start();
                view = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .FirstOrDefault(v => !v.IsTemplate && v.CanBePrinted)
                    ?? throw new InvalidOperationException("Keine 3D-View gefunden");
                view.IsolateElementsTemporary(elementsToExport);
                tx.Commit();
            }

            // 2. IFC-Export-Optionen
            var opts = new IFCExportOptions
            {
                FileVersion = IFCVersion.IFC4,
                FilterViewId = view.Id,
                ExportBaseQuantities = true
            };

            // 3. Export
            var path = Path.Combine(Path.GetTempPath(), $"delta_{Guid.NewGuid():N}.ifc");
            doc.Export(Path.GetDirectoryName(path), Path.GetFileName(path), opts);

            // 4. Isolation zurücksetzen
            using (var tx = new Transaction(doc, "Tmp Unisolate"))
            {
                tx.Start();
                view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                tx.Commit();
            }

            return path;
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

                // 2. SQLite-Query
                /*        string sql = $@"
                    INSERT OR REPLACE INTO Wall 
                    (ElementId, Name, LevelId, Type) 
                    VALUES ({wall.Id.Value}, '{EscapeString(wallName)}', {wall.LevelId.Value}, '{EscapeString(wallName)}')";
                        _cmdManager.EnqueueSql(sql);*/

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



                // 2. SQLite-Query
                /*  string sql = $@"
                      INSERT OR REPLACE INTO Door 
                      (ElementId, Name, WallId) 
                      VALUES ({door.Id.Value}, '{EscapeString(doorName)}', {(hostWall?.Id.Value != null ? hostWall.Id.Value.ToString() : "NULL")})";

                  if (!_cmdManager.sqlCommands.Contains(sql))
                      _cmdManager.EnqueueSql(sql);*/
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Door Processing Error] {ex.Message}");
            }
        }


        private void ProcessStairs(Document doc, Level level)
        {
            var provider = new ParameterValueProvider(
    new ElementId((int)BuiltInParameter.STAIRS_BASE_LEVEL_PARAM));
            var rule = new FilterElementIdRule(provider,
                new FilterNumericEquals(), level.Id);
            var stairFilter = new ElementParameterFilter(rule);

            // 2. Collector mit Parameter-Filter
            var collector = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Stairs)
                .WherePasses(stairFilter)
                .WhereElementIsNotElementType();

            foreach (Element elem in collector)
            {
                var bottomParam = elem.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM);
                ElementId bottomId = bottomParam != null
                    ? bottomParam.AsElementId()
                    : ElementId.InvalidElementId;

                var topParam = elem.get_Parameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM);
                ElementId topId = topParam != null
                    ? topParam.AsElementId()
                    : ElementId.InvalidElementId;

                // Level-Instanzen laden (Fallback auf null, falls Parameter fehlt)
                var bottomLevel = bottomId != ElementId.InvalidElementId
                    ? doc.GetElement(bottomId) as Level
                    : null;
                var topLevel = topId != ElementId.InvalidElementId
                    ? doc.GetElement(topId) as Level
                    : null;

                // 3. Name aus Level-Namen zusammensetzen
                string bottomName = bottomLevel?.Name ?? "Unbekannt";
                string topName = topLevel?.Name ?? "Unbekannt";
                string stairName = $"Treppe {bottomName} {topName}";

                // 4. Cypher-Statement erzeugen
                string cy =
    $"MERGE (s:Stair {{ElementId: {elem.Id.Value}}}) " +
    $"SET s.Name = '{EscapeString(stairName)}' " +
    // bringe bottom/top Level in den Scope
    $"WITH s " +
    $"MATCH (l1:Level {{ElementId: {bottomId.Value}}}), " +
    $"      (l2:Level {{ElementId: {topId.Value}}}) " +
    // erstelle die Beziehungen
    $"MERGE (l1)-[:CONNECTS_TO]->(s) " +
    $"MERGE (s)-[:CONNECTS_TO]->(l2)";

                _cmdManager.cypherCommands.Enqueue(cy);
                Debug.WriteLine("[Neo4j] Cypher erzeugt (Stair + Level-Rels): " + cy);
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

                // 2. SQLite-Query
                /*    string sql = $@"
                        INSERT OR REPLACE INTO Room
                        (ElementId, Name, LevelId) 
                        VALUES ({room.Id.Value}, '{EscapeString(roomName)}', {room.LevelId.Value})";

                    if (!_cmdManager.sqlCommands.Contains(sql))
                        _cmdManager.EnqueueSql(sql);*/
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Room Processing Error] {ex.Message}");
            }
        }



        private string EscapeString(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            return input
                .Replace("\\", "")        // Backslash entfernen
                .Replace("'", "''")       // für SQL
                .Replace("\"", "'");      // für Cypher (doppelte Anführungszeichen → einfach)
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

                /* string sql = "INSERT OR REPLACE INTO Level (ElementId, Name) VALUES (" + lvl.Id + ", '" + lvl.Name + "');";
                 _cmdManager.EnqueueSql(sql);*/

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

                    /*    sql = "INSERT OR REPLACE INTO Room (ElementId, Name) VALUES (" + room.Id + ", '" + escapedRoomName + "');";
                        _cmdManager.EnqueueSql(sql);

                        sql = "INSERT OR REPLACE INTO contains (LevelId, ElementId) VALUES (" + room.LevelId + ", " + room.Id + ");";
                        _cmdManager.EnqueueSql(sql);*/

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

                                /*     sql = $"REPLACE INTO Wall (ElementId, Name) VALUES ({wall.Id}, '{escapedWallName}');";
                                     if (!_cmdManager.sqlCommands.Contains(sql))
                                     {
                                         _cmdManager.EnqueueSql(sql);
                                     }

                                     sql = $"REPLACE INTO bounds (WallId, RoomId) VALUES ({wall.Id}, {room.Id});";
                                     if (!_cmdManager.sqlCommands.Contains(sql))
                                     {
                                         _cmdManager.EnqueueSql(sql);
                                     }

                                     sql = $"REPLACE INTO contains (LevelId, ElementId) VALUES ({neighbor.LevelId}, {wall.Id});";
                                     if (!_cmdManager.sqlCommands.Contains(sql))
                                     {
                                         _cmdManager.EnqueueSql(sql);
                                     }*/
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


                    /*      sql = " INSERT OR REPLACE INTO Door (ElementId, Name, WallId) VALUES (" + door.Id + ", '" + door.Name + "', " + wall.Id + ");";
                          _cmdManager.EnqueueSql(sql);
                          // insert level into table
                          sql = " INSERT OR REPLACE INTO contains (LevelId, ElementId) VALUES (" + door.LevelId + ", " + door.Id + ");";
                          _cmdManager.EnqueueSql(sql);*/
                }
            }

            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceTracker");
            Directory.CreateDirectory(baseDir); // falls noch nicht vorhanden

            var cyPath = Path.Combine(baseDir, "neo4j_cypher.txt");
            File.WriteAllText(cyPath, string.Join("\n", _cmdManager.cypherCommands));

            var sqlPath = Path.Combine(baseDir, "sql_commands.txt");
            File.WriteAllText(sqlPath, string.Join("\n", _cmdManager.sqlCommands));

            // print out the elapsed time and stop the timer
            Debug.WriteLine($"#--------#\nTimer stopped: {timer.ElapsedMilliseconds}ms\n#--------#");
            timer.Stop();
        }

        public async Task UpdateGraphAsync(
        Document doc,
        List<ElementId> EnqueueedElementIds,
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
                ProcessElements(doc, EnqueueedElementIds.Concat(modifiedElementIds).ToList());
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
            // 1) Basis- und Ober-Ebenen-Parameter auslesen
            var baseParam = stairElem.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM);
            var topParam = stairElem.get_Parameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM);

            ElementId baseLevelId = baseParam != null
                ? baseParam.AsElementId()
                : ElementId.InvalidElementId;
            ElementId topLevelId = topParam != null
                ? topParam.AsElementId()
                : ElementId.InvalidElementId;

            // 2) Revit-Level-Instanzen
            var baseLevel = doc.GetElement(baseLevelId) as Level;
            var topLevel = doc.GetElement(topLevelId) as Level;
            if (baseLevel == null || topLevel == null)
                return;  // ohne beide Ebenen keine Relationship

            // 3) Lesbarer Name für die Treppe
            string stairName = $"Treppe {EscapeString(baseLevel.Name)}→{EscapeString(topLevel.Name)}";

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


        /*private string ExportIfcSubset(Document doc, List<ElementId> elementsToExport)
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
            var tempIfcPath = Path.Combine(Path.GetTempPath(), $"change_{Guid.NewGuid()}.ifc");
            doc.Export(Path.GetDirectoryName(tempIfcPath), Path.GetFileName(tempIfcPath), ifcExportOptions);

            // 4. Isolation zurücksetzen
            using (var tx = new Transaction(doc, "Unisolate IFC View"))
            {
                tx.Start();
                view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                tx.Commit();
            }

            return tempIfcPath;
        }*/



        // Deletes all previously existing data (convenient for debugging)


        public void UpdateGraph(Document doc, List<Element> EnqueueedElements, List<ElementId> deletedElementIds, List<Element> modifiedElements)
        {
            Debug.WriteLine(" Starting to update Graph...\n");
            string cy;
            // string sql;
            // delete nodes
            foreach (ElementId id in deletedElementIds)
            {
                Debug.WriteLine($"Deleting Node with ID: {id}");
                int intId = (int)id.Value;
                string cyDel = $"MATCH (n {{ElementId: {id}}}) DETACH DELETE n";
                _cmdManager.cypherCommands.Enqueue(cyDel);
                Debug.WriteLine("[Neo4j] Node deletion Cypher: " + cyDel);

                // 2) erst danach versuchen, das Revit-Element zu holen (für SQL o.ä.)
                Element e = doc.GetElement(id);
                if (e == null)
                {
                    Debug.WriteLine($"[Warning] Gelöschtes Element {id} nicht mehr im Doc vorhanden, SQL überspringe.");
                    continue;
                }

                Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cyDel);

                /*   if (typeof(Room).IsAssignableFrom(e.GetType()))
                   {
                       sql = " DELETE FROM Room WHERE ElementId = " + id;
                       _cmdManager.EnqueueSql(sql);

                       sql = " DELETE FROM bounds WHERE RoomId = " + id;
                       _cmdManager.EnqueueSql(sql);

                       sql = " DELETE FROM contains WHERE ElementId = " + id;
                       _cmdManager.EnqueueSql(sql);
                   }
                   else if (typeof(Wall).IsAssignableFrom(e.GetType()))
                   {
                       sql = " DELETE FROM Wall WHERE ElementId = " + id;
                       _cmdManager.EnqueueSql(sql);

                       sql = " DELETE FROM bounds WHERE WallId = " + id;
                       _cmdManager.EnqueueSql(sql);

                       sql = " DELETE FROM contains WHERE ElementId = " + id;
                       _cmdManager.EnqueueSql(sql);
                   }
                   else if (typeof(Level).IsAssignableFrom(e.GetType()))
                   {
                       sql = " DELETE FROM Level Where ElementId = " + id;
                       _cmdManager.EnqueueSql(sql);

                       sql = " DELETE FROM contains WHERE LevelId = " + id;
                       _cmdManager.EnqueueSql(sql);
                   }
                   else if (e is FamilyInstance fi && fi.Category.Id.Value == (int)BuiltInCategory.OST_Doors)
                   {
                       _cmdManager.EnqueueSql($"DELETE FROM Door WHERE ElementId = {id}");
                       _cmdManager.EnqueueSql($"DELETE FROM contains WHERE ElementId = {id}");
                   }
                   else if (e.Category.Id.Value == (int)BuiltInCategory.OST_Stairs)
                   {
                       _cmdManager.EnqueueSql($"DELETE FROM Stair WHERE ElementId = {id}");
                       // falls es eine eigene contains-/bounds-Tabelle gibt, ebenfalls löschen
                   }



               }
   */
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

                    /*       sql = " UPDATE Room SET Name = " + e.Name + " WHERE ElementId = " + id;
                           _cmdManager.EnqueueSql(sql);*/

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


                                /*        sql = " INSERT OR REPLACE INTO Wall (ElementId, Name) VALUES (" + neighbor.Id + ", '" + neighbor.Name + "');";
                                        _cmdManager.EnqueueSql(sql);

                                        sql = " INSERT OR REPLACE INTO bounds (WallId, RoomId) VALUES (" + neighbor.Id + ", " + room.Id + ");";
                                        _cmdManager.EnqueueSql(sql);

                                        sql = " INSERT OR REPLACE INTO contains (LevelId, ElementId) VALUES (" + neighbor.LevelId + ", " + neighbor.Id + ");";
                                        _cmdManager.EnqueueSql(sql);
        */
                                Debug.WriteLine($"Modified Room with ID: {id} and Name: {e.Name}");


                            }
                        }
                    }
                }
                if (typeof(Wall).IsAssignableFrom(e.GetType()))
                {
                    Debug.WriteLine($"Modifying Node with ID: {id} and Name: {e.Name}");

                    /*          sql = $"UPDATE Wall SET Name = " + e.Name + " WHERE ElementId = " + id;
                              _cmdManager.EnqueueSql(sql);*/

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

                        /*       sql = " INSERT OR REPLACE INTO Wall (ElementId, Name) VALUES (" + id + ", '" + e.Name + "');";
                               _cmdManager.EnqueueSql(sql); */
                        Debug.WriteLine($"Modified Wall with ID: {id} and Name: {e.Name} ");
                    }
                }

                if (typeof(Level).IsAssignableFrom(e.GetType()))
                {
                    Debug.WriteLine($"Modifying Node with ID: {id} and Name: {e.Name}");

                    //       sql = " UPDATE Level SET Name = " + e.Name + " WHERE ElementId = " + id;
                    //     _cmdManager.EnqueueSql(sql);

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

                            //           sql = " INSERT OR REPLACE INTO Wall (ElementId, Name) VALUES (" + id + ", '" + e.Name + "');";
                            //           _cmdManager.EnqueueSql(sql);

                            //         sql = " INSERT OR REPLACE INTO contains (LevelId, ElementId) VALUES (" + id + ", " + element.Id + ");";
                            //        _cmdManager.EnqueueSql(sql);
                        }
                        else if (typeof(Room).IsAssignableFrom(element.GetType()))
                        {
                            cy = " MATCH (l:Level{ElementId: " + id + "}) " +
                                 " MATCH (r:Room{ElementId: " + element.Id + "}) " +
                                 " MERGE (l)-[:CONTAINS]->(r)";
                            _cmdManager.cypherCommands.Enqueue(cy);
                            Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);

                            //             sql = " INSERT OR REPLACE INTO Wall (ElementId, Name) VALUES (" + id + ", '" + e.Name + "');";
                            //            _cmdManager.EnqueueSql(sql);

                            //            sql = " INSERT OR REPLACE INTO contains (LevelId, ElementId) VALUES (" + id + ", '" + element.Id + "');";
                            //             _cmdManager.EnqueueSql(sql);
                        }

                        Debug.WriteLine($"Modified Level with ID: {id} and Name: {e.Name}");
                    }
                }
            }

            foreach (var e in EnqueueedElements)
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
                        var lvl = doc.GetElement(st.LevelId) as Level;
                        if (lvl != null) ProcessStairs(doc, lvl);
                        break;
                }
            }

            //Enqueue nodes
            var EnqueueedElementIds = EnqueueedElements.Select(e => e.Id).ToList();
            foreach (ElementId id in EnqueueedElementIds)
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

        public IList<Element> getRoomFromWall(Document doc, Wall wall)
        {
            BoundingBoxXYZ wall_bb = wall.get_BoundingBox(null);
            Outline outl = new Outline(wall_bb.Min, wall_bb.Max);
            ElementFilter bbfilter = new BoundingBoxIntersectsFilter(outl);

            IList<Element> rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WherePasses(bbfilter).ToElements();

            return rooms;



        }

    }
}