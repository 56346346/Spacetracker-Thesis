// Test cypher to validate GOT_CHANGED relationships are working
// Run this in Neo4j Browser after creating a new wall

// 1. Check total ChangeLog entries
MATCH (cl:ChangeLog)
RETURN count(cl) as total_changelogs;

// 2. Check ChangeLog entries WITH GOT_CHANGED relationships
MATCH (cl:ChangeLog)-[:GOT_CHANGED]->(elem)
RETURN count(DISTINCT cl) as changelogs_with_relationships, 
       labels(elem) as element_types, 
       count(*) as relationship_count
ORDER BY element_types;

// 3. Check ChangeLog entries WITHOUT GOT_CHANGED relationships
MATCH (cl:ChangeLog)
WHERE NOT EXISTS { (cl)-[:GOT_CHANGED]->() }
RETURN count(cl) as orphaned_changelogs, 
       collect(DISTINCT cl.elementId)[0..10] as sample_elementIds,
       collect(DISTINCT cl.type)[0..5] as sample_types;

// 4. Check if elements exist for orphaned ChangeLog entries
MATCH (cl:ChangeLog)
WHERE NOT EXISTS { (cl)-[:GOT_CHANGED]->() }
WITH cl.elementId as eid
OPTIONAL MATCH (wall:Wall) WHERE wall.elementId = eid OR wall.elementId = toString(eid)
OPTIONAL MATCH (door:Door) WHERE door.elementId = eid OR door.elementId = toString(eid)
OPTIONAL MATCH (pipe:Pipe) WHERE pipe.elementId = eid OR pipe.elementId = toString(eid)
OPTIONAL MATCH (space:ProvisionalSpace) WHERE space.elementId = eid OR space.elementId = toString(eid)
RETURN eid, 
       CASE WHEN wall IS NOT NULL THEN 'Wall' ELSE null END as wall_exists,
       CASE WHEN door IS NOT NULL THEN 'Door' ELSE null END as door_exists,
       CASE WHEN pipe IS NOT NULL THEN 'Pipe' ELSE null END as pipe_exists,
       CASE WHEN space IS NOT NULL THEN 'ProvisionalSpace' ELSE null END as space_exists
LIMIT 20;

// 5. Detailed analysis of element ID formats
MATCH (wall:Wall)
RETURN 'Wall' as type, collect(DISTINCT toString(wall.elementId))[0..5] as sample_elementIds
UNION
MATCH (door:Door)
RETURN 'Door' as type, collect(DISTINCT toString(door.elementId))[0..5] as sample_elementIds
UNION
MATCH (pipe:Pipe)
RETURN 'Pipe' as type, collect(DISTINCT toString(pipe.elementId))[0..5] as sample_elementIds
UNION
MATCH (space:ProvisionalSpace)
RETURN 'ProvisionalSpace' as type, collect(DISTINCT toString(space.elementId))[0..5] as sample_elementIds
UNION
MATCH (cl:ChangeLog)
RETURN 'ChangeLog' as type, collect(DISTINCT toString(cl.elementId))[0..10] as sample_elementIds;
