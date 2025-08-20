# Copilot Instructions for SpaceTracker

## Project Overview
SpaceTracker is a C#/.NET desktop application (WPF/WinForms) for synchronizing architectural elements (walls, doors, pipes, provisional spaces) between Autodesk Revit and a Neo4j graph database. The MVP focuses on **wall synchronization only** between two parallel Revit instances using a ChangeLog-based approach.

## Key Architectural Components
- **GraphPullHandler**: Manages pull requests and executes them within Revit transactions. See `GraphPullHandler.cs`.
- **GraphPuller**: Core synchronization logic. Contains `ApplyPendingWallChanges()` for ChangeLog-based wall synchronization. See `GraphPuller.cs`.
- **Neo4jConnector**: Handles all database communication, including Cypher queries and session management. See `Neo4jConnector.cs`.
- **CommandManager**: Singleton managing sync state, session IDs, and command queues. See `CommandManager.cs`.
- **SessionManager**: Tracks open Revit sessions and their sync times. See `SessionManager.cs`.
- **Logger/MethodLogger**: Centralized logging to files in `%APPDATA%/SpaceTracker/log/` using Serilog and custom helpers.

## Synchronization Model (ChangeLog-Based)
- **Single Source of Truth**: Neo4j database with (:Wall) nodes and (:ChangeLog) entries
- **Pull Operation**: Reads unacknowledged ChangeLog entries for the current session, reconstructs walls from Neo4j attributes
- **Identity Bridge**: Uses `ALL_MODEL_INSTANCE_COMMENTS` parameter with "SpaceTracker:ElementId=<remoteId>" to map remote ElementIds to local Revit elements
- **Acknowledgment**: After successful application, sets `ack=true` on ChangeLog nodes

## Neo4j Data Model
### Wall Nodes (:Wall)
Required attributes (all in meters):
- `ElementId` (int): Primary key from source document
- `x1, y1, z1, x2, y2, z2` (double): Line geometry in project coordinates
- `baseLevelUid` (string): Revit Level UniqueId for base constraint
- `typeName, familyName` (string): Wall type identification
- `thickness_m` (double): Wall thickness for type matching
- `location_line` (int): 0-5 mapping to WallLocationLine enum
- `flipped, structural` (bool): Wall properties
- Optional: `topLevelUid, top_offset_m, unconnected_height_m, roomBounding`

### ChangeLog Nodes (:ChangeLog)
- `elementId` (int): References Wall.ElementId
- `op` (string): "Create", "Modify", or "Delete"
- `targetSessionId` (string): Destination session
- `ack` (boolean): Acknowledgment status
- `ts` (datetime): Timestamp
- Relationship: `[:CHANGED]->(:Wall)`

## Developer Workflows
- **Build**: Standard .NET build via Visual Studio or `dotnet build`. Target framework is `net8.0-windows`, platform is `x64`.
- **Run**: Launch via Visual Studio or Revit add-in loader. Ensure Revit and Neo4j are running locally.
- **Debug**: Use Visual Studio debugger. Logging output is in `%APPDATA%/SpaceTracker/log/`.
- **Pull Testing**: Use pull button in Revit to trigger `GraphPullHandler.RequestPull()`
- **Reset Sync State**: Use "Acknowledge All" button to mark all ChangeLog entries as acknowledged (prevents pull loops)

## Troubleshooting
- **Revit Hangs During Pull**: Use "Acknowledge All" button to reset ChangeLog state and prevent infinite loops
- **Solibri API Errors**: Check if Solibri is running on port 10876; these are non-critical for wall synchronization
- **Pull Failures**: Check `%APPDATA%/SpaceTracker/log/sync.log` for detailed error messages

## Project-Specific Conventions
- **Units**: Store all measurements in meters in Neo4j, convert to feet for Revit API using `UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters)`
- **Transactions**: All Revit document modifications must be wrapped in a `Transaction` (handled by `GraphPullHandler.Handle()`)
- **Logging**: Use `Logger.LogToFile` and `Logger.LogCrash` for all error and event reporting
- **Element Identification**: Use Comments parameter for remote ElementId mapping: `"SpaceTracker:ElementId=<remoteId>"`
- **Level References**: Store and resolve using Level.UniqueId, not ElementId
- **Type Matching**: Primary match by typeName+familyName, fallback by thickness tolerance

## Integration Points
- **Neo4j**: Local instance, default URI `bolt://localhost:7687`, credentials in `Neo4jConnector` constructor
- **Revit**: All document operations use Autodesk Revit API. Only run on Windows with UI thread for element creation

## Example Patterns
- Triggering a pull:
  ```csharp
  graphPullHandler.RequestPull(doc);
  ```
- ChangeLog query (in ApplyPendingWallChanges):
  ```cypher
  MATCH (c:ChangeLog {ack:false})-[:CHANGED]->(w:Wall)
  WHERE c.targetSessionId = $sessionId
  RETURN id(c) AS changeId, c.op AS op, w AS wall
  ORDER BY c.ts ASC
  ```
- Element identification:
  ```csharp
  var tag = $"SpaceTracker:ElementId={remoteId}";
  wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set(tag);
  ```

## Key Files
- `GraphPullHandler.cs`: Transaction management and pull coordination
- `GraphPuller.cs`: Core sync logic with `ApplyPendingWallChanges()`
- `Neo4jConnector.cs`: Database operations
- `CommandManager.cs`: Session and state management

## MVP Scope
Current implementation focuses on **walls only**. Doors, pipes, and provisional spaces are not part of the ChangeLog-based synchronization yet.

---
For questions about the ChangeLog synchronization model or wall reconstruction logic, review `GraphPuller.ApplyPendingWallChanges()` and related helper methods.
