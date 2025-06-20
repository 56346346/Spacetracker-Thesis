namespace Autodesk.Revit.Attributes
{
    public enum TransactionMode { Manual, Automatic }
    public class TransactionAttribute : System.Attribute
    {
        public TransactionAttribute(TransactionMode mode) { }
    }
}