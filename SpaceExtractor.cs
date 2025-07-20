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
using System.Text.RegularExpressions;
using Autodesk.Revit.UI;



namespace SpaceTracker
{
    public class SpaceExtractor
    {


        private readonly CommandManager _cmdManager;
        // Separate Logdatei für ProvisionalSpaces
        private const string ProvLog = "provisional_spaces.log";

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
        // Konstruktor, erhält den CommandManager zum Einreihen von Cypher-Befehlen.
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
                Dictionary<string, object> data = WallSerializer.ToNode((Wall)wall);
                var inv = CultureInfo.InvariantCulture;
                var setParts = new List<string>
                {
                    $"w.uid = '{ParameterUtils.EscapeForCypher(data["uid"].ToString())}'",
                    $"w.elementId = {wall.Id.Value}",
                    $"w.typeId = {data["typeId"]}",
                    $"w.typeName = '{ParameterUtils.EscapeForCypher(data["typeName"].ToString())}'",
                    $"w.familyName = '{ParameterUtils.EscapeForCypher(data["familyName"].ToString())}'",
                    $"w.Name = '{ParameterUtils.EscapeForCypher(data["Name"].ToString())}'",
                    $"w.levelId = {data["levelId"]}",
                    $"w.x1 = {((double)data["x1"]).ToString(inv)}",
                    $"w.y1 = {((double)data["y1"]).ToString(inv)}",
                    $"w.z1 = {((double)data["z1"]).ToString(inv)}",
                    $"w.x2 = {((double)data["x2"]).ToString(inv)}",
                    $"w.y2 = {((double)data["y2"]).ToString(inv)}",
                    $"w.z2 = {((double)data["z2"]).ToString(inv)}",
                    $"w.height_mm = {((double)data["height_mm"]).ToString(inv)}",
                    $"w.thickness_mm = {((double)data["thickness_mm"]).ToString(inv)}",
                    $"w.structural = {data["structural"]}",
                    $"w.flipped = {data["flipped"]}",
                    $"w.base_offset_mm = {((double)data["base_offset_mm"]).ToString(inv)}",
                    $"w.location_line = {data["location_line"]}",
                    $"w.user = '{ParameterUtils.EscapeForCypher(data["user"].ToString())}'",
                    $"w.created = datetime('{((DateTime)data["created"]).ToString("o")}')",
                    $"w.modified = datetime('{((DateTime)data["modified"]).ToString("o")}')",
                    $"w.lastModifiedUtc = datetime('{((DateTime)data["modified"]).ToString("o")}')"

                };

                string cy =
                  $"MATCH (l:Level {{ElementId: {wall.LevelId.Value}}}) MERGE (w:Wall {{ElementId: {wall.Id.Value}}}) SET {string.Join(", ", setParts)} MERGE (l)-[:CONTAINS]->(w)";


                _cmdManager.cypherCommands.Enqueue(cy);
                Debug.WriteLine("[Neo4j] Created Wall node: " + cy);

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Wall Processing Error] {ex.Message}");
            }
        }

        private void ProcessDoor(Element door, Document doc)
        {
            if (door.Category?.Id.Value != (int)BuiltInCategory.OST_Doors)
                return;
            try
            {
                // 1. Neo4j Cypher-Query
                string doorName = door.get_Parameter(BuiltInParameter.DOOR_NUMBER)?.AsString() ?? "Unbenannt";
                FamilyInstance doorInstance = door as FamilyInstance;
                Element hostWall = doorInstance?.Host;
                var sym = doc.GetElement(door.GetTypeId()) as FamilySymbol;
                Dictionary<string, object> data = doorInstance != null ? DoorSerializer.ToNode(doorInstance) : new();
                var inv = CultureInfo.InvariantCulture;
                var setParts = new List<string>
                {
                    // 1) Tür mit Wand und Level verknüpfen
                    $"d.uid = '{ParameterUtils.EscapeForCypher(data.GetValueOrDefault("uid", door.UniqueId).ToString())}'",
                    $"d.elementId = {door.Id.Value}",
                    $"d.name = '{ParameterUtils.EscapeForCypher(doorName)}'",
                    $"d.typeId = {door.GetTypeId().Value}",
                    $"d.familyName = '{ParameterUtils.EscapeForCypher(data.GetValueOrDefault("familyName", string.Empty).ToString())}'",
                    $"d.symbolName = '{ParameterUtils.EscapeForCypher(data.GetValueOrDefault("symbolName", string.Empty).ToString())}'",
                    $"d.levelId = {door.LevelId.Value}",
                    $"d.hostId = {doorInstance?.Host?.Id.Value ?? -1}",
                    $"d.hostUid = '{ParameterUtils.EscapeForCypher(doorInstance?.Host?.UniqueId ?? string.Empty)}'",
                    $"d.x = {((double)data.GetValueOrDefault("x", 0.0)).ToString(inv)}",
                    $"d.y = {((double)data.GetValueOrDefault("y", 0.0)).ToString(inv)}",
                    $"d.z = {((double)data.GetValueOrDefault("z", 0.0)).ToString(inv)}",
                    $"d.rotation = {((double)data.GetValueOrDefault("rotation", 0.0)).ToString(inv)}",
                    $"d.width = {((double)data.GetValueOrDefault("width", 0.0)).ToString(inv)}",
                    $"d.height = {((double)data.GetValueOrDefault("height", 0.0)).ToString(inv)}",
                    $"d.thickness = {((double)data.GetValueOrDefault("thickness", 0.0)).ToString(inv)}",
$"d.user = '{ParameterUtils.EscapeForCypher(data.GetValueOrDefault("user", CommandManager.Instance.SessionId).ToString())}'",                    $"d.created = datetime('{((DateTime)data.GetValueOrDefault("created", DateTime.UtcNow)).ToString("o")}')",
                    $"d.modified = datetime('{((DateTime)data.GetValueOrDefault("modified", DateTime.UtcNow)).ToString("o")}')"
                };

                string cyBase = $"MATCH (l:Level {{ElementId: {door.LevelId.Value}}})";
                if (hostWall != null)
                    cyBase += $", (w:Wall {{ElementId: {hostWall.Id.Value}}})";
                string cyNode =
                    $"{cyBase} MERGE (d:Door {{ElementId: {door.Id.Value}}}) SET {string.Join(", ", setParts)}";
                if (hostWall != null)
                    cyNode += " MERGE (l)-[:CONTAINS]->(d) MERGE (d)-[:INSTALLED_IN]->(w)";
                else
                    cyNode += " MERGE (l)-[:CONTAINS]->(d)";

                _cmdManager.cypherCommands.Enqueue(cyNode);
                Debug.WriteLine("[Neo4j] Created Door node: " + cyNode);


            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Door Processing Error] {ex.Message}");
            }
        }

        private void ProcessWindow(Element window, Document doc)
        {
            if (window.Category?.Id.Value != (int)BuiltInCategory.OST_Windows)
                return;

            try
            {
                var winInstance = window as FamilyInstance;
                Element hostWall = winInstance?.Host;
                string winName = ParameterUtils.EscapeForCypher(window.Name);

                string cyBase = $"MATCH (l:Level {{ElementId: {window.LevelId.Value}}})";
                if (hostWall != null)
                    cyBase += $", (w:Wall {{ElementId: {hostWall.Id.Value}}})";

                string cyNode =
                    $"{cyBase} MERGE (wi:Window {{ElementId: {window.Id.Value}}}) SET wi.Name = '{winName}'";

                cyNode += " MERGE (l)-[:CONTAINS]->(wi)";
                if (hostWall != null)
                    cyNode += " MERGE (wi)-[:INSTALLED_IN]->(w)";

                _cmdManager.cypherCommands.Enqueue(cyNode);
                Debug.WriteLine("[Neo4j] Created Window node: " + cyNode);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Window Processing Error] {ex.Message}");
            }
        }


        private void ProcessProvisionalSpace(FamilyInstance inst, Document doc)
        {
            try
            {
                Logger.LogToFile($"Begin processing {inst.UniqueId}", ProvLog);
                bool isProv = ParameterUtils.IsProvisionalSpace(inst);
                Logger.LogToFile($"Is provisional: {isProv}", ProvLog);
                if (!isProv)
                {
                    Logger.LogToFile("Skipped - not provisional", ProvLog);
                    return;
                }
                var host = inst.Host as Wall;
                if (host == null)
                {
                    BoundingBoxXYZ bb = inst.get_BoundingBox(null);
                    if (bb != null)
                    {
                        Outline outl = new Outline(bb.Min, bb.Max);
                        ElementFilter bbfilter = new BoundingBoxIntersectsFilter(outl);
                        host = new FilteredElementCollector(doc)
                            .OfClass(typeof(Wall))
                            .WherePasses(bbfilter)
                            .Cast<Wall>()
                            .FirstOrDefault();
                    }
                }
                var node = ProvisionalSpaceSerializer.ToProvisionalSpaceNode(inst, out var data);
                Logger.LogToFile($"Serialized data for {inst.UniqueId}", ProvLog);

                var inv = CultureInfo.InvariantCulture;
                var setParts = new List<string>
                {
                    $"p.name = '{ParameterUtils.EscapeForCypher(data["name"].ToString())}'",
                    $"p.width = {((double)data["width"]).ToString(inv)}",
                    $"p.height = {((double)data["height"]).ToString(inv)}",
                    $"p.thickness = {((double)data["thickness"]).ToString(inv)}",
                    $"p.level = '{ParameterUtils.EscapeForCypher(data["level"].ToString())}'",
                    $"p.x = {((double)data["x"]).ToString(inv)}",
                    $"p.y = {((double)data["y"]).ToString(inv)}",
                    $"p.z = {((double)data["z"]).ToString(inv)}",
                    $"p.rotation = {((double)data["rotation"]).ToString(inv)}",
                    $"p.hostId = {data["hostId"]}",
                    $"p.revitId = {data["revitId"]}",
                    $"p.ifcType = '{ParameterUtils.EscapeForCypher(data["ifcType"].ToString())}'",
                    $"p.familyName = '{ParameterUtils.EscapeForCypher(data.GetValueOrDefault("familyName", "").ToString())}'",
                    $"p.symbolName = '{ParameterUtils.EscapeForCypher(data.GetValueOrDefault("symbolName", "").ToString())}'",
                    $"p.category = '{ParameterUtils.EscapeForCypher(data.GetValueOrDefault("category", "").ToString())}'",
                    $"p.phaseCreated = {data.GetValueOrDefault("phaseCreated", -1)}",
                    $"p.phaseDemolished = {data.GetValueOrDefault("phaseDemolished", -1)}",
                    $"p.bbMinX = {((double)data.GetValueOrDefault("bbMinX", 0.0)).ToString(inv)}",
                    $"p.bbMinY = {((double)data.GetValueOrDefault("bbMinY", 0.0)).ToString(inv)}",
                    $"p.bbMinZ = {((double)data.GetValueOrDefault("bbMinZ", 0.0)).ToString(inv)}",
                    $"p.bbMaxX = {((double)data.GetValueOrDefault("bbMaxX", 0.0)).ToString(inv)}",
                    $"p.bbMaxY = {((double)data.GetValueOrDefault("bbMaxY", 0.0)).ToString(inv)}",
                    $"p.bbMaxZ = {((double)data.GetValueOrDefault("bbMaxZ", 0.0)).ToString(inv)}",
                    $"p.uid = '{ParameterUtils.EscapeForCypher(inst.UniqueId)}'",
                    $"p.elementId = {inst.Id.Value}",
                    $"p.typeId = {inst.GetTypeId().Value}",
                    $"p.created = datetime('{((DateTime)data["created"]).ToString("o")}')",
                    $"p.modified = datetime('{((DateTime)data["modified"]).ToString("o")}')",
                    $"p.user = '{ParameterUtils.EscapeForCypher(data["user"].ToString())}'"
                };

                string cyNode =
                    $"MERGE (p:ProvisionalSpace {{guid:'{data["guid"]}'}}) " +
                          $"SET {string.Join(", ", setParts)}";
                _cmdManager.cypherCommands.Enqueue(cyNode);
                Logger.LogToFile($"Cypher node queued: {cyNode}", ProvLog);
                if (host != null)
                {
                    string cyRel =
                        $"MATCH (w:Wall {{ElementId:{host.Id.Value}}}), (p:ProvisionalSpace {{guid:'{data["guid"]}'}}) " +
                        "MERGE (w)-[:HAS_PROV_SPACE]->(p)";
                    _cmdManager.cypherCommands.Enqueue(cyRel);
                    Logger.LogToFile($"Cypher relation queued: {cyRel}", ProvLog);
                    Debug.WriteLine("[Neo4j] Created ProvisionalSpace relation: " + cyRel);
                }

                Debug.WriteLine("[Neo4j] Created ProvisionalSpace node: " + cyNode);
                Logger.LogToFile($"Finished processing {inst.UniqueId}", ProvLog);
                UpdateProvisionalSpaceRelations(inst, doc);

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProvisionalSpace Error] {ex.Message}");
                Logger.LogCrash("ProcessProvisionalSpace", ex);
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
                    $"SET p.elementId = {data["elementId"]}, " +
                    $"p.typeId = {data["typeId"]}, " +
                    $"p.systemTypeId = {data["systemTypeId"]}, " +
                    $"p.levelId = {data["levelId"]}, " +
                    $"p.x1 = {((double)data["x1"]).ToString(inv)}, p.y1 = {((double)data["y1"]).ToString(inv)}, p.z1 = {((double)data["z1"]).ToString(inv)}, " +
                    $"p.x2 = {((double)data["x2"]).ToString(inv)}, p.y2 = {((double)data["y2"]).ToString(inv)}, p.z2 = {((double)data["z2"]).ToString(inv)}, " +
  $"p.diameter = {((double)data["diameter"]).ToString(inv)}, " +
                    $"p.createdBy = coalesce(p.createdBy,'{ParameterUtils.EscapeForCypher(data["user"].ToString())}'), " +
                    $"p.createdAt = coalesce(p.createdAt, datetime('{((DateTime)data["created"]).ToString("o")}')), " +
                    $"p.lastModifiedUtc = datetime('{((DateTime)data["modified"]).ToString("o")}')"; _cmdManager.cypherCommands.Enqueue(cyNode);
                Debug.WriteLine("[Neo4j] Cypher erzeugt (Pipe Node): " + cyNode);

                UpdatePipeRelations(pipe, doc);

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

                string cy = $"MERGE (r:Room {{ElementId: {room.Id.Value}}}) SET r.Name = '{ParameterUtils.EscapeForCypher(roomName)}', r.Level = '{ParameterUtils.EscapeForCypher(levelName)}' WITH r MATCH (l:Level {{ElementId: {room.LevelId.Value}}}) MERGE (l)-[:CONTAINS]->(r)";

                _cmdManager.cypherCommands.Enqueue(cy);
                Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Room Processing Error] {ex.Message}");
            }
        }
        // Liest alle relevanten Elemente aus dem Dokument und erzeugt erste Neo4j-Knoten.
        public void CreateInitialGraph(Document doc)
        {


            // create stopwatch to measure the elapsed time
            Stopwatch timer = new Stopwatch();
            timer.Start();
            Debug.WriteLine("#--------#\nTimer started.\n#--------#");

            string buildingName = "Teststraße 21";
            string buildingNameEsc = ParameterUtils.EscapeForCypher(buildingName);
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
                string levelName = ParameterUtils.EscapeForCypher(lvl.Name);
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
                    string escapedRoomName = ParameterUtils.EscapeForCypher(room.Name);
                    Debug.WriteLine($"Room: {escapedRoomName}, ID: {room.Id}");

                    cy = $"MERGE (r:Room {{ElementId: {room.Id.Value}}}) " +
                         $"SET r.Name = '{ParameterUtils.EscapeForCypher(room.Name)}', r.Level = '{ParameterUtils.EscapeForCypher(levelName)}' " +
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
                                string escapedWallName = ParameterUtils.EscapeForCypher(wallName);

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

                // Iterate over all doors at current level using the detailed
                // serialization method so that all properties are stored in
                // Neo4j. This ensures a door can be fully reconstructed when
                // pulling the model.
                foreach (var door in doors)
                {
                    ProcessDoor(door, doc);
                }
                var windowCollector = new FilteredElementCollector(doc)
                 .OfCategory(BuiltInCategory.OST_Windows).OfClass(typeof(FamilyInstance)).WherePasses(lvlFilter);

                var windows = windowCollector.ToElements();
                foreach (var win in windows)
                {
                    ProcessWindow(win, doc);
                }
                ProcessPipes(doc, lvl);
            }
            ProcessProvisionalSpaces(doc);
            CheckBoundingForAllPipes(doc);
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
        private void ProcessProvisionalSpaces(Document doc)
        {
            var collector = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_GenericModel)
                                .OfClass(typeof(FamilyInstance));


            foreach (FamilyInstance inst in collector)
            {
                ProcessProvisionalSpace(inst, doc);
            }
        }

        private void ProcessPipes(Document doc, Level level)
        {
            var levelFilter = new ElementLevelFilter(level.Id);
            var catFilter = new LogicalOrFilter(new List<ElementFilter>
            {
                new ElementCategoryFilter(BuiltInCategory.OST_PipeCurves),
                new ElementCategoryFilter(BuiltInCategory.OST_PipeSegments)
            });
            var collector = new FilteredElementCollector(doc)
                .WherePasses(levelFilter)
                .WherePasses(catFilter)
                .OfClass(typeof(MEPCurve));

            foreach (MEPCurve pipe in collector.Cast<MEPCurve>())
            {
                ProcessPipe(pipe, doc);
            }
        }
        private static bool Intersects(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            return a.Min.X <= b.Max.X && a.Max.X >= b.Min.X &&
                             a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y &&
                             a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;
        }

        private void UpdatePipeRelations(MEPCurve pipe, Document doc)
        {
            var bbPipe = pipe.get_BoundingBox(null);
            if (bbPipe == null) return;

            var psCollector = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_GenericModel)
                .OfClass(typeof(FamilyInstance));

            foreach (FamilyInstance ps in psCollector.Cast<FamilyInstance>())
            {
                if (!ParameterUtils.IsProvisionalSpace(ps))
                    continue;

                var bbPs = ps.get_BoundingBox(null);
                if (bbPs == null) continue;
                bool intersects = Intersects(bbPipe, bbPs);
                string cypher;

                if (intersects)
                {
                    cypher =
                        $"MATCH (pi:Pipe {{uid:'{pipe.UniqueId}'}}), (ps:ProvisionalSpace {{guid:'{ps.UniqueId}'}}) MERGE (pi)-[:CONTAINED_IN]->(ps)";
                }
                else
                {
                    cypher =
                        $"MATCH (pi:Pipe {{uid:'{pipe.UniqueId}'}})-[r:CONTAINED_IN]->(ps:ProvisionalSpace {{guid:'{ps.UniqueId}'}}) DELETE r";
                }
                _cmdManager.cypherCommands.Enqueue(cypher);
                Debug.WriteLine("[Neo4j] Updated Pipe-ProvisionalSpace relation: " + cypher);
            }
        }

        private void UpdateProvisionalSpaceRelations(FamilyInstance ps, Document doc)
        {
            if (!ParameterUtils.IsProvisionalSpace(ps))
                return;

            var bbPs = ps.get_BoundingBox(null);
            if (bbPs == null) return;

            var catFilter = new LogicalOrFilter(new List<ElementFilter>
            {
                new ElementCategoryFilter(BuiltInCategory.OST_PipeCurves),
                new ElementCategoryFilter(BuiltInCategory.OST_PipeSegments)
            });

            var pipes = new FilteredElementCollector(doc)
                .WherePasses(catFilter)
                .OfClass(typeof(MEPCurve));

            foreach (MEPCurve pipe in pipes.Cast<MEPCurve>())
            {
                var bbPipe = pipe.get_BoundingBox(null);
                if (bbPipe == null) continue;

                bool intersects = Intersects(bbPipe, bbPs);
                string cypher;
                if (intersects)
                {
                    cypher =
                        $"MATCH (pi:Pipe {{uid:'{pipe.UniqueId}'}}), (ps:ProvisionalSpace {{guid:'{ps.UniqueId}'}}) MERGE (pi)-[:CONTAINED_IN]->(ps)";
                }
                else
                {
                    cypher =
                        $"MATCH (pi:Pipe {{uid:'{pipe.UniqueId}'}})-[r:CONTAINED_IN]->(ps:ProvisionalSpace {{guid:'{ps.UniqueId}'}}) DELETE r";
                }
                _cmdManager.cypherCommands.Enqueue(cypher);
                Debug.WriteLine("[Neo4j] Updated Pipe-ProvisionalSpace relation: " + cypher);
            }

        }

        public void CheckBoundingForAllPipes(Document doc)
        {
            var catFilter = new LogicalOrFilter(new List<ElementFilter>
            {
                new ElementCategoryFilter(BuiltInCategory.OST_PipeCurves),
                new ElementCategoryFilter(BuiltInCategory.OST_PipeSegments)
            });

            var collector = new FilteredElementCollector(doc)
                .WherePasses(catFilter)
                .OfClass(typeof(MEPCurve));

            foreach (MEPCurve pipe in collector.Cast<MEPCurve>())
            {
                UpdatePipeRelations(pipe, doc);
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
  $"SET s.Name = '{ParameterUtils.EscapeForCypher(stairName)}' " +
                $"WITH s " +
                $"MATCH (b:Level {{ElementId: {baseLevelId.Value}}}), (t:Level {{ElementId: {topLevelId.Value}}}) " +
                $"MERGE (b)-[:CONNECTS_TO]->(s) " +
                $"MERGE (s)-[:CONNECTS_TO]->(t)";

            _cmdManager.cypherCommands.Enqueue(cy);
            Debug.WriteLine("[Neo4j] Cypher erzeugt (Stair-Verbindungen): " + cy);
        }

        // Exportiert nur die angegebenen Elemente als temporäre IFC-Datei.
        public string ExportIfcSubset(Document doc, List<ElementId> elementsToExport)
        {
            if (doc.IsReadOnly)
            {
                Autodesk.Revit.UI.TaskDialog.Show("IFC Export", "Dokument ist schreibgesch\u00fctzt. Export nicht m\u00f6glich.");
                return string.Empty;
            }
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
            ifcExportOptions.AddOption("UseElementIdAsIfcGUID", "1");

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


        /// <summary>
        /// Reads the exported IFC file and maps IFC GlobalIds to the provided
        /// Revit ElementIds. This is a best-effort text based mapping that
        /// assumes the order of occurrences matches the element list.
        /// </summary>
        public Dictionary<string, ElementId> MapIfcGuidsToRevitIds(string ifcFilePath, List<ElementId> elementIds)
        {
            var map = new Dictionary<string, ElementId>();
            if (string.IsNullOrEmpty(ifcFilePath) || !File.Exists(ifcFilePath))
                return map;

            var guidRegex = new Regex(@"GLOBALID\('(?<g>[^']+)'", RegexOptions.IgnoreCase);
            var guids = new List<string>();
            foreach (var line in File.ReadLines(ifcFilePath))
            {
                var m = guidRegex.Match(line);
                if (m.Success)
                {
                    string guid = m.Groups["g"].Value.Trim();
                    if (!string.IsNullOrEmpty(guid))
                        guids.Add(guid);
                }
            }

            for (int i = 0; i < elementIds.Count && i < guids.Count; i++)
            {
                map[guids[i]] = elementIds[i];
            }
            return map;
        }
        // Ältere Methode zur Graphaktualisierung, wird für Debugzwecke verwendet.
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
                Element e = doc.GetElement(id);
                string cyDel;
                if (e != null && e.Category?.Id.Value == (int)BuiltInCategory.OST_Doors)
                {
                    cyDel = $"MATCH (d:Door {{ElementId: {intId}}}) DETACH DELETE d";
                }
                else
                {
                    cyDel = $"MATCH (n {{ElementId: {intId}}}) DETACH DELETE n";
                }
                _cmdManager.cypherCommands.Enqueue(cyDel);
                Debug.WriteLine("[Neo4j] Node deletion Cypher: " + cyDel);


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
                    // Tür-Eigenschaften und Host aktualisieren
                    var sym = doc.GetElement(fi.GetTypeId()) as FamilySymbol;
                    string doorType = sym?.Name ?? "Unbekannter Typ";
                    string doorNameMod = fi.get_Parameter(BuiltInParameter.DOOR_NUMBER)?.AsString() ?? fi.Name;
                    var hostWall = fi.Host as Wall;
                    string cyDoor =
                        $"MATCH (d:Door {{ElementId: {intId}}}) " +
                        "OPTIONAL MATCH (d)-[r:INSTALLED_IN]->() DELETE r " +
                        "WITH d " +
                        $"MATCH (l:Level {{ElementId: {fi.LevelId.Value}}}) ";
                    if (hostWall != null)
                        cyDoor += $"MATCH (w:Wall {{ElementId: {hostWall.Id.Value}}}) ";
                    cyDoor +=
                        $"SET d.Name = '{ParameterUtils.EscapeForCypher(doorNameMod)}', " +
                        $"d.Type = '{ParameterUtils.EscapeForCypher(doorType)}', " +
                        $"d.hostId = {(hostWall != null ? hostWall.Id.Value : -1)} " +
                        "MERGE (l)-[:CONTAINS]->(d) ";
                    if (hostWall != null)
                        cyDoor += "MERGE (d)-[:INSTALLED_IN]->(w)";
                    cy = cyDoor;
                }
                else if (e is Room)
                {
                    // Raum-Name aktualisieren
                    cy = $"MATCH (r:Room {{ElementId: {intId}}}) " +
 $"SET r.Name = '{ParameterUtils.EscapeForCypher(e.Name)}'";
                }
                else if (e is Wall)
                {
                    // Wand-Name aktualisieren
                    cy = $"MATCH (w:Wall {{ElementId: {intId}}}) " +
 $"SET w.Name = '{ParameterUtils.EscapeForCypher(e.Name)}'";
                }
                else if (e is Level)
                {
                    cy = $"MATCH (l:Level {{ElementId: {intId}}}) " +
 $"SET l.Name = '{ParameterUtils.EscapeForCypher(e.Name)}'";
                }
                else if (e is FamilyInstance win && win.Category.Id.Value == (int)BuiltInCategory.OST_Windows)
                {
                    cy = $"MATCH (wi:Window {{ElementId: {intId}}}) " +
                         $"SET wi.Name = '{ParameterUtils.EscapeForCypher(e.Name)}'";
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
                                string escapedWallName = ParameterUtils.EscapeForCypher(wall.Name);
                                cy = "MATCH (r:Room{ElementId: " + room.Id + "})" +
        " MATCH (w:Wall{ElementId: " + wall.Id + "})" +
        " MATCH (l:Level{ElementId: " + wall.LevelId.Value + "})" +
        " MERGE (l)-[:CONTAINS]->(w)-[:BOUNDS]->(r)";
                                _cmdManager.cypherCommands.Enqueue(cy);
                                Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);
                                Debug.WriteLine($"Modified Room with ID: {id} and Name: {e.Name}");
                            }
                        }
                        if (e is FamilyInstance wfi && wfi.Category.Id.Value == (int)BuiltInCategory.OST_Windows)
                        {
                            Element host = wfi.Host;
                            cy = $"MATCH (wi:Window{{ElementId: {intId}}}), (l:Level{{ElementId: {wfi.LevelId.Value}}}) MERGE (l)-[:CONTAINS]->(wi)";
                            if (host is Wall hw)
                                cy += $" WITH wi MATCH (w:Wall{{ElementId: {hw.Id.Value}}}) MERGE (wi)-[:INSTALLED_IN]->(w)";
                            _cmdManager.cypherCommands.Enqueue(cy);
                            Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);
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
                    case FamilyInstance wi when wi.Category.Id.Value == (int)BuiltInCategory.OST_Windows:
                        ProcessWindow(wi, doc);
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
                    Debug.WriteLine($"Wall: {wall.Name}, ID: {wall.Id}");
                    if (wall.LevelId == ElementId.InvalidElementId)
                    {
                        Debug.WriteLine($"[WARN] Wall {wall.Id} has invalid LevelId.");
                    }

                    // Create or update wall node with all properties
                    ProcessWall(wall, doc);
                    // Link wall to adjacent rooms
                    IList<Element> rooms = getRoomFromWall(doc, wall);
                    foreach (var roomElement in rooms)
                    {
                        if (roomElement is Room r)
                        {
                            string cyRel =
 $"MATCH (w:Wall {{ElementId: {wall.Id.Value}}}), (r:Room {{ElementId: {r.Id.Value}}}) MERGE (w)-[:BOUNDS]->(r)"; _cmdManager.cypherCommands.Enqueue(cyRel);
                            Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cyRel);
                        }
                    }
                }
                if (e is FamilyInstance wfi && wfi.Category.Id.Value == (int)BuiltInCategory.OST_Windows)
                {
                    string escapedName = ParameterUtils.EscapeForCypher(wfi.Name);
                    Element host = wfi.Host;
                    cy = $"MATCH (l:Level{{ElementId:{wfi.LevelId.Value}}})";
                    if (host is Wall hw)
                        cy += $", (w:Wall {{ElementId:{hw.Id.Value}}})";
                    cy += $" MERGE (wi:Window {{ElementId:{wfi.Id.Value}, Name:'{escapedName}'}})";
                    cy += " MERGE (l)-[:CONTAINS]->(wi)";
                    if (host is Wall)
                        cy += " MERGE (wi)-[:INSTALLED_IN]->(w)";
                    _cmdManager.cypherCommands.Enqueue(cy);
                    Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);
                }


            }




        }
        // Hilfsfunktion: findet Räume, die eine Wand schneiden.
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