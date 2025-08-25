using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Plumbing;
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
                  $"MATCH (l:Level {{elementId: {wall.LevelId.Value}}}) MERGE (w:Wall {{elementId: {wall.Id.Value}}}) SET {string.Join(", ", setParts)} MERGE (l)-[:CONTAINS]->(w)";


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

                string cyBase = $"MATCH (l:Level {{elementId: {door.LevelId.Value}}})";
                if (hostWall != null)
                    cyBase += $", (w:Wall {{elementId: {hostWall.Id.Value}}})";
                string cyNode =
                    $"{cyBase} MERGE (d:Door {{elementId: {door.Id.Value}}}) SET {string.Join(", ", setParts)}";
                if (hostWall != null)
                    cyNode += " MERGE (l)-[:CONTAINS]->(d) MERGE (d)-[:INSTALLED_IN]->(w)";
                else
                    cyNode += " MERGE (l)-[:CONTAINS]->(d)";

                _cmdManager.cypherCommands.Enqueue(cyNode);
                Debug.WriteLine("[Neo4j] Created Door node: " + cyNode);

                // Set SpaceTracker tag for local Door (matches pull logic)
                try
                {
                    var tag = $"SpaceTracker:ElementId={door.Id.Value}";
                    var commentParam = door.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (commentParam != null && !commentParam.IsReadOnly)
                    {
                        commentParam.Set(tag);
                        Logger.LogToFile($"PROCESSDOOR: Set tag '{tag}' on Door {door.Id}", "sync.log");
                    }
                    else
                    {
                        Logger.LogToFile($"PROCESSDOOR: WARNING - Could not set tag on Door {door.Id} (parameter null or read-only)", "sync.log");
                    }
                }
                catch (Exception tagEx)
                {
                    Logger.LogToFile($"PROCESSDOOR: Error setting tag on Door {door.Id}: {tagEx.Message}", "sync.log");
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
                Logger.LogToFile($"PROCESSPROVISIONALSPACE: Begin processing ProvisionalSpace {inst.Id} (UniqueId: {inst.UniqueId})", "sync.log");
                Logger.LogToFile($"Begin processing {inst.UniqueId}", ProvLog);
                bool isProv = ParameterUtils.IsProvisionalSpace(inst);
                Logger.LogToFile($"PROCESSPROVISIONALSPACE: IsProvisionalSpace check result: {isProv} for element {inst.Id}", "sync.log");
                Logger.LogToFile($"Is provisional: {isProv}", ProvLog);
                if (!isProv)
                {
                    Logger.LogToFile($"PROCESSPROVISIONALSPACE: Skipped element {inst.Id} - not identified as provisional space", "sync.log");
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
                
                // DEBUG: Log elementId to identify -1 issues
                var elementId = data["elementId"];
                Logger.LogToFile($"PROVISIONAL SPACE ELEMENT ID: {elementId} for instance {inst.UniqueId} (Revit ID: {inst.Id.Value})", "sync.log");
                if (elementId.Equals(-1) || elementId.ToString() == "-1")
                {
                    Logger.LogToFile($"WARNING: ProvisionalSpace has elementId = -1! Instance: {inst.UniqueId}, Revit ID: {inst.Id.Value}", "sync.log");
                }

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
                    $"p.elementId = {data["elementId"]}",  // FIXED: Changed from revitId to elementId for consistency
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
                        $"MATCH (w:Wall {{elementId:{host.Id.Value}}}), (p:ProvisionalSpace {{guid:'{data["guid"]}'}}) " +
                        "MERGE (w)-[:HAS_PROV_SPACE]->(p)";
                    _cmdManager.cypherCommands.Enqueue(cyRel);
                    Logger.LogToFile($"Cypher relation queued: {cyRel}", ProvLog);
                    Debug.WriteLine("[Neo4j] Created ProvisionalSpace relation: " + cyRel);
                }

                Debug.WriteLine("[Neo4j] Created ProvisionalSpace node: " + cyNode);
                Logger.LogToFile($"Finished processing {inst.UniqueId}", ProvLog);
                
                // Set SpaceTracker tag for local ProvisionalSpace (matches pull logic)
                try
                {
                    var tag = $"SpaceTracker:ElementId={inst.Id.Value}";
                    var commentParam = inst.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (commentParam != null && !commentParam.IsReadOnly)
                    {
                        commentParam.Set(tag);
                        Logger.LogToFile($"PROCESSPROVISIONALSPACE: Set tag '{tag}' on ProvisionalSpace {inst.Id}", "sync.log");
                    }
                    else
                    {
                        Logger.LogToFile($"PROCESSPROVISIONALSPACE: WARNING - Could not set tag on ProvisionalSpace {inst.Id} (parameter null or read-only)", "sync.log");
                    }
                }
                catch (Exception tagEx)
                {
                    Logger.LogToFile($"PROCESSPROVISIONALSPACE: Error setting tag on ProvisionalSpace {inst.Id}: {tagEx.Message}", "sync.log");
                }
                
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

                // Set SpaceTracker tag for local Pipe (matches pull logic)
                try
                {
                    var tag = $"SpaceTracker:ElementId={pipe.Id.Value}";
                    var commentParam = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (commentParam != null && !commentParam.IsReadOnly)
                    {
                        commentParam.Set(tag);
                        Logger.LogToFile($"PROCESSPIPE: Set tag '{tag}' on Pipe {pipe.Id}", "sync.log");
                    }
                    else
                    {
                        Logger.LogToFile($"PROCESSPIPE: WARNING - Could not set tag on Pipe {pipe.Id} (parameter null or read-only)", "sync.log");
                    }
                }
                catch (Exception tagEx)
                {
                    Logger.LogToFile($"PROCESSPIPE: Error setting tag on Pipe {pipe.Id}: {tagEx.Message}", "sync.log");
                }

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

                string cy = $"MERGE (r:Room {{elementId: {room.Id.Value}}}) SET r.Name = '{ParameterUtils.EscapeForCypher(roomName)}', r.Level = '{ParameterUtils.EscapeForCypher(levelName)}' WITH r MATCH (l:Level {{elementId: {room.LevelId.Value}}}) MERGE (l)-[:CONTAINS]->(r)";

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
            Logger.LogToFile("CREATE INITIAL GRAPH TRACE 1: Starting CreateInitialGraph method", "sync.log");

            // create stopwatch to measure the elapsed time
            Stopwatch timer = new Stopwatch();
            timer.Start();
            Debug.WriteLine("#--------#\nTimer started.\n#--------#");
            Logger.LogToFile("CREATE INITIAL GRAPH TRACE 2: Timer started", "sync.log");

            Logger.LogToFile("CREATE INITIAL GRAPH TRACE 3: Creating building node", "sync.log");
            string buildingName = "Teststraße 21";
            string buildingNameEsc = ParameterUtils.EscapeForCypher(buildingName);
            string cyBuilding = $"MERGE (b:Building {{Name: \"{buildingNameEsc}\", elementId: 1}})";
            _cmdManager.cypherCommands.Enqueue(cyBuilding);
            Debug.WriteLine("[Neo4j] Cypher erzeugt (Building): " + cyBuilding);
            Logger.LogToFile("CREATE INITIAL GRAPH TRACE 4: Building node queued", "sync.log");
            
            // Note: ChangeLog for Building will be created later with all other elements

            // 1. Alle Level einlesen
            Logger.LogToFile("CREATE INITIAL GRAPH TRACE 5: Starting level collection", "sync.log");

            // Get all level
            FilteredElementCollector collector = new FilteredElementCollector(doc);
            IList<Level> levels = collector.OfClass(typeof(Level)).Cast<Level>().ToList();
            Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 6: Found {levels.Count} levels", "sync.log");

            // Iterate over all level
            int levelIndex = 0;
            foreach (Level lvl in levels)
            {
                levelIndex++;
                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 7-{levelIndex}: Processing level '{lvl.Name}' (ID: {lvl.Id})", "sync.log");
                
                Debug.WriteLine($"Level: {lvl.Name}, ID: {lvl.Id}");
                string levelName = ParameterUtils.EscapeForCypher(lvl.Name);
                string cy = $"MERGE (l:Level{{Name: \"{levelName}\", elementId: {lvl.Id}}})";
                _cmdManager.cypherCommands.Enqueue(cy);
                Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);
                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 8-{levelIndex}: Level node queued", "sync.log");

                string cyRel =
            $"MATCH (b:Building {{Name: \"{buildingNameEsc}\", elementId: 1}}), " +
            $"      (l:Level    {{elementId: {lvl.Id}}}) " +
            $"MERGE (b)-[:CONTAINS]->(l)";
                _cmdManager.cypherCommands.Enqueue(cyRel);
                Debug.WriteLine("[Neo4j] Cypher erzeugt (Building contains Level): " + cyRel);
                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 9-{levelIndex}: Level relationship queued", "sync.log");

                // get all Elements of type Room in the current level
                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 10-{levelIndex}: Getting rooms for level {lvl.Name}", "sync.log");
                ElementLevelFilter lvlFilter = new ElementLevelFilter(lvl.Id);

                IList<Element> rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .WherePasses(lvlFilter)
                    .ToElements();
                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 11-{levelIndex}: Found {rooms.Count} rooms on level {lvl.Name}", "sync.log");
                
                // Iterate over all rooms in that level
                int roomIndex = 0;
                foreach (var element in rooms)
                {
                    roomIndex++;
                    Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 12-{levelIndex}-{roomIndex}: Processing room {element.Id}", "sync.log");
                    
                    var room = (Room)element;

                    if (room.LevelId == null || room.LevelId.Value == -1)
                    {
                        Debug.WriteLine($"[WARN] Raum {room.Id} hat kein gültiges Level – wird übersprungen.");
                        Logger.LogToFile($"CREATE INITIAL GRAPH TRACE WARNING: Room {room.Id} has invalid level, skipping", "sync.log");
                        continue;
                    }
                    string escapedRoomName = ParameterUtils.EscapeForCypher(room.Name);
                    Debug.WriteLine($"Room: {escapedRoomName}, ID: {room.Id}");

                    cy = $"MERGE (r:Room {{elementId: {room.Id.Value}}}) " +
                         $"SET r.Name = '{ParameterUtils.EscapeForCypher(room.Name)}', r.Level = '{ParameterUtils.EscapeForCypher(levelName)}' " +
                         $"WITH r MATCH (l:Level {{elementId: {room.LevelId.Value}}}) " +
                         $"MERGE (l)-[:CONTAINS]->(r)";

                    _cmdManager.cypherCommands.Enqueue(cy);
                    Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);
                    Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 13-{levelIndex}-{roomIndex}: Room node queued", "sync.log");

                    Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 14-{levelIndex}-{roomIndex}: Getting boundary segments for room {room.Id}", "sync.log");
                    IList<IList<BoundarySegment>> boundaries = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                    Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 15-{levelIndex}-{roomIndex}: Got {boundaries.Count} boundary groups", "sync.log");

                    int boundaryIndex = 0;
                    foreach (IList<BoundarySegment> b in boundaries)
                    {
                        boundaryIndex++;
                        Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 16-{levelIndex}-{roomIndex}-{boundaryIndex}: Processing boundary group with {b.Count} segments", "sync.log");
                        
                        int segmentIndex = 0;
                        foreach (BoundarySegment s in b)
                        {
                            segmentIndex++;
                            Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 17-{levelIndex}-{roomIndex}-{boundaryIndex}-{segmentIndex}: Processing boundary segment", "sync.log");
                            
                            ElementId neighborId = s.ElementId;
                            if (neighborId.Value == -1)
                            {
                                Debug.WriteLine("Something went wrong when extracting Element ID " + neighborId);
                                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE WARNING: Invalid neighbor element ID -1", "sync.log");
                                continue;
                            }

                            Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 18-{levelIndex}-{roomIndex}-{boundaryIndex}-{segmentIndex}: Getting neighbor element {neighborId.Value}", "sync.log");
                            Element neighbor = doc.GetElement(neighborId);

                            if (neighbor is Wall wall)
                            {
                                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 19-{levelIndex}-{roomIndex}-{boundaryIndex}-{segmentIndex}: Processing wall neighbor {wall.Id}", "sync.log");
                                
                                string wallName = wall.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString()
                                                  ?? wall.Name ?? "Unbenannt";
                                string escapedWallName = ParameterUtils.EscapeForCypher(wallName);

                                Debug.WriteLine($"\tNeighbor Type: Wall - ID: {wall.Id}, Name: {escapedWallName}");

                                cy = "MATCH (r:Room{elementId:" + room.Id + "}) " +
     "MATCH (l:Level{elementId:" + neighbor.LevelId + "}) " +
     "MERGE (w:Wall{elementId:" + wall.Id + "}) " +
     "SET w.Name = \"" + escapedWallName + "\" " +
     "MERGE (l)-[:CONTAINS]->(w)-[:BOUNDS]->(r)";
                                _cmdManager.cypherCommands.Enqueue(cy);
                                Debug.WriteLine("[Neo4j] Cypher erzeugt: " + cy);
                                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 20-{levelIndex}-{roomIndex}-{boundaryIndex}-{segmentIndex}: Wall relationship queued", "sync.log");

                            }
                            else
                            {
                                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 21-{levelIndex}-{roomIndex}-{boundaryIndex}-{segmentIndex}: Skipping undefined neighbor type", "sync.log");
                                Debug.WriteLine("\tNeighbor Type: Undefined - ID: " + neighbor.Id);
                            }
                        }
                    }
                }
                
                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 22-{levelIndex}: Starting ProcessWalls for level {lvl.Name}", "sync.log");
                ProcessWalls(doc, lvl);
                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 23-{levelIndex}: ProcessWalls completed for level {lvl.Name}", "sync.log");
                
                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 24-{levelIndex}: Starting stair processing for level {lvl.Name}", "sync.log");
                var stairFilter = new ElementLevelFilter(lvl.Id);
                var stairs = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Stairs)
                    .WherePasses(stairFilter)
                    .WhereElementIsNotElementType()
                    .ToElements();
                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 25-{levelIndex}: Found {stairs.Count} stairs on level {lvl.Name}", "sync.log");
                
                int stairIndex = 0;
                foreach (Element e in stairs)
                {
                    stairIndex++;
                    Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 26-{levelIndex}-{stairIndex}: Processing stair {e.Id}", "sync.log");
                    ProcessStair(e, doc);
                    Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 27-{levelIndex}-{stairIndex}: Stair {e.Id} processed", "sync.log");
                }

                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 28-{levelIndex}: Starting door processing for level {lvl.Name}", "sync.log");
                var doorCollector = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors).OfClass(typeof(FamilyInstance)).WherePasses(lvlFilter);

                var doors = doorCollector.ToElements();
                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 29-{levelIndex}: Found {doors.Count} doors on level {lvl.Name}", "sync.log");

                // Iterate over all doors at current level using the detailed
                // serialization method so that all properties are stored in
                // Neo4j. This ensures a door can be fully reconstructed when
                // pulling the model.
                int doorIndex = 0;
                foreach (var door in doors)
                {
                    doorIndex++;
                    Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 30-{levelIndex}-{doorIndex}: Processing door {door.Id}", "sync.log");
                    ProcessDoor(door, doc);
                    Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 31-{levelIndex}-{doorIndex}: Door {door.Id} processed", "sync.log");
                }
             
                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 32-{levelIndex}: Starting pipe processing for level {lvl.Name}", "sync.log");
                ProcessPipes(doc, lvl);
                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 33-{levelIndex}: Pipe processing completed for level {lvl.Name}", "sync.log");
            }
            
            Logger.LogToFile("CREATE INITIAL GRAPH TRACE 34: Level iteration completed, starting provisional spaces", "sync.log");
            ProcessProvisionalSpaces(doc);
            Logger.LogToFile("CREATE INITIAL GRAPH TRACE 35: Provisional spaces processed", "sync.log");
            
            Logger.LogToFile("CREATE INITIAL GRAPH TRACE 36: Starting pipe bounding check", "sync.log");
            CheckBoundingForAllPipes(doc);
            Logger.LogToFile("CREATE INITIAL GRAPH TRACE 37: Pipe bounding check completed", "sync.log");
            
            Logger.LogToFile("CREATE INITIAL GRAPH TRACE 38: Starting global stair processing", "sync.log");
            var globalStairs = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Stairs)
                .WhereElementIsNotElementType()
                .ToElements();
            Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 39: Found {globalStairs.Count} global stairs", "sync.log");
            
            int globalStairIndex = 0;
            foreach (Element stair in globalStairs)
            {
                globalStairIndex++;
                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 40-{globalStairIndex}: Processing global stair {stair.Id}", "sync.log");
                ProcessStair(stair, doc);
                Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 41-{globalStairIndex}: Global stair {stair.Id} processed", "sync.log");
            }

            Logger.LogToFile("CREATE INITIAL GRAPH TRACE 42: Starting file output", "sync.log");
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceTracker");
            Directory.CreateDirectory(baseDir); // falls noch nicht vorhanden

            var cyPath = Path.Combine(baseDir, "neo4j_cypher.txt");
            Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 43: Writing cypher commands to {cyPath}", "sync.log");
            File.WriteAllText(cyPath, string.Join("\n", _cmdManager.cypherCommands));
            Logger.LogToFile("CREATE INITIAL GRAPH TRACE 44: Cypher file written successfully", "sync.log");

            // print out the elapsed time and stop the timer
            Debug.WriteLine($"#--------#\nTimer stopped: {timer.ElapsedMilliseconds}ms\n#--------#");
            timer.Stop();
            Logger.LogToFile($"CREATE INITIAL GRAPH TRACE 45: Timer stopped, total time: {timer.ElapsedMilliseconds}ms", "sync.log");
            
            // DISABLED: Do not create initial ChangeLog entries - they cause sync conflicts
            // Initial elements are handled by the first session's real changes
            Logger.LogToFile("CREATE INITIAL GRAPH TRACE 47: Skipping initial ChangeLog creation to prevent ElementId conflicts", "sync.log");
            // CreateInitialChangeLogEntries(doc); // DISABLED
            Logger.LogToFile("CREATE INITIAL GRAPH TRACE 48: Initial ChangeLog creation skipped", "sync.log");
            
            Logger.LogToFile("CREATE INITIAL GRAPH TRACE 46: CreateInitialGraph method completed successfully", "sync.log");
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

            // Update Pipe-ProvisionalSpace relationships
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

            // NEW: Update Pipe-Wall relationships (for pipe intersection detection)
            var wallCollector = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .OfClass(typeof(Wall));

            foreach (Wall wall in wallCollector.Cast<Wall>())
            {
                var bbWall = wall.get_BoundingBox(null);
                if (bbWall == null) continue;
                
                bool intersects = Intersects(bbPipe, bbWall);
                string cypher;

                if (intersects)
                {
                    cypher =
                        $"MATCH (pi:Pipe {{uid:'{pipe.UniqueId}'}}), (w:Wall {{elementId:{wall.Id.Value}}}) MERGE (pi)-[:INTERSECTS]->(w)";
                }
                else
                {
                    cypher =
                        $"MATCH (pi:Pipe {{uid:'{pipe.UniqueId}'}})-[r:INTERSECTS]->(w:Wall {{elementId:{wall.Id.Value}}}) DELETE r";
                }
                _cmdManager.cypherCommands.Enqueue(cypher);
                Debug.WriteLine("[Neo4j] Updated Pipe-Wall relation: " + cypher);
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
                $"MERGE (s:Stair {{elementId: {stairElem.Id.Value}}}) " +
  $"SET s.Name = '{ParameterUtils.EscapeForCypher(stairName)}' " +
                $"WITH s " +
                $"MATCH (b:Level {{elementId: {baseLevelId.Value}}}), (t:Level {{elementId: {topLevelId.Value}}}) " +
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
                Logger.LogToFile($"UPDATEGRPH DELETE: Processing deleted element {id}", "sync.log");
                
                int intId = (int)id.Value;
                Element e = doc.GetElement(id);
                string cyDel;
                if (e != null && e.Category?.Id.Value == (int)BuiltInCategory.OST_Doors)
                {
                    cyDel = $"MATCH (d:Door {{elementId: {intId}}}) DETACH DELETE d";
                }
                else if (e != null && e.Category?.Id.Value == (int)BuiltInCategory.OST_PipeCurves)
                {
                    cyDel = $"MATCH (p:Pipe {{elementId: {intId}}}) DETACH DELETE p";
                }
                else if (e != null && e.Category?.Id.Value == (int)BuiltInCategory.OST_GenericModel && e is FamilyInstance fi && ParameterUtils.IsProvisionalSpace(fi))
                {
                    cyDel = $"MATCH (ps:ProvisionalSpace {{elementId: {intId}}}) DETACH DELETE ps";
                }
                else
                {
                    cyDel = $"MATCH (n {{elementId: {intId}}}) DETACH DELETE n";
                }
                _cmdManager.cypherCommands.Enqueue(cyDel);
                Debug.WriteLine("[Neo4j] Node deletion Cypher: " + cyDel);

                // Create ChangeLog for deletion (always create, even if element is null)
                Logger.LogToFile($"UPDATEGRPH: Creating ChangeLog for deleted element {id}", "sync.log");
                CreateChangeLogForElement(id.Value, "Delete");

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

                Logger.LogToFile($"UPDATEGRPH MODIFY: Processing modified element {e.Id} of category {e.Category?.Name} ({e.Category?.Id.Value})", "sync.log");

                // change properties
                int intId = (int)e.Id.Value;
                if (e is FamilyInstance fi && fi.Category.Id.Value == (int)BuiltInCategory.OST_Doors)
                {
                    // CRITICAL FIX: Do not update doors during pull operations to prevent feedback loop
                    if (CommandManager.Instance.IsPullInProgress)
                    {
                        Logger.LogToFile($"UPDATEGRPH: Skipping Door update during pull - {fi.Id}", "sync.log");
                        continue; // Skip door updates during pull to prevent overwriting correct Neo4j data
                    }
                    
                    // Tür-Eigenschaften und Host aktualisieren
                    var sym = doc.GetElement(fi.GetTypeId()) as FamilySymbol;
                    string doorType = sym?.Name ?? "Unbekannter Typ";
                    string doorNameMod = fi.get_Parameter(BuiltInParameter.DOOR_NUMBER)?.AsString() ?? fi.Name;
                    var hostWall = fi.Host as Wall;
                    string cyDoor =
                        $"MATCH (d:Door {{elementId: {intId}}}) " +
                        "OPTIONAL MATCH (d)-[r:INSTALLED_IN]->() DELETE r " +
                        "WITH d " +
                        $"MATCH (l:Level {{elementId: {fi.LevelId.Value}}}) ";
                    if (hostWall != null)
                        cyDoor += $"MATCH (w:Wall {{elementId: {hostWall.Id.Value}}}) ";
                    cyDoor +=
                        $"SET d.Name = '{ParameterUtils.EscapeForCypher(doorNameMod)}', " +
                        $"d.Type = '{ParameterUtils.EscapeForCypher(doorType)}', " +
                        $"d.hostId = {(hostWall != null ? hostWall.Id.Value : -1)} " +
                        "MERGE (l)-[:CONTAINS]->(d) ";
                    if (hostWall != null)
                        cyDoor += "MERGE (d)-[:INSTALLED_IN]->(w)";
                    cy = cyDoor;
                    
                    // Create ChangeLog for Door modification
                    Logger.LogToFile($"UPDATEGRPH: Creating ChangeLog for modified Door {fi.Id}", "sync.log");
                    CreateChangeLogForElement(fi.Id.Value, "Modify");
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
                    
                    // Create ChangeLog for Wall modification
                    Logger.LogToFile($"UPDATEGRPH: Creating ChangeLog for modified Wall {e.Id}", "sync.log");
                    CreateChangeLogForElement(e.Id.Value, "Modify");
                }
                else if (e is FamilyInstance psFi && psFi.Category.Id.Value == (int)BuiltInCategory.OST_GenericModel && ParameterUtils.IsProvisionalSpace(psFi))
                {
                    // CRITICAL FIX: Do not update coordinates during pull operations to prevent feedback loop
                    if (CommandManager.Instance.IsPullInProgress)
                    {
                        Logger.LogToFile($"UPDATEGRPH: Skipping ProvisionalSpace coordinate update during pull - {psFi.Id}", "sync.log");
                        continue; // Skip coordinate updates during pull to prevent overwriting correct Neo4j data
                    }
                    
                    // ProvisionalSpace-Eigenschaften aktualisieren
                    var data = ProvisionalSpaceSerializer.ToProvisionalSpaceNode(psFi, out var dictData);
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    
                    Logger.LogToFile($"UPDATEGRPH: Serializing ProvisionalSpace {psFi.Id} coordinates: x={((double)dictData["x"]):F6}, y={((double)dictData["y"]):F6}, z={((double)dictData["z"]):F6} (meters)", "sync.log");
                    
                    cy = $"MATCH (ps:ProvisionalSpace {{ElementId: {intId}}}) " +
                         $"SET ps.name = '{ParameterUtils.EscapeForCypher(dictData["name"].ToString())}', " +
                         $"ps.x = {((double)dictData["x"]).ToString(inv)}, " +
                         $"ps.y = {((double)dictData["y"]).ToString(inv)}, " +
                         $"ps.z = {((double)dictData["z"]).ToString(inv)}, " +
                         $"ps.width = {((double)dictData["width"]).ToString(inv)}, " +
                         $"ps.height = {((double)dictData["height"]).ToString(inv)}, " +
                         $"ps.modified = datetime('{((DateTime)dictData["modified"]).ToString("o")}')";
                    
                    // Create ChangeLog for ProvisionalSpace modification
                    Logger.LogToFile($"UPDATEGRPH: Creating ChangeLog for modified ProvisionalSpace {psFi.Id}", "sync.log");
                    CreateChangeLogForElement(psFi.Id.Value, "Modify");
                    
                    // CRITICAL FIX: Update relationships when ProvisionalSpace is modified/moved
                    Logger.LogToFile($"UPDATEGRPH: Updating ProvisionalSpace relationships for {psFi.Id}", "sync.log");
                    UpdateProvisionalSpaceRelations(psFi, doc);
                }
                else if (e is MEPCurve pipe && pipe.Category.Id.Value == (int)BuiltInCategory.OST_PipeCurves)
                {
                    // CRITICAL FIX: Do not update pipes during pull operations to prevent feedback loop
                    if (CommandManager.Instance.IsPullInProgress)
                    {
                        Logger.LogToFile($"UPDATEGRPH: Skipping Pipe update during pull - {pipe.Id}", "sync.log");
                        continue; // Skip pipe updates during pull to prevent overwriting correct Neo4j data
                    }
                    
                    // Pipe-Eigenschaften aktualisieren
                    var data = PipeSerializer.ToNode(pipe);
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    cy = $"MATCH (p:Pipe {{ElementId: {intId}}}) " +
                         $"SET p.x1 = {((double)data["x1"]).ToString(inv)}, " +
                         $"p.y1 = {((double)data["y1"]).ToString(inv)}, " +
                         $"p.z1 = {((double)data["z1"]).ToString(inv)}, " +
                         $"p.x2 = {((double)data["x2"]).ToString(inv)}, " +
                         $"p.y2 = {((double)data["y2"]).ToString(inv)}, " +
                         $"p.z2 = {((double)data["z2"]).ToString(inv)}, " +
                         $"p.diameter = {((double)data["diameter"]).ToString(inv)}, " +
                         $"p.lastModifiedUtc = datetime('{((DateTime)data["modified"]).ToString("o")}')";
                    
                    // Create ChangeLog for Pipe modification
                    Logger.LogToFile($"UPDATEGRPH: Creating ChangeLog for modified Pipe {pipe.Id}", "sync.log");
                    CreateChangeLogForElement(pipe.Id.Value, "Modify");
                    
                    // CRITICAL FIX: Update relationships when Pipe is modified/moved
                    Logger.LogToFile($"UPDATEGRPH: Updating Pipe relationships for {pipe.Id}", "sync.log");
                    UpdatePipeRelations(pipe, doc);
                }
                else if (e is Level)
                {
                    cy = $"MATCH (l:Level {{ElementId: {intId}}}) " +
     $"SET l.Name = '{ParameterUtils.EscapeForCypher(e.Name)}'";
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
                Logger.LogToFile($"UPDATEGRPH PROCESS ADDED: Processing element {e.Id} of category {e.Category?.Name} ({e.Category?.Id.Value})", "sync.log");
                
                switch (e)
                {
                    case Room room:
                        Logger.LogToFile($"UPDATEGRPH: Processing Room {room.Id}", "sync.log");
                        ProcessRoom(room, doc);
                        break;
                    case Wall wall:
                        Logger.LogToFile($"UPDATEGRPH: Processing Wall {wall.Id}", "sync.log");
                        ProcessWall(wall, doc);
                        Logger.LogToFile($"UPDATEGRPH: Creating ChangeLog for Wall {wall.Id}", "sync.log");
                        CreateChangeLogForElement(wall.Id.Value, "Insert");
                        break;
                    case FamilyInstance fi when fi.Category.Id.Value == (int)BuiltInCategory.OST_Doors:
                        Logger.LogToFile($"UPDATEGRPH: Processing Door {fi.Id}", "sync.log");
                        ProcessDoor(fi, doc);
                        Logger.LogToFile($"UPDATEGRPH: Creating ChangeLog for Door {fi.Id}", "sync.log");
                        CreateChangeLogForElement(fi.Id.Value, "Insert");
                        break;
                    case FamilyInstance fi when fi.Category.Id.Value == (int)BuiltInCategory.OST_GenericModel && ParameterUtils.IsProvisionalSpace(fi):
                        Logger.LogToFile($"UPDATEGRPH: Processing NEW ProvisionalSpace {fi.Id} (Category: {fi.Category?.Name}, IsProvisionalSpace: {ParameterUtils.IsProvisionalSpace(fi)})", "sync.log");
                        ProcessProvisionalSpace(fi, doc);
                        Logger.LogToFile($"UPDATEGRPH: Creating ChangeLog for NEW ProvisionalSpace {fi.Id}", "sync.log");
                        CreateChangeLogForElement(fi.Id.Value, "Insert");
                        Logger.LogToFile($"UPDATEGRPH: ProvisionalSpace {fi.Id} processing COMPLETED", "sync.log");
                        break;
                    case MEPCurve pipe when pipe.Category.Id.Value == (int)BuiltInCategory.OST_PipeCurves:
                        Logger.LogToFile($"UPDATEGRPH: Processing Pipe {pipe.Id}", "sync.log");
                        ProcessPipe(pipe, doc);
                        Logger.LogToFile($"UPDATEGRPH: Creating ChangeLog for Pipe {pipe.Id}", "sync.log");
                        CreateChangeLogForElement(pipe.Id.Value, "Insert");
                        break;
                    case Element st when st.Category.Id.Value == (int)BuiltInCategory.OST_Stairs:
                        Logger.LogToFile($"UPDATEGRPH: Processing Stair {st.Id}", "sync.log");
                        // Directly process the stair element. Level information
                        // will be resolved inside ProcessStair.
                        ProcessStair(st, doc);
                        break;
                    default:
                        Logger.LogToFile($"UPDATEGRPH WARNING: Unhandled element type {e.GetType().Name} with category {e.Category?.Name} ({e.Category?.Id.Value}) for element {e.Id}", "sync.log");
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

        /// <summary>
        /// Creates ChangeLog entries for all initial elements so other sessions can pull them.
        /// This ensures that when a fresh Neo4j database is populated from one Revit session,
        /// other sessions can synchronize all elements properly.
        /// </summary>
        private void CreateInitialChangeLogEntries(Document doc)
        {
            try
            {
                Logger.LogToFile("INITIAL CHANGELOG: Starting ChangeLog creation for initial graph elements", "sync.log");
                
                var currentSessionId = _cmdManager.SessionId;
                Logger.LogToFile($"INITIAL CHANGELOG: Current session {currentSessionId}, creating generic ChangeLog entries", "sync.log");

                // Create ChangeLog entries for all relevant element types
                // Note: We create entries without specific target sessions for now
                // The ChangeLog schema allows targetSessionId to be null, and the pull mechanism 
                // will find these entries when other sessions query for unacknowledged changes
                int totalChangeLogs = 0;

                // 1. Walls
                var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().ToList();
                foreach (var wall in walls)
                {
                    CreateChangeLogForElement(wall.Id.Value, "Insert");
                    totalChangeLogs++;
                }
                Logger.LogToFile($"INITIAL CHANGELOG: Created {walls.Count} wall ChangeLog entries", "sync.log");

                // 2. Doors  
                var doors = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).ToList();
                foreach (var door in doors)
                {
                    CreateChangeLogForElement(door.Id.Value, "Insert");
                    totalChangeLogs++;
                }
                Logger.LogToFile($"INITIAL CHANGELOG: Created {doors.Count} door ChangeLog entries", "sync.log");

                // 3. Pipes
                var pipes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_PipeCurves).ToList();
                foreach (var pipe in pipes)
                {
                    CreateChangeLogForElement(pipe.Id.Value, "Insert");
                    totalChangeLogs++;
                }
                Logger.LogToFile($"INITIAL CHANGELOG: Created {pipes.Count} pipe ChangeLog entries", "sync.log");

                // 4. Provisional Spaces (Generic Models)
                var provisionalSpaces = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_GenericModel)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(inst => ParameterUtils.IsProvisionalSpace(inst))
                    .ToList();
                    
                foreach (var ps in provisionalSpaces)
                {
                    CreateChangeLogForElement(ps.Id.Value, "Insert");
                    totalChangeLogs++;
                }
                Logger.LogToFile($"INITIAL CHANGELOG: Created {provisionalSpaces.Count} provisional space ChangeLog entries", "sync.log");

                // 5. Levels
                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                foreach (var level in levels)
                {
                    CreateChangeLogForElement(level.Id.Value, "Insert");
                    totalChangeLogs++;
                }
                Logger.LogToFile($"INITIAL CHANGELOG: Created {levels.Count} level ChangeLog entries", "sync.log");

                // 6. Building (elementId=1)
                CreateChangeLogForElement(1, "Insert");
                totalChangeLogs++;
                Logger.LogToFile($"INITIAL CHANGELOG: Created 1 building ChangeLog entry", "sync.log");

                Logger.LogToFile($"INITIAL CHANGELOG: Total {totalChangeLogs} ChangeLog entries created for initial graph", "sync.log");
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Failed to create initial ChangeLog entries", ex);
            }
        }

        /// <summary>
        /// Creates a ChangeLog entry for an element using the central Neo4j-based multi-session approach
        /// </summary>
        private void CreateChangeLogForElement(long elementId, string operation)
        {
            try
            {
                Logger.LogToFile($"CHANGELOG CREATION: Creating ChangeLog for element {elementId} with operation {operation}", "sync.log");
                
                // CRITICAL FIX: Use the central Neo4j-based ChangeLog creation method
                // This automatically creates ChangeLog entries for ALL other sessions
                string creatingSessionId = CommandManager.Instance.SessionId;
                var connector = CommandManager.Instance.Neo4jConnector;
                
                // Use async Task.Run to avoid blocking the UI thread
                Task.Run(async () =>
                {
                    try
                    {
                        await connector.CreateChangeLogEntryWithRelationshipsAsync(elementId, operation, creatingSessionId);
                        Logger.LogToFile($"CHANGELOG CREATION COMPLETED: Successfully created ChangeLog entries for element {elementId} ({operation})", "sync.log");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogToFile($"CHANGELOG CREATION ERROR: Failed to create ChangeLog for element {elementId}: {ex.Message}", "sync.log");
                        Logger.LogCrash($"CreateChangeLogForElement failed for {elementId}", ex);
                    }
                });
                
                Logger.LogToFile($"CHANGELOG CREATION INITIATED: ChangeLog creation started for element {elementId}", "sync.log");
            }
            catch (Exception ex)
            {
                Logger.LogCrash("CreateChangeLogForElement", ex);
            }
        }
    }
}