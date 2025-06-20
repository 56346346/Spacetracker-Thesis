namespace Autodesk.Revit.DB
{
<<<<<<< HEAD
    public class Element { public ElementId Id { get; } = new ElementId(1); }
    public class ElementId { public ElementId(int v) { } }
    public class Document
    {
        public Element GetElement(ElementId id) => null;
=======
    public class Element { public ElementId Id { get; } = new ElementId(1); public void SetEntity(Autodesk.Revit.DB.ExtensibleStorage.Entity e) { } public Autodesk.Revit.DB.ExtensibleStorage.Entity GetEntity(Autodesk.Revit.DB.ExtensibleStorage.Schema s) => null; public string Name { get; set; } = ""; }
    public class ElementId { public ElementId(int v) { } public int IntegerValue => 0; }
    public class Document
     public CreationDocument Create { get; } = new CreationDocument();
    {
        public Element GetElement(ElementId id) => null;
        
         public class CreationDocument
    {
        public Autodesk.Revit.DB.Architecture.Room NewRoom(Level level, UV point) => new Autodesk.Revit.DB.Architecture.Room();
        public FamilyInstance NewFamilyInstance(XYZ p, FamilySymbol s, Level l, StructuralType st) => new FamilyInstance();
        public Wall NewWall(Line line, ElementId levelId, bool structural) => new Wall();
    }
    public class Wall : Element { public WallType WallType { get; } = new WallType(); public void ChangeTypeId(ElementId id) { } public static Wall Create(Document doc, Line line, ElementId levelId, bool structural) => new Wall(); }
>>>>>>> c31c12a (Update)
    }
    public class Wall : Element { public string Name { get; set; } = ""; public WallType WallType { get; } = new WallType(); }
    public class WallType : Element { public string Name { get; set; } = ""; }
    public class Level : Element { public string Name { get; set; } = ""; }
    public class Room : Element { public string Name { get; set; } = ""; public string Number { get; set; } = ""; }
    public class FamilyInstance : Element { public string Name { get; set; } = ""; public Category Category { get; } = new Category(); }
    public class Category { public ElementId Id { get; } = new ElementId(0); }
<<<<<<< HEAD
    public class FamilySymbol : Element { public bool IsActive { get; set; } }
=======
     public class FamilySymbol : Element { public bool IsActive { get; set; } public void Activate() { } }
>>>>>>> c31c12a (Update)
    public class Line { public static Line CreateBound(XYZ a, XYZ b) => new Line(); }
    public class XYZ { public static XYZ Zero => new XYZ(); public XYZ() { } public XYZ(double x,double y,double z) { } }
    public enum StructuralType { NonStructural }
    public class FilteredElementCollector { public FilteredElementCollector(Document d) { } public FilteredElementCollector OfClass(System.Type t) => this; public FilteredElementCollector OfCategory(BuiltInCategory c) => this; public Element FirstOrDefault(System.Func<Element, bool> f = null) => null; }
    public enum BuiltInCategory { OST_Doors }
    public class Transaction : System.IDisposable { public Transaction(Document doc,string name){ } public void Start() { } public void Commit() { } public void RollBack() { } public void Dispose() {} }
    public class TransactionGroup : System.IDisposable { public TransactionGroup(Document doc,string name){ } public void Start() { } public void Assimilate() { } public void Dispose() {} }
    public interface IUpdater { void Execute(UpdaterData data); string GetAdditionalInformation(); ChangePriority GetChangePriority(); UpdaterId GetUpdaterId(); bool IsApplicable(Element e); }
    public class UpdaterData { public Document GetDocument() => null; public System.Collections.Generic.ICollection<ElementId> GetModifiedElementIds() => null; }
    public class AddInId { }
    public class UpdaterId { public UpdaterId(AddInId a,System.Guid g) { } }
    public enum ChangePriority { Anything }
    public class Schema { public static Schema Lookup(System.Guid id)=>null; public Field GetField(string name)=>null; }
    public class Field { }
    public class SchemaBuilder { public SchemaBuilder(System.Guid id){ } public void AddSimpleField(string n,System.Type t){ } public void SetSchemaName(string n){ } public Schema Finish()=>null; }
    public class Entity { public Entity(Schema s){} public void Set(string f,string v){} public string Get<T>(Field f)=>default; public bool IsValid()=>false; }
    public static class WallCreation { }
    public static class DocumentExtensions
    { public static Wall Create(Document doc, Line line, ElementId levelId, bool structural) => new Wall();
<<<<<<< HEAD
      public static Room NewRoom(Document doc, Level level, UV point) => new Room();
=======
       public static Autodesk.Revit.DB.Architecture.Room NewRoom(Document doc, Level level, UV point) => new Autodesk.Revit.DB.Architecture.Room();
>>>>>>> c31c12a (Update)
      public static FamilyInstance NewFamilyInstance(Document doc, XYZ p, FamilySymbol s, Level l, StructuralType st) => new FamilyInstance(); }
    public class UV { public UV(double u,double v) { } }
    public class FailureMessageAccessor { public FailureSeverity GetSeverity()=>FailureSeverity.Warning; }
    public enum FailureSeverity { Warning, Error }
    public class FailuresAccessor { public System.Collections.Generic.IEnumerable<FailureMessageAccessor> GetFailureMessages() => new FailureMessageAccessor[0]; public void DeleteWarning(FailureMessageAccessor f){} }
    public enum FailureProcessingResult { Continue, ProceedWithRollBack }
<<<<<<< HEAD
    public interface IFailuresPreprocessor { FailureProcessingResult PreprocessFailures(FailuresAccessor accessor); }
}
namespace Autodesk.Revit.UI
{
=======
public interface IFailuresPreprocessor { FailureProcessingResult PreprocessFailures(FailuresAccessor accessor); }
    
    namespace Autodesk.Revit.DB.Architecture
{
    public class Room : Element { public string Name { get; set; } = ""; public string Number { get; set; } = ""; }
}
namespace Autodesk.Revit.DB.ExtensibleStorage
{
    public class Schema { public static Schema Lookup(System.Guid id)=>null; public Autodesk.Revit.DB.ExtensibleStorage.Field GetField(string name)=>null; }
    public class Field { }
    public class SchemaBuilder { public SchemaBuilder(System.Guid id) { } public void AddSimpleField(string n, System.Type t) { } public void SetSchemaName(string n) { } public Schema Finish() => null; }
    public class Entity { public Entity(Schema s) { } public void Set(string f, string v) { } public string Get<T>(Field f) => default; public bool IsValid() => false; }
}
namespace Autodesk.Revit.Attributes
{
    public enum TransactionMode { Manual, Automatic, ReadOnly }
    public class TransactionAttribute : System.Attribute { public TransactionAttribute(TransactionMode mode) { } }
}
}
namespace Autodesk.Revit.UI
   
{

     using Autodesk.Revit.DB;
>>>>>>> c31c12a (Update)
    public interface IExternalApplication { Result OnStartup(UIControlledApplication a); Result OnShutdown(UIControlledApplication a); }
    public interface IExternalCommand { Result Execute(ExternalCommandData data, ref string message, ElementSet elements); }
    public class Result { public static Result Succeeded = new Result(); public static Result Failed = new Result(); public static Result Cancelled = new Result(); }
    public class UIControlledApplication { public RibbonPanel CreateRibbonPanel(string name) => new RibbonPanel(); }
    public class RibbonPanel { public void AddItem(object d){} }
    public class PushButtonData { public PushButtonData(string name,string text,string assembly,string className){ } }
    public class UIApplication { public UIDocument ActiveUIDocument => null; }
    public class UIDocument { public Document Document => null; }
    public class ExternalEvent { public static ExternalEvent Create(IExternalEventHandler h)=>new ExternalEvent(); }
    public interface IExternalEventHandler { void Execute(UIApplication app); string GetName(); }
    public class ExternalCommandData { public UIApplication Application => null; }
    public class ElementSet { }
}
<<<<<<< HEAD
=======
namespace Autodesk.Revit.UI.Forms { public class OpenFileDialog { public bool Multiselect { get; set; } public string Filter { get; set; } public bool? ShowDialog() => true; public string[] GetFileNames() => new string[0]; } }
>>>>>>> c31c12a (Update)
