// Cypher script to repair missing GOT_CHANGED relationships
// Run this in Neo4j Browser to fix existing ChangeLog entries

// Find all ChangeLog entries without GOT_CHANGED relationships and add them
MATCH (cl:ChangeLog)
WHERE NOT EXISTS { (cl)-[:GOT_CHANGED]->() }
WITH cl
OPTIONAL MATCH (wall:Wall) WHERE wall.elementId = cl.elementId OR wall.elementId = toString(cl.elementId)
OPTIONAL MATCH (door:Door) WHERE door.elementId = cl.elementId OR door.elementId = toString(cl.elementId)
OPTIONAL MATCH (pipe:Pipe) WHERE pipe.elementId = cl.elementId OR pipe.elementId = toString(cl.elementId)
OPTIONAL MATCH (space:ProvisionalSpace) WHERE space.elementId = cl.elementId OR space.elementId = toString(cl.elementId)
OPTIONAL MATCH (level:Level) WHERE level.elementId = cl.elementId OR level.elementId = toString(cl.elementId)
OPTIONAL MATCH (building:Building) WHERE building.elementId = cl.elementId OR building.elementId = toString(cl.elementId)
WITH cl, wall, door, pipe, space, level, building
WHERE wall IS NOT NULL OR door IS NOT NULL OR pipe IS NOT NULL OR space IS NOT NULL OR level IS NOT NULL OR building IS NOT NULL
FOREACH (elem IN CASE WHEN wall IS NOT NULL THEN [wall] ELSE [] END | MERGE (cl)-[:GOT_CHANGED]->(elem))
FOREACH (elem IN CASE WHEN door IS NOT NULL THEN [door] ELSE [] END | MERGE (cl)-[:GOT_CHANGED]->(elem))
FOREACH (elem IN CASE WHEN pipe IS NOT NULL THEN [pipe] ELSE [] END | MERGE (cl)-[:GOT_CHANGED]->(elem))
FOREACH (elem IN CASE WHEN space IS NOT NULL THEN [space] ELSE [] END | MERGE (cl)-[:GOT_CHANGED]->(elem))
FOREACH (elem IN CASE WHEN level IS NOT NULL THEN [level] ELSE [] END | MERGE (cl)-[:GOT_CHANGED]->(elem))
FOREACH (elem IN CASE WHEN building IS NOT NULL THEN [building] ELSE [] END | MERGE (cl)-[:GOT_CHANGED]->(elem))
RETURN count(cl) as repaired_changelogs;

// Verify the results
MATCH (cl:ChangeLog)-[:GOT_CHANGED]->(elem)
RETURN labels(elem) as element_type, count(*) as got_changed_count
ORDER BY element_type;

// Check for ChangeLog entries that still don't have GOT_CHANGED relationships
MATCH (cl:ChangeLog)
WHERE NOT EXISTS { (cl)-[:GOT_CHANGED]->() }
RETURN count(cl) as orphaned_changelogs, collect(cl.elementId)[0..10] as sample_element_ids;
