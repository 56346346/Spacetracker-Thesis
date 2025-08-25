# SpaceTracker - Comprehensive Technical Documentation

## Overview
SpaceTracker is a C#/.NET desktop application (WPF/WinForms) that synchronizes architectural elements (walls, doors, pipes, provisional spaces) between Autodesk Revit and a Neo4j graph database. The system enables real-time collaboration between multiple Revit sessions by using a ChangeLog-based synchronization approach.

## Core Architecture

### Primary Components

1. **Neo4j Graph Database** - Central data repository
2. **Revit Add-in** - C# plugin for Autodesk Revit
3. **ChangeLog System** - Event-driven synchronization mechanism
4. **Session Management** - Multi-user session tracking
5. **Solibri Integration** - Automated rule validation

## Key Classes and Their Roles

### 1. SpaceTrackerClass.cs - Main Application Entry Point

**Purpose**: Central coordinator and Revit add-in entry point
**Key Methods**:
- `OnStartup(ControlledApplication)` - Initializes entire system
- `OnShutdown(ControlledApplication)` - Cleanup operations
- `CreateRibbonUI(ControlledApplication)` - Creates Revit ribbon interface
- `PerformConsistencyCheck(Document, bool)` - Runs Solibri validation
- `InitializeExistingElements(Document)` - Processes existing elements on startup

**Startup Sequence**:
1. Clear log files
2. Initialize Neo4jConnector
3. Initialize CommandManager
4. Set up HTTP client for Solibri
5. Create SpaceExtractor, GraphPuller, GraphPullHandler
6. Initialize AutoPullService
7. Create Ribbon UI with buttons
8. Register document event handlers
9. Start background Solibri process

### 2. Neo4jConnector.cs - Database Communication Layer

**Purpose**: Handles all Neo4j database operations
**Key Methods**:
- `RunQueryAsync<T>(string cypher, object parameters, Func<IRecord, T> mapper)` - Generic query execution
- `CreateChangeLogEntryWithRelationshipsAsync(int elementId, string operation, string targetSessionId)` - Creates ChangeLog entries for multi-session sync
- `GetPendingChangeLogsAsync(string sessionId)` - Retrieves unacknowledged changes for a session
- `AcknowledgeChangeLogAsync(long changeId)` - Marks ChangeLog entry as processed
- `CreateTestChangeLogEntriesAsync(string targetSessionId)` - Debug method for testing sync
- `UpdateSessionLastSyncAsync(string sessionId, DateTime lastSync)` - Updates session sync timestamps

**Database Schema**:
```cypher
// Nodes
(:Wall {elementId, x1, y1, z1, x2, y2, z2, baseLevelUid, typeName, familyName, thickness_m, location_line, flipped, structural, ...})
(:Door {elementId, x, y, z, width, height, levelUid, hostWallId, ...})
(:Pipe {elementId, x1, y1, z1, x2, y2, z2, diameter, systemType, ...})
(:ProvisionalSpace {elementId, x, y, z, width, height, depth, ...})
(:ChangeLog {elementId, op, targetSessionId, ack, ts})
(:Level {elementId, name, elevation, uid})
(:Session {sessionId, lastSync, isActive})

// Relationships
(:ChangeLog)-[:CHANGED]->(:Wall|:Door|:Pipe|:ProvisionalSpace)
(:Wall)-[:ON_LEVEL]->(:Level)
(:Door)-[:ON_LEVEL]->(:Level)
(:Door)-[:HOSTED_BY]->(:Wall)
(:Pipe)-[:INTERSECTS]->(:Wall)
(:Pipe)-[:CONTAINED_IN]->(:ProvisionalSpace)
```

### 3. CommandManager.cs - Session and Command Queue Management

**Purpose**: Singleton managing sync state, session IDs, and command queues
**Key Methods**:
- `Initialize(Neo4jConnector)` - Sets up connector and loads session
- `ProcessCypherQueueAsync()` - Processes accumulated Cypher commands
- `AddToQueue(string cypher)` - Adds command to processing queue
- `PersistSyncTime()` - Saves last sync time to file
- `LoadLastSyncTime()` - Loads last sync time from file

**Properties**:
- `SessionId` - Unique identifier for current Revit session
- `LastSyncTime` - Timestamp of last synchronization
- `cypherCommands` - Queue of pending Cypher commands
- `Neo4jConnector` - Database connection instance

**Sync File Management**:
- File location: `%APPDATA%/SpaceTracker/session_sync_time.txt`
- Format: ISO 8601 DateTime string

### 4. GraphPuller.cs - Change Application Engine

**Purpose**: Core synchronization logic for applying changes from other sessions
**Key Methods**:
- `ApplyPendingElementChanges(Document doc, string sessionId)` - Main entry point for applying changes
- `ApplyPendingWallChanges(Document doc, string sessionId)` - Processes Wall ChangeLog entries
- `UpsertWallFromGraphProperties(Dictionary<string, object> w, Document doc)` - Creates/updates walls
- `CreateWallFromProperties(Dictionary<string, object> w, Document doc)` - Creates new wall from Neo4j data
- `UpdateWallFromProperties(Wall wall, Dictionary<string, object> w, Document doc)` - Updates existing wall
- `FindElementByRemoteId<T>(Document doc, int remoteId)` - Finds elements by remote ElementId using Comments parameter
- `MarkWallWithRemoteId(Wall wall, int remoteId)` - Tags wall with remote ElementId for identification
- `AutoJoinWalls(Wall wall, Document doc)` - Automatically joins walls at endpoints

**ChangeLog Processing Flow**:
1. Query unacknowledged ChangeLog entries for session
2. For each entry:
   - Extract operation (Create/Modify/Delete) and element data
   - Apply operation to Revit document
   - Acknowledge ChangeLog entry in Neo4j
3. Commit Revit transaction

**Element Identification System**:
- Uses `ALL_MODEL_INSTANCE_COMMENTS` parameter
- Format: `"SpaceTracker:ElementId=<remoteId>"`
- Enables mapping between local and remote element IDs

### 5. SpaceExtractor.cs - Change Detection and Graph Building

**Purpose**: Detects document changes and creates ChangeLog entries
**Key Methods**:
- `UpdateGraph(Document doc)` - Main entry point for processing document changes
- `ExtractAndCompareWalls(Document doc)` - Processes wall changes
- `ExtractAndCompareDoors(Document doc)` - Processes door changes
- `ExtractAndComparePipes(Document doc)` - Processes pipe changes
- `ExtractAndCompareProvisionalSpaces(Document doc)` - Processes provisional space changes
- `CreateChangeLogForElement(int elementId, string changeType)` - Creates ChangeLog entry for all other sessions
- `InitializeExistingElements(Document doc)` - Creates initial graph from document

**Change Detection Process**:
1. Extract current elements from Revit document
2. Serialize elements to Neo4j format
3. Compare with existing graph data
4. Generate Cypher MERGE/CREATE/DELETE commands
5. Create ChangeLog entries for other sessions
6. Queue commands for processing

**Spatial Relationship Detection**:
- **Door-Wall relationships**: Uses door's `Host` property
- **Pipe-Wall intersections**: Geometric intersection testing using bounding boxes
- **Pipe-ProvisionalSpace containment**: Geometric containment testing

### 6. GraphPullHandler.cs - Transaction Management

**Purpose**: Manages pull requests and executes them within Revit transactions
**Key Methods**:
- `Execute(UIApplication app)` - External event handler entry point
- `RequestPull(Document doc)` - Initiates pull request
- `Handle(ExternalEventRequest request)` - Processes pull request within transaction

**Transaction Management**:
- Wraps all Revit document modifications in `Transaction`
- Ensures atomicity of pull operations
- Handles rollback on errors

### 7. AutoPullService.cs - Event-Driven Pull Notifications

**Purpose**: Automatically triggers pulls when changes are detected
**Key Methods**:
- `Initialize()` - Sets up Neo4j change notifications
- `OnChangeLogCreated(object sender, ChangeLogEventArgs e)` - Handles ChangeLog creation events
- `TriggerPull()` - Initiates automatic pull operation

**Event Flow**:
1. Other session creates ChangeLog entry
2. Neo4j change notification fired
3. AutoPullService receives notification
4. Automatic pull triggered for current session

### 8. DatabaseUpdateHandler.cs - Push Operations

**Purpose**: Handles database push operations via ExternalEvent
**Key Methods**:
- `Execute(UIApplication app)` - External event handler
- `RequestPush()` - Initiates push request
- `Handle(ExternalEventRequest request)` - Processes push within transaction

### 9. Serializer Classes (WallSerializer, DoorSerializer, etc.)

**Purpose**: Convert Revit elements to Neo4j-compatible format
**Key Methods**:
- `SerializeToNode(Element element, Document doc)` - Converts element to dictionary
- `GetCypherMergeQuery(Dictionary<string, object> nodeData)` - Generates Cypher MERGE statement

**Unit Conversion**:
- Revit uses feet internally
- Neo4j stores measurements in meters
- Conversion: `UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters)`

## Synchronization Workflow

### Push Operation (Local Changes → Neo4j)

1. **Document Event Triggered**: Revit document change detected
2. **SpaceExtractor.UpdateGraph()**: 
   - Extract changed elements
   - Generate Cypher commands
   - Create ChangeLog entries for other sessions
3. **CommandManager.ProcessCypherQueueAsync()**:
   - Execute accumulated Cypher commands
   - Update session timestamp
   - Trigger Solibri validation

### Pull Operation (Neo4j → Local Changes)

1. **Pull Trigger**: Manual button or automatic via AutoPullService
2. **GraphPullHandler.Handle()**:
   - Start Revit transaction
   - Call GraphPuller.ApplyPendingElementChanges()
3. **GraphPuller Processing**:
   - Query unacknowledged ChangeLog entries
   - Apply changes to Revit document
   - Acknowledge processed entries
   - Commit transaction

### ChangeLog-Based Multi-Session Sync

**ChangeLog Creation** (in Neo4jConnector.CreateChangeLogEntryWithRelationshipsAsync):
```cypher
// Get all active sessions except current
MATCH (s:Session {isActive: true})
WHERE s.sessionId <> $currentSessionId
WITH collect(s.sessionId) as targetSessions

// Create ChangeLog entry for each target session
UNWIND targetSessions as targetSession
CREATE (c:ChangeLog {
  elementId: $elementId,
  op: $operation,
  targetSessionId: targetSession,
  ack: false,
  ts: datetime()
})
WITH c
MATCH (e {elementId: $elementId})
CREATE (c)-[:CHANGED]->(e)
```

**ChangeLog Query** (in Neo4jConnector.GetPendingChangeLogsAsync):
```cypher
MATCH (c:ChangeLog {ack: false})-[:CHANGED]->(e)
WHERE c.targetSessionId = $sessionId
RETURN id(c) as changeId, c.op as operation, e
ORDER BY c.ts ASC
```

## Solibri Integration

### Components
- **SolibriApiClient.cs**: REST API client for Solibri communication
- **SolibriChecker.cs**: High-level Solibri operations
- **SolibriRulesetValidator.cs**: Document validation against rulesets
- **SolibriProcessManager.cs**: Manages Solibri process lifecycle

### Integration Points
1. **Automatic Validation**: Triggered after document changes
2. **Manual Validation**: Via PerformConsistencyCheck method
3. **Rule Execution**: Uses DeltaRuleset.cset for validation rules
4. **Result Processing**: Converts Solibri results to ClashResult objects

## Error Handling and Logging

### Logging System
- **File Locations**: `%APPDATA%/SpaceTracker/log/`
- **Log Files**:
  - `sync.log` - Synchronization operations
  - `crash.log` - Error logs and stack traces
  - `concurrency.log` - Multi-threading issues
  - `solibri.log` - Solibri API operations

### Key Logging Methods
- `Logger.LogToFile(string message, string logFile)` - General logging
- `Logger.LogCrash(string context, Exception ex)` - Error logging with stack trace
- `MethodLogger.LogMethodCall(string methodName, Dictionary<string, object> parameters)` - Method entry logging

## Configuration and Settings

### File Locations
- **Session Sync**: `%APPDATA%/SpaceTracker/session_sync_time.txt`
- **Logs**: `%APPDATA%/SpaceTracker/log/`
- **Solibri Ruleset**: `C:/Users/Public/Solibri/SOLIBRI/Regelsaetze/RegelnThesis/DeltaRuleset.cset`

### Neo4j Connection
- **Default URI**: `bolt://localhost:7687`
- **Credentials**: Embedded in Neo4jConnector constructor
- **Driver Configuration**: Uses Neo4j.Driver package

### Revit Integration
- **Target Framework**: `net8.0-windows`
- **Platform**: `x64`
- **Required References**: Autodesk Revit API assemblies
- **Add-in Manifest**: Standard Revit .addin file

## Data Flow Diagrams

### Push Flow
```
Revit Document Change
        ↓
Document Event Handler
        ↓
SpaceExtractor.UpdateGraph()
        ↓
Element Serialization (WallSerializer, etc.)
        ↓
Cypher Command Generation
        ↓
ChangeLog Creation (for other sessions)
        ↓
CommandManager.ProcessCypherQueueAsync()
        ↓
Neo4j Database Update
        ↓
Solibri Validation (automatic)
```

### Pull Flow
```
ChangeLog Created in Neo4j
        ↓
AutoPullService Notification
        ↓
GraphPullHandler.RequestPull()
        ↓
Revit Transaction Started
        ↓
GraphPuller.ApplyPendingElementChanges()
        ↓
ChangeLog Query & Processing
        ↓
Element Creation/Modification in Revit
        ↓
ChangeLog Acknowledgment
        ↓
Transaction Commit
```

## Critical Implementation Details

### Unit Conversion
- **Revit Internal Units**: Feet
- **Neo4j Storage**: Meters
- **Conversion Methods**: 
  - `UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Meters)`
  - `UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Meters)`
- **Precision**: Uses `UnitConversion.ToFeetPreciseWithLogging()` for consistent precision

### Element Identification
- **Primary**: Revit ElementId
- **Secondary**: Comments parameter with format `"SpaceTracker:ElementId=<remoteId>"`
- **Fallback**: UniqueId for cross-session element mapping

### Transaction Management
- **All Revit Modifications**: Must be within Transaction
- **External Events**: Used for cross-thread operations
- **Atomicity**: Full rollback on any operation failure

### Geometric Operations
- **Wall Creation**: Uses `Wall.Create()` with Line geometry
- **Intersection Testing**: Bounding box overlap for pipe-wall relationships
- **Containment Testing**: Point-in-box testing for pipe-provisional space relationships

## Performance Considerations

### Batching
- **Cypher Commands**: Accumulated in queue and executed in batches
- **ChangeLog Processing**: Processed in chronological order
- **Geometric Queries**: Optimized with spatial indexing considerations

### Caching
- **Element Collections**: Cached during single operation
- **Type Information**: Wall types cached for performance
- **Session Data**: Minimal database round-trips

### Memory Management
- **Document References**: Careful handling of Revit document lifecycle
- **Neo4j Connections**: Proper disposal of sessions and transactions
- **Large Collections**: Streaming where possible

## Security Considerations

### Database Access
- **Local Neo4j**: Assumes trusted local environment
- **Session Isolation**: Session-based access control
- **Data Validation**: Input sanitization for Cypher queries

### File System
- **AppData Usage**: Standard Windows application data directory
- **Log File Security**: Standard file system permissions
- **Configuration Files**: Protected by file system ACLs

## Troubleshooting Guide

### Common Issues

1. **Pull Hanging**: 
   - Check ChangeLog entries with `ack=false`
   - Use "Acknowledge All" button to reset state
   - Review sync.log for detailed operation logs

2. **Element Not Syncing**:
   - Verify Comments parameter contains proper ElementId tag
   - Check ChangeLog creation in Neo4j
   - Review session ID matching

3. **Solibri Connection Issues**:
   - Verify Solibri running on port 10876
   - Check HTTP client configuration
   - Review solibri.log for API errors

4. **Database Connection Issues**:
   - Verify Neo4j running on bolt://localhost:7687
   - Check network connectivity
   - Review connection credentials

### Debug Methods
- **Test ChangeLog Creation**: `Neo4jConnector.CreateTestChangeLogEntriesAsync()`
- **Manual Pull Trigger**: GraphPullHandler.RequestPull()
- **Log Analysis**: Comprehensive logging in all major operations
- **Transaction Rollback**: Automatic on any operation failure

## Extension Points

### Adding New Element Types
1. Create new Node class (e.g., `WindowNode.cs`)
2. Create serializer class (e.g., `WindowSerializer.cs`)
3. Add extraction method in `SpaceExtractor.cs`
4. Add processing method in `GraphPuller.cs`
5. Update ChangeLog handling

### Custom Validation Rules
1. Extend `SolibriRulesetValidator.cs`
2. Add custom rule files
3. Integrate with validation workflow
4. Update result processing

### Additional Database Operations
1. Extend `Neo4jConnector.cs` with new methods
2. Add appropriate error handling
3. Update logging for new operations
4. Consider transaction boundaries

## Version History and Evolution

The system has evolved through several key phases:
1. **Initial Implementation**: Basic wall synchronization
2. **Multi-Element Support**: Added doors, pipes, provisional spaces
3. **ChangeLog System**: Implemented event-driven synchronization
4. **Solibri Integration**: Added automated validation
5. **Performance Optimization**: Batching and precision improvements
6. **Code Cleanup**: Removed redundant validation approaches

This documentation provides a comprehensive understanding of the SpaceTracker system, enabling AI systems or developers to understand the complete workflow, identify issues, and make informed modifications to the codebase.
