using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using System.Linq;
using System.Threading.Tasks;

namespace SpaceTracker
{
    public class PropertySyncService
    {
        private readonly Neo4jConnector _connector;

        public PropertySyncService(Neo4jConnector connector)
        {
            _connector = connector;
        }

        public async Task SyncRoomParameters(Document doc, long elementId)
        {
            var recs = await _connector.RunReadQueryAsync(
                "MATCH (r:Room {ElementId: $id}) RETURN r.Name AS name",
                new { id = elementId });
            var room = doc.GetElement(new ElementId(elementId)) as Architecture.Room;
            if (room == null) return;
            string newName = recs.FirstOrDefault()?[
                "name"]?.As<string>() ?? string.Empty;
            if (!string.IsNullOrEmpty(newName))
            {
                room.Name = newName;
            }
        }

        public async Task SyncWallParameters(Document doc, long elementId)
        {
            var wall = doc.GetElement(new ElementId(elementId)) as Wall;
            if (wall == null) return;

            var recs = await _connector.RunReadQueryAsync(
                "MATCH (w:Wall {ElementId: $id}) RETURN coalesce(w.Name, w.Type) AS name",
                new { id = elementId });
            string newName = recs.FirstOrDefault()?[
                "name"]?.As<string>() ?? string.Empty;
            if (string.IsNullOrEmpty(newName)) return;

            WallType wType = wall.WallType;
            if (wType.Name != newName)
            {
                wType.Name = newName;
            }
        }

        public async Task SyncDoorParameters(Document doc, long elementId)
        {
            var fi = doc.GetElement(new ElementId(elementId)) as FamilyInstance;
            if (fi == null || fi.Category.Id.IntegerValue != (int)BuiltInCategory.OST_Doors) return;

            var recs = await _connector.RunReadQueryAsync(
                "MATCH (d:Door {ElementId: $id}) RETURN d.Name AS mark",
                new { id = elementId });
            string newMark = recs.FirstOrDefault()?[
                "mark"]?.As<string>() ?? string.Empty;
            if (!string.IsNullOrEmpty(newMark))
            {
                var markParam = fi.get_Parameter(BuiltInParameter.DOOR_NUMBER);
                if (markParam != null && !markParam.IsReadOnly)
                {
                    markParam.Set(newMark);
                }
            }
        }
    }
}