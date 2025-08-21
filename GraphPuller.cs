using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using System.Linq;
using Autodesk.Revit.UI;
using Neo4j.Driver;
using System.IO;
using static System.Environment;




namespace SpaceTracker;

[SupportedOSPlatform("windows")]
public class GraphPuller
{
    private bool _pullInProgress = false;

    private static readonly string _logDir =
       Path.Combine(GetFolderPath(Environment.SpecialFolder.ApplicationData),
       "SpaceTracker", "log");
    private static readonly string _logPath =
       Path.Combine(_logDir, nameof(GraphPuller) + ".log");
    private static readonly object _logLock = new object();

    static GraphPuller()
    {
        if (!Directory.Exists(_logDir))
            Directory.CreateDirectory(_logDir);
        MethodLogger.InitializeLog(nameof(GraphPuller));
    }

    private static void LogMethodCall(string methodName, Dictionary<string, object> parameters)
    {
        MethodLogger.Log(nameof(GraphPuller), methodName, parameters);
    }
    private readonly Neo4jConnector _connector;
    public DateTime LastPulledAt { get; private set; } = DateTime.MinValue;
    // Erzeugt den Puller mit optionalem Connector

    public GraphPuller(Neo4jConnector connector = null)
    {
        _connector = connector ?? CommandManager.Instance.Neo4jConnector;
    }

    // Applies pending changes from ChangeLog entries for this session (all element types)
    public void ApplyPendingWallChanges(Document doc, string sessionId)
    {
        var startTime = DateTime.Now;
        try
        {
            Logger.LogToFile($"PULL APPLY STARTED: ApplyPendingElementChanges for session {sessionId} on document '{doc.Title}' at {startTime:yyyy-MM-dd HH:mm:ss.fff}", "sync.log");

            // 1) Load pending changes using new ChangeLog method
            Logger.LogToFile("PULL LOADING CHANGES: Calling GetPendingChangeLogsAsync", "sync.log");
            var changes = _connector.GetPendingChangeLogsAsync(sessionId).GetAwaiter().GetResult();
            Logger.LogToFile($"PULL CHANGES LOADED: Found {changes.Count} pending element changes for session {sessionId}", "sync.log");

            if (changes.Count == 0)
            {
                // DISABLED: Test entries creation disabled while fixing sync issues
                Logger.LogToFile("PULL NO CHANGES: No changes found. Test entry creation temporarily disabled.", "sync.log");
                Logger.LogToFile("PULL DEBUG: If this persists, check if ChangeLog entries are being created properly in other session", "sync.log");
                return; // Exit early instead of creating test entries
                
                /* DISABLED - was creating ElementId 999 test entries
                // Try to create test entries for debugging
                Logger.LogToFile("PULL NO CHANGES: No changes found, creating test ChangeLog entries for debugging...", "sync.log");
                _connector.CreateTestChangeLogEntriesAsync(sessionId).GetAwaiter().GetResult();

                // Try again after creating test entries
                Logger.LogToFile("PULL RETRY: Retrying GetPendingChangeLogsAsync after creating test entries", "sync.log");
                changes = _connector.GetPendingChangeLogsAsync(sessionId).GetAwaiter().GetResult();
                Logger.LogToFile($"PULL TEST ENTRIES: After creating test entries, found {changes.Count} pending element changes", "sync.log");
                */
            }

            // 2) Apply each change
            int processedCount = 0;
            foreach (var (changeId, op, elementProperties) in changes)
            {
                processedCount++;
                try
                {
                    var elementId = elementProperties.ContainsKey("elementId") ?
                        Convert.ToInt32(elementProperties["elementId"]) : -1;
                    
                    // Determine element type from properties
                    string elementType = DetermineElementType(elementProperties);
                    
                    Logger.LogToFile($"PULL PROCESSING CHANGE {processedCount}/{changes.Count}: ChangeId={changeId}, Operation={op}, ElementId={elementId}, Type={elementType}", "sync.log");

                    // Validate elementProperties before processing
                    if (elementProperties == null || elementProperties.Count == 0)
                    {
                        Logger.LogToFile($"WARNING: Empty element properties for change {changeId}, acknowledging anyway", "sync.log");
                    }
                    else if (elementId == -1)
                    {
                        Logger.LogToFile($"WARNING: Invalid ElementId for change {changeId}, acknowledging anyway", "sync.log");
                    }
                    else
                    {
                        switch (op)
                        {
                            case "Create":
                            case "Insert":
                            case "Add":
                                Logger.LogToFile($"Processing {op} operation for {elementType} {elementId}", "sync.log");
                                if (!elementProperties.ContainsKey("__deleted__"))
                                {
                                    ApplyElementUpsert(doc, elementProperties, elementType);
                                    Logger.LogToFile($"Successfully applied {op} for {elementType} {elementId}", "sync.log");
                                }
                                break;

                            case "Modify":
                                Logger.LogToFile($"Processing Modify operation for {elementType} {elementId}", "sync.log");
                                if (!elementProperties.ContainsKey("__deleted__"))
                                {
                                    ApplyElementUpsert(doc, elementProperties, elementType);
                                    Logger.LogToFile($"Successfully applied {op} for {elementType} {elementId}", "sync.log");
                                }
                                break;

                            case "Delete":
                                Logger.LogToFile($"Processing Delete operation for {elementType} {elementId}", "sync.log");
                                DeleteElementByRemoteElementId(doc, elementId, elementType);
                                Logger.LogToFile($"Successfully deleted {elementType} {elementId}", "sync.log");
                                break;

                            default:
                                Logger.LogToFile($"Unknown operation: {op} for change {changeId}, acknowledging anyway", "sync.log");
                                break;
                        }
                    }

                    // 3) Acknowledge this specific ChangeLog entry
                    Logger.LogToFile($"PULL ACKNOWLEDGING: Acknowledging ChangeLog entry {changeId}", "sync.log");
                    _connector.AcknowledgeChangeLogAsync(changeId).GetAwaiter().GetResult();
                    Logger.LogToFile($"PULL ACKNOWLEDGED: Successfully acknowledged change {changeId}", "sync.log");

                    Logger.LogToFile($"PULL CHANGE COMPLETE: Applied and acknowledged change {changeId} ({op}) for element {elementId}", "sync.log");
                }
                catch (Exception ex)
                {
                    Logger.LogToFile($"PULL CHANGE ERROR: Error applying wall change {changeId} - {ex.Message}", "sync.log");
                    Logger.LogCrash($"Error applying wall change {changeId}", ex);
                    // Continue with next change instead of failing completely
                }
            }

            var duration = DateTime.Now - startTime;
            Logger.LogToFile($"PULL APPLY COMPLETED: ApplyPendingWallChanges finished. Processed {processedCount} changes in {duration.TotalMilliseconds:F0}ms", "sync.log");
        }
        catch (Exception ex)
        {
            var duration = DateTime.Now - startTime;
            Logger.LogToFile($"PULL APPLY FAILED: ApplyPendingWallChanges failed after {duration.TotalMilliseconds:F0}ms - {ex.Message}", "sync.log");
            Logger.LogCrash("ApplyPendingWallChanges failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Upserts a wall from ChangeLog properties dictionary
    /// </summary>
    private void UpsertWallFromGraphProperties(Document doc, Dictionary<string, object> w)
    {
        try
        {
            Logger.LogToFile($"UpsertWallFromGraphProperties called with {w.Keys.Count} properties", "sync.log");

            // --- 1) Validate required fields ---
            if (!w.ContainsKey("ElementId"))
            {
                Logger.LogToFile("Wall properties missing ElementId, skipping", "sync.log");
                return;
            }

            // Validate geometry fields
            var requiredGeometryFields = new[] { "x1", "y1", "z1", "x2", "y2", "z2" };
            var missingGeometryFields = requiredGeometryFields.Where(field => !w.ContainsKey(field)).ToList();
            if (missingGeometryFields.Any())
            {
                Logger.LogToFile($"Wall properties missing required geometry fields: {string.Join(", ", missingGeometryFields)}, skipping", "sync.log");
                return;
            }

            // Validate level information
            if (!w.ContainsKey("baseLevelUid") && !w.ContainsKey("levelId"))
            {
                Logger.LogToFile("Wall properties missing both baseLevelUid and levelId, skipping", "sync.log");
                return;
            }

            // Validate wall type information
            if (!w.ContainsKey("typeName") && !w.ContainsKey("familyName") && !w.ContainsKey("typeId") && !w.ContainsKey("thickness_m") && !w.ContainsKey("thickness_mm"))
            {
                Logger.LogToFile("Wall properties missing wall type information (typeName, familyName, typeId, or thickness), skipping", "sync.log");
                return;
            }

            // Log all available properties for debugging
            Logger.LogToFile($"Wall properties available: {string.Join(", ", w.Keys)}", "sync.log");
            Logger.LogToFile($"Geometry: x1={w.GetValueOrDefault("x1", "N/A")}, y1={w.GetValueOrDefault("y1", "N/A")}, z1={w.GetValueOrDefault("z1", "N/A")}, x2={w.GetValueOrDefault("x2", "N/A")}, y2={w.GetValueOrDefault("y2", "N/A")}, z2={w.GetValueOrDefault("z2", "N/A")}", "sync.log");

            // baseLevelUid is now required and should be stored
            if (!w.ContainsKey("baseLevelUid"))
            {
                Logger.LogToFile("Wall properties missing baseLevelUid, trying to reconstruct from levelId", "sync.log");

                // Try to reconstruct baseLevelUid from levelId
                if (w.ContainsKey("levelId"))
                {
                    var levelId = Convert.ToInt64(w["levelId"]);
                    var level = doc.GetElement(new ElementId(levelId)) as Level;
                    if (level != null)
                    {
                        w["baseLevelUid"] = level.UniqueId;
                        Logger.LogToFile($"Reconstructed baseLevelUid: {level.UniqueId}", "sync.log");
                    }
                }

                if (!w.ContainsKey("baseLevelUid"))
                {
                    Logger.LogToFile("Could not determine baseLevelUid, skipping", "sync.log");
                    return;
                }
            }

            // --- 2) Extract ElementId properly ---
            var remoteElementId = Convert.ToInt32(w["elementId"]);
            Logger.LogToFile($"Processing wall with ElementId {remoteElementId}", "sync.log");

            // --- 3) Find existing wall or create new one ---
            var existingWall = FindLocalWallByRemoteId(doc, remoteElementId);

            if (existingWall != null)
            {
                Logger.LogToFile($"Updating existing wall {existingWall.Id} for remote ElementId {remoteElementId}", "sync.log");
                // Update existing wall geometry and properties
                UpdateWallGeometry(existingWall, w);
                UpdateWallProperties(existingWall, w);
            }
            else
            {
                Logger.LogToFile($"Creating new wall for remote ElementId {remoteElementId}", "sync.log");
                // Create new wall
                var newWall = CreateWallFromProperties(doc, w);
                if (newWall != null)
                {
                    MarkWallWithRemoteId(newWall, remoteElementId);
                    Logger.LogToFile($"Successfully created and marked wall {newWall.Id} with remote ElementId {remoteElementId}", "sync.log");
                }
                else
                {
                    Logger.LogToFile($"Failed to create wall for remote ElementId {remoteElementId}", "sync.log");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogCrash("UpsertWallFromGraphProperties failed", ex);
            throw; // Re-throw to ensure proper error handling
        }
    }

    /// <summary>
    /// Creates a new wall from properties dictionary
    /// </summary>
    private Wall CreateWallFromProperties(Document doc, Dictionary<string, object> w)
    {
        try
        {
            Logger.LogToFile($"Creating wall with properties: {string.Join(", ", w.Keys)}", "sync.log");

            // Extract coordinates (stored in meters)
            var x1 = Convert.ToDouble(w["x1"]);
            var y1 = Convert.ToDouble(w["y1"]);
            var z1 = Convert.ToDouble(w["z1"]);
            var x2 = Convert.ToDouble(w["x2"]);
            var y2 = Convert.ToDouble(w["y2"]);
            var z2 = Convert.ToDouble(w["z2"]);

            // Convert from meters to Revit internal units (feet)
            var startPoint = new XYZ(ToFeet(x1), ToFeet(y1), ToFeet(z1));
            var endPoint = new XYZ(ToFeet(x2), ToFeet(y2), ToFeet(z2));
            var line = Line.CreateBound(startPoint, endPoint);

            Logger.LogToFile($"Wall geometry: Start({startPoint.X:F3}, {startPoint.Y:F3}, {startPoint.Z:F3}) End({endPoint.X:F3}, {endPoint.Y:F3}, {endPoint.Z:F3})", "sync.log");

            // Find level by UID
            var baseLevelUid = w["baseLevelUid"].ToString();
            var level = FindLevelByUid(doc, baseLevelUid);
            if (level == null)
            {
                Logger.LogToFile($"Level with UID {baseLevelUid} not found, trying by levelId", "sync.log");

                // Fallback: try by levelId
                if (w.ContainsKey("levelId"))
                {
                    var levelId = Convert.ToInt64(w["levelId"]);
                    level = doc.GetElement(new ElementId(levelId)) as Level;
                }

                // Final fallback: find any level
                if (level == null)
                {
                    level = FindLevelByName(doc, "Level 1") ?? FindLevelByName(doc, "Ebene 1");
                    if (level == null)
                    {
                        level = new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElement() as Level;
                    }
                }
            }

            if (level == null)
            {
                Logger.LogToFile("No suitable level found, cannot create wall", "sync.log");
                return null;
            }

            Logger.LogToFile($"Using level: {level.Name} (Id: {level.Id})", "sync.log");

            // Find wall type - improved type matching
            WallType wallType = null;

            // Try by typeName and familyName first
            if (w.ContainsKey("typeName") && w.ContainsKey("familyName"))
            {
                var typeName = w["typeName"].ToString();
                var familyName = w["familyName"].ToString();

                wallType = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .FirstOrDefault(wt => wt.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) &&
                                         wt.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase));

                if (wallType != null)
                {
                    Logger.LogToFile($"Found wall type by name: {wallType.Name}", "sync.log");
                }
            }

            // Fallback: try by typeId
            if (wallType == null && w.ContainsKey("typeId"))
            {
                var typeId = Convert.ToInt64(w["typeId"]);
                wallType = doc.GetElement(new ElementId(typeId)) as WallType;

                if (wallType != null)
                {
                    Logger.LogToFile($"Found wall type by ID: {wallType.Name}", "sync.log");
                }
            }

            // Fallback: try by thickness
            if (wallType == null && (w.ContainsKey("thickness_m") || w.ContainsKey("thickness_mm")))
            {
                var thickness = w.ContainsKey("thickness_m") ?
                    ToFeet(Convert.ToDouble(w["thickness_m"])) :
                    UnitConversion.ToFt(Convert.ToDouble(w["thickness_mm"]));

                wallType = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .FirstOrDefault(wt => Math.Abs(wt.Width - thickness) < 0.01); // 0.01 feet tolerance

                if (wallType != null)
                {
                    Logger.LogToFile($"Found wall type by thickness: {wallType.Name}", "sync.log");
                }
            }

            // Final fallback: use first available wall type
            if (wallType == null)
            {
                wallType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).FirstElement() as WallType;
                Logger.LogToFile($"Using default wall type: {wallType?.Name ?? "None"}", "sync.log");
            }

            if (wallType == null)
            {
                Logger.LogToFile("No valid wall type found, cannot create wall", "sync.log");
                return null;
            }

            // Get height (stored in meters)
            var height = w.ContainsKey("height") ?
                ToFeet(Convert.ToDouble(w["height"])) :
                UnitConversion.ToFt(Convert.ToDouble(w.GetValueOrDefault("height_mm", 3000.0))); // 3m default

            Logger.LogToFile($"Wall height: {height} feet", "sync.log");

            // Get base offset (stored in mm)
            var baseOffset = w.ContainsKey("base_offset_mm") ?
                UnitConversion.ToFt(Convert.ToDouble(w["base_offset_mm"])) : 0.0;

            // Get structural flag
            var structural = w.ContainsKey("structural") && Convert.ToBoolean(w["structural"]);

            // Get flipped flag  
            var flipped = w.ContainsKey("flipped") && Convert.ToBoolean(w["flipped"]);

            Logger.LogToFile($"Creating wall with: height={height:F3}ft, offset={baseOffset:F3}ft, structural={structural}, flipped={flipped}", "sync.log");

            // Create wall with correct parameters
            var wall = Wall.Create(doc, line, wallType.Id, level.Id, height, baseOffset, flipped, structural);

            // Apply additional properties after creation
            if (wall != null)
            {
                Logger.LogToFile($"Successfully created wall {wall.Id}", "sync.log");

                // KRITISCH: Markiere die Wand als SpaceTracker-Element um Feedback-Loops zu vermeiden
                MarkWallWithRemoteId(wall, Convert.ToInt32(w["elementId"]));

                // Set location line
                if (w.ContainsKey("location_line"))
                {
                    var locationLine = Convert.ToInt32(w["location_line"]);
                    wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.Set(locationLine);
                    Logger.LogToFile($"Set wall location line to {locationLine}", "sync.log");
                }

                // Set room bounding property if available
                if (w.ContainsKey("roomBounding"))
                {
                    var roomBounding = Convert.ToBoolean(w["roomBounding"]);
                    var param = wall.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
                    if (param != null && !param.IsReadOnly)
                    {
                        param.Set(roomBounding ? 1 : 0);
                        Logger.LogToFile($"Set wall room bounding to {roomBounding}", "sync.log");
                    }
                }

                // Apply height constraints if available
                if (w.ContainsKey("topLevelUid") && w.ContainsKey("top_offset_m"))
                {
                    var topLevelUid = w["topLevelUid"].ToString();
                    var topLevel = FindLevelByUid(doc, topLevelUid);
                    if (topLevel != null)
                    {
                        var topOffset = ToFeet(Convert.ToDouble(w["top_offset_m"]));
                        var topConstraintParam = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
                        var topOffsetParam = wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);

                        if (topConstraintParam != null && !topConstraintParam.IsReadOnly)
                        {
                            topConstraintParam.Set(topLevel.Id);
                            Logger.LogToFile($"Set wall top constraint to level {topLevel.Name}", "sync.log");
                        }

                        if (topOffsetParam != null && !topOffsetParam.IsReadOnly)
                        {
                            topOffsetParam.Set(topOffset);
                            Logger.LogToFile($"Set wall top offset to {topOffset:F3}ft", "sync.log");
                        }
                    }
                }
            }

            return wall;
        }
        catch (Exception ex)
        {
            Logger.LogCrash("CreateWallFromProperties failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Updates wall geometry from properties
    /// </summary>
    private void UpdateWallGeometry(Wall wall, Dictionary<string, object> w)
    {
        try
        {
            var x1 = Convert.ToDouble(w["x1"]);
            var y1 = Convert.ToDouble(w["y1"]);
            var z1 = Convert.ToDouble(w["z1"]);
            var x2 = Convert.ToDouble(w["x2"]);
            var y2 = Convert.ToDouble(w["y2"]);
            var z2 = Convert.ToDouble(w["z2"]);

            var startPoint = new XYZ(ToFeet(x1), ToFeet(y1), ToFeet(z1));
            var endPoint = new XYZ(ToFeet(x2), ToFeet(y2), ToFeet(z2));
            var newLine = Line.CreateBound(startPoint, endPoint);

            // Update location line
            var locationCurve = wall.Location as LocationCurve;
            if (locationCurve != null)
            {
                locationCurve.Curve = newLine;
            }
        }
        catch (Exception ex)
        {
            Logger.LogCrash("UpdateWallGeometry failed", ex);
        }
    }

    /// <summary>
    /// Updates wall properties from properties dictionary
    /// </summary>
    private void UpdateWallProperties(Wall wall, Dictionary<string, object> w)
    {
        try
        {
            Logger.LogToFile($"Updating properties for wall {wall.Id}", "sync.log");

            // Update height if provided
            if (w.ContainsKey("height"))
            {
                var heightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                if (heightParam != null && !heightParam.IsReadOnly)
                {
                    var newHeight = ToFeet(Convert.ToDouble(w["height"]));
                    heightParam.Set(newHeight);
                    Logger.LogToFile($"Updated wall height to {newHeight:F3}ft", "sync.log");
                }
            }

            // Update base offset if provided
            if (w.ContainsKey("base_offset_mm"))
            {
                var baseOffsetParam = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
                if (baseOffsetParam != null && !baseOffsetParam.IsReadOnly)
                {
                    var newOffset = UnitConversion.ToFt(Convert.ToDouble(w["base_offset_mm"]));
                    baseOffsetParam.Set(newOffset);
                    Logger.LogToFile($"Updated wall base offset to {newOffset:F3}ft", "sync.log");
                }
            }

            // Update location line if provided
            if (w.ContainsKey("location_line"))
            {
                var locationLineParam = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                if (locationLineParam != null && !locationLineParam.IsReadOnly)
                {
                    var newLocationLine = Convert.ToInt32(w["location_line"]);
                    locationLineParam.Set(newLocationLine);
                    Logger.LogToFile($"Updated wall location line to {newLocationLine}", "sync.log");
                }
            }

            // Update flipped state if needed
            if (w.ContainsKey("flipped"))
            {
                var shouldBeFlipped = Convert.ToBoolean(w["flipped"]);
                if (wall.Flipped != shouldBeFlipped)
                {
                    wall.Flip();
                    Logger.LogToFile($"Flipped wall to match target state: {shouldBeFlipped}", "sync.log");
                }
            }

            // Update room bounding if provided
            if (w.ContainsKey("roomBounding"))
            {
                var roomBoundingParam = wall.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
                if (roomBoundingParam != null && !roomBoundingParam.IsReadOnly)
                {
                    var newRoomBounding = Convert.ToBoolean(w["roomBounding"]);
                    roomBoundingParam.Set(newRoomBounding ? 1 : 0);
                    Logger.LogToFile($"Updated wall room bounding to {newRoomBounding}", "sync.log");
                }
            }

            Logger.LogToFile($"Completed property updates for wall {wall.Id}", "sync.log");
        }
        catch (Exception ex)
        {
            Logger.LogCrash("UpdateWallProperties failed", ex);
        }
    }

    /// <summary>
    /// Helper method to find level by UID
    /// </summary>
    private Level FindLevelByUid(Document doc, string uid)
    {
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
        foreach (var level in levels)
        {
            // Use UniqueId instead of non-existent LEVEL_UID
            if (level.UniqueId == uid)
                return level;
        }
        return null;
    }

    /// <summary>
    /// Helper method to find level by name
    /// </summary>
    private Level FindLevelByName(Document doc, string name)
    {
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
        return levels.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private Wall FindLocalWallByRemoteId(Document doc, int remoteId)
    {
        var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>();
        foreach (var w in walls)
        {
            var p = w.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            var s = p?.AsString();
            if (!string.IsNullOrEmpty(s) && s.Contains($"SpaceTracker:ElementId={remoteId}"))
                return w;
        }
        return null;
    }

    /// <summary>
    /// Generic method to find any element by remote ElementId
    /// </summary>
    private T FindElementByRemoteElementId<T>(Document doc, int remoteId) where T : Element
    {
        var elements = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>();
        foreach (var element in elements)
        {
            var p = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            var s = p?.AsString();
            if (!string.IsNullOrEmpty(s) && s.Contains($"SpaceTracker:ElementId={remoteId}"))
                return element;
        }
        return null;
    }

    private void MarkWallWithRemoteId(Wall wall, int remoteId)
    {
        try
        {
            var p = wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (p != null && !p.IsReadOnly)
            {
                var current = p.AsString();
                var tag = $"SpaceTracker:ElementId={remoteId}";
                
                // Zusätzlich: Pull-Marker hinzufügen
                var pullMarker = $"SpaceTracker:PulledFrom={CommandManager.Instance.SessionId}";
                
                if (string.IsNullOrEmpty(current))
                {
                    p.Set($"{tag}; {pullMarker}");
                }
                else if (!current.Contains(tag))
                {
                    p.Set($"{current}; {tag}; {pullMarker}");
                }
                
                Logger.LogToFile($"WALL MARKING: Marked wall {wall.Id} with remoteId {remoteId} and pull marker", "sync.log");
            }
            else
            {
                Logger.LogToFile($"WARNING: Could not mark wall {wall.Id} - Comments parameter is not available or read-only", "sync.log");
            }
        }
        catch (Exception ex)
        {
            Logger.LogToFile($"ERROR: Failed to mark wall {wall.Id} with remoteId {remoteId} - {ex.Message}", "sync.log");
        }
    }

    private void SetBaseAndHeight(Document doc, Wall wall, Level baseLevel, Level topLevel, double height_ft, double baseOffset_ft)
    {
        // Basis-Level
        var pBaseLvl = wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
        if (pBaseLvl != null && !pBaseLvl.IsReadOnly) pBaseLvl.Set(baseLevel.Id);

        // Base-Offset
        var pBaseOff = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
        if (pBaseOff != null && !pBaseOff.IsReadOnly) pBaseOff.Set(baseOffset_ft);

        if (topLevel != null)
        {
            // Top-Constraint = Level
            var pTopType = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
            if (pTopType != null && !pTopType.IsReadOnly) pTopType.Set(topLevel.Id);
        }
        else
        {
            // Unconnected Height
            var pTopType = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
            if (pTopType != null && !pTopType.IsReadOnly) pTopType.Set(ElementId.InvalidElementId);

            var pHeight = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            if (pHeight != null && !pHeight.IsReadOnly) pHeight.Set(height_ft);
        }
    }

    private void SetWallLocationLine(Wall wall, int code)
    {
        var map = new Dictionary<int, WallLocationLine>
        {
            [0] = WallLocationLine.WallCenterline,
            [1] = WallLocationLine.CoreCenterline,
            [2] = WallLocationLine.FinishFaceExterior,
            [3] = WallLocationLine.FinishFaceInterior,
            [4] = WallLocationLine.CoreExterior,
            [5] = WallLocationLine.CoreInterior
        };
        var val = map.ContainsKey(code) ? map[code] : WallLocationLine.CoreCenterline;

        var p = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
        if (p != null && !p.IsReadOnly) p.Set((int)val);
    }

    private void SetRoomBoundingIfNeeded(Wall wall, IDictionary<string, object> wprops)
    {
        if (wprops.ContainsKey("roomBounding"))
        {
            bool rb = (bool)wprops["roomBounding"];
            var p = wall.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
            if (p != null && !p.IsReadOnly) p.Set(rb ? 1 : 0);
        }
    }

    private WallType FindWallType(Document doc, string typeName, string familyName, double thickness_m)
    {
        var types = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>();
        foreach (var t in types)
        {
            if (!string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(familyName) && !t.FamilyName.Contains(familyName, StringComparison.OrdinalIgnoreCase)) continue;
            return t;
        }
        return null;
    }

    private WallType FindFallbackWallType(Document doc, string familyName, double thickness_m)
    {
        var types = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();
        if (!string.IsNullOrEmpty(familyName))
            types = types.Where(t => t.FamilyName.Contains(familyName, StringComparison.OrdinalIgnoreCase)).ToList();

        // Nächstliegende Dicke
        var target = ToFeet(thickness_m);
        WallType best = null;
        double bestDelta = double.MaxValue;
        foreach (var t in types)
        {
            var cs = t.GetCompoundStructure();
            if (cs == null) continue;
            var width = cs.GetWidth(); // Gesamtstärke in Fuß
            var delta = Math.Abs(width - target);
            if (delta < bestDelta) { bestDelta = delta; best = t; }
        }
        return best;
    }

    private void DeleteWallByRemoteElementId(Document doc, int remoteId)
    {
        var wall = FindLocalWallByRemoteId(doc, remoteId);
        if (wall != null)
        {
            doc.Delete(wall.Id);
            Logger.LogToFile($"Deleted wall for remote ElementId {remoteId}", "sync.log");
        }
    }

    // Konvertierer (using existing UnitConversion patterns)
    private static XYZ ToFeetXYZ(double xm, double ym, double zm) =>
        new XYZ(ToFeet(xm), ToFeet(ym), ToFeet(zm));

    private static double ToFeet(double meters) =>
        UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);
    // LEGACY METHOD - Redirects to ChangeLog-based synchronization
    public void PullRemoteChanges(Document doc, string currentUserId)
    {
        var startTime = DateTime.Now;
        Logger.LogToFile($"PULL REMOTE STARTED: PullRemoteChanges called for document '{doc.Title}' with user {currentUserId} at {startTime:yyyy-MM-dd HH:mm:ss.fff}", "sync.log");

        // KRITISCH: Pull-Modus aktivieren für direkte PullRemoteChanges Aufrufe
        bool wasPullInProgress = CommandManager.Instance.IsPullInProgress;
        if (!wasPullInProgress)
        {
            CommandManager.Instance.IsPullInProgress = true;
        }

        // For MVP, redirect to ChangeLog-based wall synchronization only
        try
        {
            Logger.LogToFile("PULL REMOTE TRANSACTION: Starting Revit transaction for ChangeLog pull", "sync.log");
            using (var tx = new Transaction(doc, "SpaceTracker ChangeLog Pull"))
            {
                tx.Start();
                Logger.LogToFile("PULL REMOTE APPLYING: Calling ApplyPendingWallChanges within transaction", "sync.log");
                ApplyPendingWallChanges(doc, currentUserId);
                tx.Commit();
                var duration = DateTime.Now - startTime;
                Logger.LogToFile($"PULL REMOTE COMPLETED: ChangeLog-based pull completed successfully in {duration.TotalMilliseconds:F0}ms", "sync.log");
            }
        }
        catch (Exception ex)
        {
            var duration = DateTime.Now - startTime;
            Logger.LogToFile($"PULL REMOTE FAILED: PullRemoteChanges failed after {duration.TotalMilliseconds:F0}ms - {ex.Message}", "sync.log");
            Logger.LogCrash("PullRemoteChanges fallback failed", ex);
            throw;
        }
        finally
        {
            // KRITISCH: Pull-Modus nur deaktivieren wenn wir ihn aktiviert haben
            if (!wasPullInProgress)
            {
                CommandManager.Instance.IsPullInProgress = false;
                Logger.LogToFile("PULL REMOTE: Pull mode deactivated after direct PullRemoteChanges call", "sync.log");
            }
        }
    }

    /// <summary>
    /// Determines the element type from Neo4j properties
    /// </summary>
    private string DetermineElementType(Dictionary<string, object> properties)
    {
        // Check for explicit type from Neo4j query first
        if (properties.ContainsKey("__element_type__"))
        {
            var elementType = properties["__element_type__"].ToString();
            if (!string.IsNullOrEmpty(elementType) && elementType != "Unknown")
            {
                return elementType;
            }
        }
        
        // Check for explicit type indicators
        if (properties.ContainsKey("rvtClass"))
        {
            return properties["rvtClass"].ToString();
        }
        
        // Infer from presence of specific properties
        if (properties.ContainsKey("x1") && properties.ContainsKey("y1") && properties.ContainsKey("z1") && 
            properties.ContainsKey("x2") && properties.ContainsKey("y2") && properties.ContainsKey("z2"))
        {
            if (properties.ContainsKey("thickness_m") || properties.ContainsKey("thickness_mm"))
                return "Wall";
            if (properties.ContainsKey("diameter"))
                return "Pipe";
        }
        
        if (properties.ContainsKey("hostId") || properties.ContainsKey("hostUid"))
        {
            if (properties.ContainsKey("width") && properties.ContainsKey("height"))
                return "Door";
        }
        
        if (properties.ContainsKey("guid") || properties.ContainsKey("familyName"))
            return "ProvisionalSpace";
            
        // Default fallback
        Logger.LogToFile($"WARNING: Could not determine element type from properties, defaulting to Wall", "sync.log");
        return "Wall";
    }

    /// <summary>
    /// Apply element upsert based on type
    /// </summary>
    private void ApplyElementUpsert(Document doc, Dictionary<string, object> properties, string elementType)
    {
        switch (elementType)
        {
            case "Wall":
                UpsertWallFromGraphProperties(doc, properties);
                break;
            case "Door":
                UpsertDoorFromGraphProperties(doc, properties);
                break;
            case "Pipe":
                UpsertPipeFromGraphProperties(doc, properties);
                break;
            case "ProvisionalSpace":
                UpsertProvisionalSpaceFromGraphProperties(doc, properties);
                break;
            default:
                Logger.LogToFile($"WARNING: Unknown element type {elementType}, treating as Wall", "sync.log");
                UpsertWallFromGraphProperties(doc, properties);
                break;
        }
    }

    /// <summary>
    /// Delete element by remote ElementId based on type
    /// </summary>
    private void DeleteElementByRemoteElementId(Document doc, int remoteElementId, string elementType)
    {
        switch (elementType)
        {
            case "Wall":
                DeleteWallByRemoteElementId(doc, remoteElementId);
                break;
            case "Door":
                DeleteDoorByRemoteElementId(doc, remoteElementId);
                break;
            case "Pipe":
                DeletePipeByRemoteElementId(doc, remoteElementId);
                break;
            case "ProvisionalSpace":
                DeleteProvisionalSpaceByRemoteElementId(doc, remoteElementId);
                break;
            default:
                Logger.LogToFile($"WARNING: Unknown element type {elementType} for deletion", "sync.log");
                break;
        }
    }

    #region Door Methods

    /// <summary>
    /// Creates or updates a door from Neo4j graph properties
    /// </summary>
    private void UpsertDoorFromGraphProperties(Document doc, Dictionary<string, object> properties)
    {
        try
        {
            int remoteElementId = Convert.ToInt32(properties["elementId"]);
            Logger.LogToFile($"DOOR UPSERT: Processing door with remoteElementId={remoteElementId}", "sync.log");

            // Find existing door by SpaceTracker tag
            FamilyInstance existingDoor = FindElementByRemoteElementId<FamilyInstance>(doc, remoteElementId);

            if (existingDoor != null)
            {
                Logger.LogToFile($"DOOR UPSERT: Updating existing door ElementId={existingDoor.Id}", "sync.log");
                UpdateDoorFromGraphProperties(existingDoor, properties);
            }
            else
            {
                Logger.LogToFile($"DOOR UPSERT: Creating new door", "sync.log");
                CreateDoorFromGraphProperties(doc, properties);
            }
        }
        catch (Exception ex)
        {
            Logger.LogCrash($"ERROR in UpsertDoorFromGraphProperties: {ex.Message}", ex);
        }
    }

    private void CreateDoorFromGraphProperties(Document doc, Dictionary<string, object> properties)
    {
        try
        {
            int remoteElementId = Convert.ToInt32(properties["elementId"]);
            
            // Get door type and family information
            string typeName = properties.ContainsKey("typeName") ? properties["typeName"].ToString() : "Standard";
            string familyName = properties.ContainsKey("familyName") ? properties["familyName"].ToString() : "Door";
            
            // Find door family symbol
            FamilySymbol doorSymbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_Doors)
                .Cast<FamilySymbol>()
                .FirstOrDefault(fs => fs.Family.Name == familyName && fs.Name == typeName) ??
                new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_Doors)
                .Cast<FamilySymbol>()
                .FirstOrDefault();

            if (doorSymbol == null)
            {
                Logger.LogToFile($"ERROR: No door symbol found for family={familyName}, type={typeName}", "sync.log");
                return;
            }

            if (!doorSymbol.IsActive)
                doorSymbol.Activate();

            // Get host wall information
            int hostElementId = properties.ContainsKey("hostId") ? Convert.ToInt32(properties["hostId"]) : 0;
            Wall hostWall = null;
            
            if (hostElementId > 0)
            {
                hostWall = FindElementByRemoteElementId<Wall>(doc, hostElementId);
            }

            if (hostWall == null)
            {
                Logger.LogToFile($"ERROR: Host wall not found for door remoteElementId={remoteElementId}", "sync.log");
                return;
            }

            // Create door location point
            double x = Convert.ToDouble(properties.ContainsKey("x") ? properties["x"] : 0);
            double y = Convert.ToDouble(properties.ContainsKey("y") ? properties["y"] : 0);
            double z = Convert.ToDouble(properties.ContainsKey("z") ? properties["z"] : 0);
            
            XYZ location = new XYZ(
                UnitUtils.ConvertToInternalUnits(x, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(y, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(z, UnitTypeId.Meters)
            );

            // Create the door
            FamilyInstance door = doc.Create.NewFamilyInstance(location, doorSymbol, hostWall, StructuralType.NonStructural);
            
            // Mark with SpaceTracker tag
            MarkDoorWithRemoteId(door, remoteElementId);
            
            Logger.LogToFile($"DOOR CREATE: Created door ElementId={door.Id} for remoteElementId={remoteElementId}", "sync.log");
        }
        catch (Exception ex)
        {
            Logger.LogCrash($"ERROR in CreateDoorFromGraphProperties: {ex.Message}", ex);
        }
    }

    private void UpdateDoorFromGraphProperties(FamilyInstance door, Dictionary<string, object> properties)
    {
        try
        {
            Logger.LogToFile($"DOOR UPDATE: Updating door ElementId={door.Id}", "sync.log");
            
            // Update door parameters if they exist
            if (properties.ContainsKey("width"))
            {
                double width = Convert.ToDouble(properties["width"]);
                Parameter widthParam = door.LookupParameter("Width");
                if (widthParam != null && !widthParam.IsReadOnly)
                {
                    widthParam.Set(UnitUtils.ConvertToInternalUnits(width, UnitTypeId.Meters));
                }
            }
            
            if (properties.ContainsKey("height"))
            {
                double height = Convert.ToDouble(properties["height"]);
                Parameter heightParam = door.LookupParameter("Height");
                if (heightParam != null && !heightParam.IsReadOnly)
                {
                    heightParam.Set(UnitUtils.ConvertToInternalUnits(height, UnitTypeId.Meters));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogCrash($"ERROR in UpdateDoorFromGraphProperties: {ex.Message}", ex);
        }
    }

    private void MarkDoorWithRemoteId(FamilyInstance door, int remoteElementId)
    {
        try
        {
            if (CommandManager.Instance.IsPullInProgress)
            {
                var tag = $"SpaceTracker:ElementId={remoteElementId}:Pull";
                door.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set(tag);
                Logger.LogToFile($"DOOR MARK: Marked door ElementId={door.Id} with pull tag={tag}", "sync.log");
            }
            else
            {
                var tag = $"SpaceTracker:ElementId={remoteElementId}";
                door.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set(tag);
                Logger.LogToFile($"DOOR MARK: Marked door ElementId={door.Id} with tag={tag}", "sync.log");
            }
        }
        catch (Exception ex)
        {
            Logger.LogCrash($"ERROR in MarkDoorWithRemoteId: {ex.Message}", ex);
        }
    }

    private void DeleteDoorByRemoteElementId(Document doc, int remoteElementId)
    {
        try
        {
            FamilyInstance door = FindElementByRemoteElementId<FamilyInstance>(doc, remoteElementId);
            if (door != null)
            {
                Logger.LogToFile($"DOOR DELETE: Deleting door ElementId={door.Id} for remoteElementId={remoteElementId}", "sync.log");
                doc.Delete(door.Id);
            }
            else
            {
                Logger.LogToFile($"DOOR DELETE: Door not found for remoteElementId={remoteElementId}", "sync.log");
            }
        }
        catch (Exception ex)
        {
            Logger.LogCrash($"ERROR in DeleteDoorByRemoteElementId: {ex.Message}", ex);
        }
    }

    #endregion

    #region Pipe Methods

    /// <summary>
    /// Creates or updates a pipe from Neo4j graph properties
    /// </summary>
    private void UpsertPipeFromGraphProperties(Document doc, Dictionary<string, object> properties)
    {
        try
        {
            int remoteElementId = Convert.ToInt32(properties["elementId"]);
            Logger.LogToFile($"PIPE UPSERT: Processing pipe with remoteElementId={remoteElementId}", "sync.log");

            // Find existing pipe by SpaceTracker tag
            Pipe existingPipe = FindElementByRemoteElementId<Pipe>(doc, remoteElementId);

            if (existingPipe != null)
            {
                Logger.LogToFile($"PIPE UPSERT: Updating existing pipe ElementId={existingPipe.Id}", "sync.log");
                UpdatePipeFromGraphProperties(existingPipe, properties);
            }
            else
            {
                Logger.LogToFile($"PIPE UPSERT: Creating new pipe", "sync.log");
                CreatePipeFromGraphProperties(doc, properties);
            }
        }
        catch (Exception ex)
        {
            Logger.LogCrash($"ERROR in UpsertPipeFromGraphProperties: {ex.Message}", ex);
        }
    }

    private void CreatePipeFromGraphProperties(Document doc, Dictionary<string, object> properties)
    {
        try
        {
            int remoteElementId = Convert.ToInt32(properties["elementId"]);
            
            // Get pipe coordinates
            double x1 = Convert.ToDouble(properties["x1"]);
            double y1 = Convert.ToDouble(properties["y1"]);
            double z1 = Convert.ToDouble(properties["z1"]);
            double x2 = Convert.ToDouble(properties["x2"]);
            double y2 = Convert.ToDouble(properties["y2"]);
            double z2 = Convert.ToDouble(properties["z2"]);
            
            XYZ startPoint = new XYZ(
                UnitUtils.ConvertToInternalUnits(x1, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(y1, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(z1, UnitTypeId.Meters)
            );
            
            XYZ endPoint = new XYZ(
                UnitUtils.ConvertToInternalUnits(x2, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(y2, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(z2, UnitTypeId.Meters)
            );

            // Get pipe type
            string typeName = properties.ContainsKey("typeName") ? properties["typeName"].ToString() : "Default";
            PipeType pipeType = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .FirstOrDefault(pt => pt.Name == typeName) ??
                new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .FirstOrDefault();

            if (pipeType == null)
            {
                Logger.LogToFile($"ERROR: No pipe type found for typeName={typeName}", "sync.log");
                return;
            }

            // Get mechanical system (required for pipe creation)
            MEPSystem system = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystem))
                .Cast<MEPSystem>()
                .FirstOrDefault() ??
                new FilteredElementCollector(doc)
                .OfClass(typeof(MEPSystem))
                .Cast<MEPSystem>()
                .FirstOrDefault();

            if (system == null)
            {
                Logger.LogToFile($"ERROR: No mechanical system found for pipe creation", "sync.log");
                return;
            }

            // Get level for pipe placement
            Level level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault();

            if (level == null)
            {
                Logger.LogToFile($"ERROR: No level found for pipe creation", "sync.log");
                return;
            }

            // Create the pipe
            Pipe pipe = Pipe.Create(doc, system.GetTypeId(), pipeType.Id, level.Id, startPoint, endPoint);
            
            if (pipe != null)
            {
                // Mark with SpaceTracker tag
                MarkPipeWithRemoteId(pipe, remoteElementId);
                Logger.LogToFile($"PIPE CREATE: Created pipe ElementId={pipe.Id} for remoteElementId={remoteElementId}", "sync.log");
            }
        }
        catch (Exception ex)
        {
            Logger.LogCrash($"ERROR in CreatePipeFromGraphProperties: {ex.Message}", ex);
        }
    }

    private void UpdatePipeFromGraphProperties(Pipe pipe, Dictionary<string, object> properties)
    {
        try
        {
            Logger.LogToFile($"PIPE UPDATE: Updating pipe ElementId={pipe.Id}", "sync.log");
            
            // Update pipe geometry
            if (properties.ContainsKey("x1") && properties.ContainsKey("y1") && properties.ContainsKey("z1") &&
                properties.ContainsKey("x2") && properties.ContainsKey("y2") && properties.ContainsKey("z2"))
            {
                double x1 = Convert.ToDouble(properties["x1"]);
                double y1 = Convert.ToDouble(properties["y1"]);
                double z1 = Convert.ToDouble(properties["z1"]);
                double x2 = Convert.ToDouble(properties["x2"]);
                double y2 = Convert.ToDouble(properties["y2"]);
                double z2 = Convert.ToDouble(properties["z2"]);
                
                XYZ startPoint = new XYZ(
                    UnitUtils.ConvertToInternalUnits(x1, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(y1, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(z1, UnitTypeId.Meters)
                );
                
                XYZ endPoint = new XYZ(
                    UnitUtils.ConvertToInternalUnits(x2, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(y2, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(z2, UnitTypeId.Meters)
                );

                // Update pipe location curve
                LocationCurve locationCurve = pipe.Location as LocationCurve;
                if (locationCurve != null)
                {
                    Line newLine = Line.CreateBound(startPoint, endPoint);
                    locationCurve.Curve = newLine;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogCrash($"ERROR in UpdatePipeFromGraphProperties: {ex.Message}", ex);
        }
    }

    private void MarkPipeWithRemoteId(Pipe pipe, int remoteElementId)
    {
        try
        {
            if (CommandManager.Instance.IsPullInProgress)
            {
                var tag = $"SpaceTracker:ElementId={remoteElementId}:Pull";
                pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set(tag);
                Logger.LogToFile($"PIPE MARK: Marked pipe ElementId={pipe.Id} with pull tag={tag}", "sync.log");
            }
            else
            {
                var tag = $"SpaceTracker:ElementId={remoteElementId}";
                pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set(tag);
                Logger.LogToFile($"PIPE MARK: Marked pipe ElementId={pipe.Id} with tag={tag}", "sync.log");
            }
        }
        catch (Exception ex)
        {
            Logger.LogCrash($"ERROR in MarkPipeWithRemoteId: {ex.Message}", ex);
        }
    }

    private void DeletePipeByRemoteElementId(Document doc, int remoteElementId)
    {
        try
        {
            Pipe pipe = FindElementByRemoteElementId<Pipe>(doc, remoteElementId);
            if (pipe != null)
            {
                Logger.LogToFile($"PIPE DELETE: Deleting pipe ElementId={pipe.Id} for remoteElementId={remoteElementId}", "sync.log");
                doc.Delete(pipe.Id);
            }
            else
            {
                Logger.LogToFile($"PIPE DELETE: Pipe not found for remoteElementId={remoteElementId}", "sync.log");
            }
        }
        catch (Exception ex)
        {
            Logger.LogCrash($"ERROR in DeletePipeByRemoteElementId: {ex.Message}", ex);
        }
    }

    #endregion

    #region ProvisionalSpace Methods

    /// <summary>
    /// Creates or updates a provisional space from Neo4j graph properties
    /// </summary>
    private void UpsertProvisionalSpaceFromGraphProperties(Document doc, Dictionary<string, object> properties)
    {
        try
        {
            int remoteElementId = Convert.ToInt32(properties["elementId"]);
            Logger.LogToFile($"PROVISIONALSPACE UPSERT: Processing provisional space with remoteElementId={remoteElementId}", "sync.log");

            // Find existing provisional space by SpaceTracker tag
            FamilyInstance existingSpace = FindElementByRemoteElementId<FamilyInstance>(doc, remoteElementId);

            if (existingSpace != null)
            {
                Logger.LogToFile($"PROVISIONALSPACE UPSERT: Updating existing provisional space ElementId={existingSpace.Id}", "sync.log");
                UpdateProvisionalSpaceFromGraphProperties(existingSpace, properties);
            }
            else
            {
                Logger.LogToFile($"PROVISIONALSPACE UPSERT: Creating new provisional space", "sync.log");
                CreateProvisionalSpaceFromGraphProperties(doc, properties);
            }
        }
        catch (Exception ex)
        {
            Logger.LogCrash($"ERROR in UpsertProvisionalSpaceFromGraphProperties: {ex.Message}", ex);
        }
    }

    private void CreateProvisionalSpaceFromGraphProperties(Document doc, Dictionary<string, object> properties)
    {
        try
        {
            int remoteElementId = Convert.ToInt32(properties["elementId"]);
            
            // Get family information
            string familyName = properties.ContainsKey("familyName") ? properties["familyName"].ToString() : "ProvisionalSpace";
            string typeName = properties.ContainsKey("typeName") ? properties["typeName"].ToString() : "Default";
            
            // Find provisional space family symbol
            FamilySymbol spaceSymbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_GenericModel)
                .Cast<FamilySymbol>()
                .FirstOrDefault(fs => fs.Family.Name == familyName && fs.Name == typeName) ??
                new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_GenericModel)
                .Cast<FamilySymbol>()
                .FirstOrDefault();

            if (spaceSymbol == null)
            {
                Logger.LogToFile($"ERROR: No provisional space symbol found for family={familyName}, type={typeName}", "sync.log");
                return;
            }

            if (!spaceSymbol.IsActive)
                spaceSymbol.Activate();

            // Create location point
            double x = Convert.ToDouble(properties.ContainsKey("x") ? properties["x"] : 0);
            double y = Convert.ToDouble(properties.ContainsKey("y") ? properties["y"] : 0);
            double z = Convert.ToDouble(properties.ContainsKey("z") ? properties["z"] : 0);
            
            XYZ location = new XYZ(
                UnitUtils.ConvertToInternalUnits(x, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(y, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(z, UnitTypeId.Meters)
            );

            // Create the provisional space
            FamilyInstance space = doc.Create.NewFamilyInstance(location, spaceSymbol, StructuralType.NonStructural);
            
            // Set custom parameters if they exist
            if (properties.ContainsKey("guid"))
            {
                Parameter guidParam = space.LookupParameter("GUID");
                if (guidParam != null && !guidParam.IsReadOnly)
                {
                    guidParam.Set(properties["guid"].ToString());
                }
            }
            
            // Mark with SpaceTracker tag
            MarkProvisionalSpaceWithRemoteId(space, remoteElementId);
            
            Logger.LogToFile($"PROVISIONALSPACE CREATE: Created provisional space ElementId={space.Id} for remoteElementId={remoteElementId}", "sync.log");
        }
        catch (Exception ex)
        {
            Logger.LogCrash($"ERROR in CreateProvisionalSpaceFromGraphProperties: {ex.Message}", ex);
        }
    }

    private void UpdateProvisionalSpaceFromGraphProperties(FamilyInstance space, Dictionary<string, object> properties)
    {
        try
        {
            Logger.LogToFile($"PROVISIONALSPACE UPDATE: Updating provisional space ElementId={space.Id}", "sync.log");
            
            // Update location if coordinates are provided
            if (properties.ContainsKey("x") && properties.ContainsKey("y") && properties.ContainsKey("z"))
            {
                double x = Convert.ToDouble(properties["x"]);
                double y = Convert.ToDouble(properties["y"]);
                double z = Convert.ToDouble(properties["z"]);
                
                XYZ newLocation = new XYZ(
                    UnitUtils.ConvertToInternalUnits(x, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(y, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(z, UnitTypeId.Meters)
                );

                LocationPoint locationPoint = space.Location as LocationPoint;
                if (locationPoint != null)
                {
                    locationPoint.Point = newLocation;
                }
            }
            
            // Update GUID parameter if provided
            if (properties.ContainsKey("guid"))
            {
                Parameter guidParam = space.LookupParameter("GUID");
                if (guidParam != null && !guidParam.IsReadOnly)
                {
                    guidParam.Set(properties["guid"].ToString());
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogCrash($"ERROR in UpdateProvisionalSpaceFromGraphProperties: {ex.Message}", ex);
        }
    }

    private void MarkProvisionalSpaceWithRemoteId(FamilyInstance space, int remoteElementId)
    {
        try
        {
            if (CommandManager.Instance.IsPullInProgress)
            {
                var tag = $"SpaceTracker:ElementId={remoteElementId}:Pull";
                space.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set(tag);
                Logger.LogToFile($"PROVISIONALSPACE MARK: Marked provisional space ElementId={space.Id} with pull tag={tag}", "sync.log");
            }
            else
            {
                var tag = $"SpaceTracker:ElementId={remoteElementId}";
                space.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set(tag);
                Logger.LogToFile($"PROVISIONALSPACE MARK: Marked provisional space ElementId={space.Id} with tag={tag}", "sync.log");
            }
        }
        catch (Exception ex)
        {
            Logger.LogCrash($"ERROR in MarkProvisionalSpaceWithRemoteId: {ex.Message}", ex);
        }
    }

    private void DeleteProvisionalSpaceByRemoteElementId(Document doc, int remoteElementId)
    {
        try
        {
            FamilyInstance space = FindElementByRemoteElementId<FamilyInstance>(doc, remoteElementId);
            if (space != null)
            {
                Logger.LogToFile($"PROVISIONALSPACE DELETE: Deleting provisional space ElementId={space.Id} for remoteElementId={remoteElementId}", "sync.log");
                doc.Delete(space.Id);
            }
            else
            {
                Logger.LogToFile($"PROVISIONALSPACE DELETE: Provisional space not found for remoteElementId={remoteElementId}", "sync.log");
            }
        }
        catch (Exception ex)
        {
            Logger.LogCrash($"ERROR in DeleteProvisionalSpaceByRemoteElementId: {ex.Message}", ex);
        }
    }

    #endregion

}