using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace SpaceTracker
{
    public class Session
    {
        public Document Document { get; }
        public GraphPuller Puller { get; }

        public Session(Document doc, GraphPuller puller)
        {
            Document = doc;
            Puller = puller;
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