using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace SpaceTracker
{
    public partial class PullDialog : Window
    {
        private readonly Document _doc;
        private readonly PropertySyncService _syncService;
        private readonly Neo4jConnector _connector;
        private readonly string _sessionId;
        public ObservableCollection<ChangeLogEntry> Entries { get; } = new ObservableCollection<ChangeLogEntry>();

        public PullDialog(Document doc, IEnumerable<ChangeLogEntry> entries, PropertySyncService syncService, Neo4jConnector connector, string sessionId)
        {
            InitializeComponent();
            _doc = doc;
            _syncService = syncService;
            _connector = connector;
            _sessionId = sessionId;
            foreach (var e in entries)
                Entries.Add(e);
            ChangesGrid.ItemsSource = Entries;
        }

        private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = ChangesGrid.SelectedItems.Cast<ChangeLogEntry>().ToList();
            if (selected.Count == 0) return;
            using (Transaction tx = new Transaction(_doc, "Apply Changes"))
            {
                tx.Start();
                foreach (var entry in selected)
                {
                    Element elem = _doc.GetElement(new ElementId(entry.ElementId));
                    if (elem is Architecture.Room)
                        await _syncService.SyncRoomParameters(_doc, entry.ElementId);
                    else if (elem is Wall)
                        await _syncService.SyncWallParameters(_doc, entry.ElementId);
                    else if (elem is FamilyInstance fi && fi.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Doors)
                        await _syncService.SyncDoorParameters(_doc, entry.ElementId);
                }
                tx.Commit();
            }
            await _connector.AcknowledgeSelectedAsync(_sessionId, selected.Select(x => x.ElementId));
            foreach (var e2 in selected.ToList())
                Entries.Remove(e2);
        }

        private async void IgnoreButton_Click(object sender, RoutedEventArgs e)
        {
            await _connector.AcknowledgeAllAsync(_sessionId);
            DialogResult = true;
            Close();
        }
    }

    public class ChangeLogEntry
    {
        public long ElementId { get; set; }
        public string ChangeType { get; set; }
        public DateTime Timestamp { get; set; }
        public string SessionId { get; set; }
    }
}