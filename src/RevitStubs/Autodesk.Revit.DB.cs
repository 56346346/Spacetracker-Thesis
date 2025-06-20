using System;
using System.Collections.Generic;

namespace Autodesk.Revit.DB
{
    #region Grundlegende Platzhalter-Typen
    public class Element
    {
        public ElementId Id { get; } = new ElementId(1);
        public string     Name { get; set; } = "";
        public void       SetEntity(ExtensibleStorage.Entity e) { }
        public ExtensibleStorage.Entity GetEntity(ExtensibleStorage.Schema s) => null;
    }

    public class ElementId      { public ElementId(int v) { } public int IntegerValue => 0; }
    public class Document       { public Element GetElement(ElementId id) => null; public CreationDocument Create { get; } = new CreationDocument(); }
    public class CreationDocument
    {
        public Architecture.Room NewRoom(Level level, UV point)                                    => new Architecture.Room();
        public FamilyInstance    NewFamilyInstance(XYZ p, FamilySymbol s, Level l, StructuralType st) => new FamilyInstance();
        public Wall             NewWall(Line line, ElementId levelId, bool structural)             => new Wall();
    }

    public class Wall      : Element { public WallType WallType { get; } = new WallType(); public static Wall Create(Document d, Line l, ElementId levelId, bool structural) => new Wall(); public void ChangeTypeId(ElementId id) { } }
    public class WallType  : Element { }
    public class Level     : Element { }
    public class Room      : Element { public string Number { get; set; } = ""; }
    public class FamilyInstance : Element { public Category Category { get; } = new Category(); }
    public class FamilySymbol   : Element { public bool IsActive { get; set; } public void Activate() { } }
    public class Category  { public ElementId Id { get; } = new ElementId(0); }

    public class Line      { public static Line CreateBound(XYZ a, XYZ b) => new Line(); }
    public class XYZ       { public static XYZ Zero => new XYZ(); public XYZ() { } public XYZ(double x, double y, double z) { } }
    public class UV        { public UV(double u, double v) { } }

    public enum StructuralType     { NonStructural }
    public enum BuiltInCategory    { OST_Doors }

    public class FilteredElementCollector
    {
        public FilteredElementCollector(Document d) { }
        public FilteredElementCollector OfClass   (Type t)                       => this;
        public FilteredElementCollector OfCategory(BuiltInCategory c)            => this;
        public Element FirstOrDefault(Func<Element, bool> predicate = null)      => null;
    }

    #endregion ----------------------------------------------------------------


    #region Transaktionen & Updater
    public class Transaction : IDisposable
    {
        public Transaction(Document d, string name) { }
        public void Start() { }
        public void Commit() { }
        public void RollBack() { }
        public void Dispose() { }
    }

    public class TransactionGroup : IDisposable
    {
        public TransactionGroup(Document d, string name) { }
        public void Start() { }
        public void Assimilate() { }
        public void Dispose() { }
    }

    public interface IUpdater
    {
        void Execute(UpdaterData data);
        string GetAdditionalInformation();
        ChangePriority GetChangePriority();
        UpdaterId GetUpdaterId();
        bool IsApplicable(Element e);
    }

    public class UpdaterData
    {
        public Document GetDocument() => null;
        public ICollection<ElementId> GetModifiedElementIds() => null;
    }

    public class AddInId    { }
    public class UpdaterId  { public UpdaterId(AddInId a, Guid g) { } }
    public enum  ChangePriority { Anything }
    #endregion --------------------------------------------------------------


    #region Extensible Storage (Stubs)
    namespace ExtensibleStorage
    {
        public class Schema      { public static Schema Lookup(Guid id) => null; public Field GetField(string n) => null; }
        public class Field       { }
        public class SchemaBuilder { public SchemaBuilder(Guid id) { } public void AddSimpleField(string n, Type t) { } public void SetSchemaName(string n) { } public Schema Finish() => null; }
        public class Entity      { public Entity(Schema s) { } public void Set(string f, string v) { } public string Get<T>(Field f) => default; public bool IsValid() => false; }
    }
    #endregion --------------------------------------------------------------
}

namespace Autodesk.Revit.DB.Architecture
{
    public class Room : Autodesk.Revit.DB.Element
    {
        public string Number { get; set; } = "";
    }
}