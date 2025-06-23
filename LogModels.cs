namespace SpaceTracker
{
    /// <summary>
    /// Repräsentiert einen Eintrag im ChangeLog.
    /// </summary>
    public class LogChange
    {
        public long ElementId { get; set; }
        public string Type { get; set; }
        public string User { get; set; }
        public System.DateTime Timestamp { get; set; }
        public string CachePath { get; set; }
        public string Label { get; set; }
    }

    /// <summary>
    /// Variante eines ChangeLog-Eintrags, der automatisch acknowledged wurde.
    /// </summary>
    public class LogChangeAcknowledged : LogChange
    {
    }

    /// <summary>
    /// Dient zur Prüfung, ob eine Session alle Änderungen gepullt hat.
    /// </summary>
    public class SessionStatus
    {
        public string Id { get; set; }
        public bool HasPulledAll { get; set; }
    }
}