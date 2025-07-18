using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace SpaceTracker
{
    public class Session
    {
        public Document Document { get; }
        public GraphPuller Puller { get; }
        public ChangeMonitor Monitor { get; }
        public DateTime LastSyncTime { get; set; }

        public Session(Document doc, GraphPuller puller, ChangeMonitor monitor)
        {
            Document = doc;
            Puller = puller;
            Monitor = monitor;
 LastSyncTime = CommandManager.Instance.LastSyncTime;
        }
    }

    public static class SessionManager
    {
        private static readonly Dictionary<string, Session> _sessions = new();
        public static IReadOnlyDictionary<string, Session> OpenSessions => _sessions;

        public static void AddSession(string id, Session session)
        {
            _sessions[id] = session;
        }

        public static void RemoveSession(string id)
        {
            _sessions.Remove(id);
        }
    }
}