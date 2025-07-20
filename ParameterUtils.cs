using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace SpaceTracker;

#nullable enable

[SupportedOSPlatform("windows")]
public static class ParameterUtils
{
       /// <summary>
    /// Name of the shared parameter used to store the Neo4j UID on elements.
    /// </summary>
    public const string Neo4jUidParam = "Neo4jUid";

    /// <summary>
    /// Returns the Neo4j UID for the given element. If the custom parameter is
    /// not present, falls back to <see cref="Element.UniqueId"/>.
    /// </summary>
    public static string GetNeo4jUid(Element elem)
    {
        return elem.LookupParameter(Neo4jUidParam)?.AsString() ?? elem.UniqueId;
    }

    /// <summary>
    /// Writes the Neo4j UID to the element parameter if available.
    /// </summary>
    public static void SetNeo4jUid(Element elem, string uid)
    {
        var p = elem.LookupParameter(Neo4jUidParam);
        if (p != null && !p.IsReadOnly)
            p.Set(uid);
    }

    
    /// <summary>
    /// Searches the document for an element whose parameter <see cref="Neo4jUidParam"/>
    /// matches the provided uid.
    /// </summary>
    public static Element? FindElementByNeo4jUid(Document doc, string uid)
    {
        if (string.IsNullOrEmpty(uid))
            return null;

        return new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .FirstOrDefault(e => e.LookupParameter(Neo4jUidParam)?.AsString() == uid);
    }
    /// <summary>
    /// Escapes a string for safe usage inside Cypher queries.
    /// Removes backslashes and doubles single quotes.
    /// </summary>
    /// 
    /// 
    public static string EscapeForCypher(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;
        return input.Replace("\\", string.Empty)
                    .Replace("'", "''")
                    .Replace("\"", "'");
    }
    // Ersetzt problematische Zeichen in Parameternamen durch Unterstriche.

    private static string Sanitize(string name)
    {
        return name
                  .Replace(" ", "_")
                  .Replace("-", "_")
                  .Replace(".", "_")
                  .Replace(":", "_");
    }
    // Schreibt alle Parameter eines Elements in das Dictionary.

    public static void SerializeParameters(Element elem, Dictionary<string, object> dict)
    {
        try
        {
            foreach (Parameter p in elem.GetOrderedParameters())
            {
                if (p.Definition == null) continue;
                string key = "param_" + Sanitize(p.Definition.Name);
                object value = p.StorageType switch
                {
                    StorageType.Double => p.AsDouble(),
                    StorageType.Integer => p.AsInteger(),
                    StorageType.String => p.AsString() ?? string.Empty,
                    StorageType.ElementId => p.AsElementId().Value,
                    _ => p.AsValueString() ?? string.Empty
                };
                dict[key] = value;
            }
        }
        catch
        {
            // ignore serialization errors
        }
    }
    // Überträgt Werte aus dem Dictionary zurück auf die Revit-Parameter.

    public static void ApplyParameters(Element elem, Dictionary<string, object> dict)
    {
        try
        {
            foreach (Parameter p in elem.GetOrderedParameters())
            {
                if (p.IsReadOnly || p.Definition == null) continue;
                string key = "param_" + Sanitize(p.Definition.Name);
                if (!dict.TryGetValue(key, out object? value)) continue;
                try
                {
                    switch (p.StorageType)
                    {
                        case StorageType.Double:
                            if (value is double d || double.TryParse(value.ToString(), out d))
                                p.Set(d);
                            break;
                        case StorageType.Integer:
                            if (value is int i || int.TryParse(value.ToString(), out i))
                                p.Set(i);
                            break;
                        case StorageType.String:
                            p.Set(value?.ToString() ?? string.Empty);
                            break;
                        case StorageType.ElementId:
                            if (value is long l)
                                p.Set(new ElementId((int)l));
                            else if (int.TryParse(value.ToString(), out int id))
                                p.Set(new ElementId(id));
                            break;
                        default:
                            break;
                    }
                }
                catch
                {
                    // ignore if setting fails
                }
            }
        }
        catch
        {
            // ignore errors
        }
    }
    // Liefert die eingestellte IFC-Entität des Elements, falls vorhanden.

    public static string GetIfcEntity(Element elem)
    {
        try
        {
            var p = elem.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT);
            return p?.AsString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
    // Prüft anhand des Namens auf einen ProvisionalSpace.

    public static bool IsProvisionalSpaceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        string[] patterns =
        {
            "ProvSpace",
            "ProvisionalSpace",
            "Provisional Space",
            "ProvSpaceVoid"
        };

        foreach (var pat in patterns)
        {
            if (name.Contains(pat, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;

    }
    // Erkennt ProvisionalSpaces anhand mehrerer Kriterien.
    /// <summary>
    /// Determines whether the given element represents a provisional space.
    /// The main indicator is the IFC export type (parameter
    /// <see cref="BuiltInParameter.IFC_EXPORT_ELEMENT"/>), which must contain
    /// "ProvSpace". In addition the name and family are checked for the same
    /// keyword and the category must be GenericModel.
    /// </summary>

    public static bool IsProvisionalSpace(Element elem)
    {
        bool categoryMatch = elem.Category != null &&
(elem.Category.Id.Value == (int)BuiltInCategory.OST_GenericModel ||
                              elem.Category.Parent?.Id.Value == (int)BuiltInCategory.OST_GenericModel); bool nameMatch = IsProvisionalSpaceName(elem.Name);
        bool familyMatch = false;
        if (elem is FamilyInstance fi)
        {
            familyMatch = IsProvisionalSpaceName(fi.Symbol?.Name) ||
                          IsProvisionalSpaceName(fi.Symbol?.FamilyName);
        }
        string ifc = GetIfcEntity(elem);
        bool ifcMatch = !string.IsNullOrEmpty(ifc) &&
(ifc.Contains("provspace", StringComparison.OrdinalIgnoreCase) ||
             ifc.Equals("IfcBuildingElementProxyType", StringComparison.OrdinalIgnoreCase));
        bool paramMatch = false;
        Parameter? flag = elem.LookupParameter("IsProvisionalSpace") ?? elem.LookupParameter("ProvisionalSpace");
        if (flag != null)
        {
            paramMatch = flag.StorageType switch
            {
                StorageType.Integer => flag.AsInteger() == 1,
                StorageType.String => flag.AsString()?.Equals("Yes", StringComparison.OrdinalIgnoreCase) == true,
                _ => false
            };
        }
        return categoryMatch && (ifcMatch || nameMatch || familyMatch || paramMatch);
    }

    /// <summary>
    /// Variant of <see cref="IsProvisionalSpace(Element)"/> that works on a
    /// property dictionary as retrieved from Neo4j. The dictionary must contain
    /// the property <c>ifcType</c> used to flag provisional spaces.
    /// </summary>
    public static bool IsProvisionalSpace(IDictionary<string, object> props)
    {
        if (props == null)
            return false;

        bool categoryMatch = props.TryGetValue("category", out var catObj) &&
                            catObj is string cat &&
(cat.Equals("Generic Models", StringComparison.OrdinalIgnoreCase) ||
                             cat.Equals("Allgemeines Modell", StringComparison.OrdinalIgnoreCase));
        bool ifcMatch = props.TryGetValue("ifcType", out var ifcObj) &&
                       ifcObj is string ifcStr &&
 (ifcStr.Contains("provspace", StringComparison.OrdinalIgnoreCase) ||
                        ifcStr.Equals("IfcBuildingElementProxyType", StringComparison.OrdinalIgnoreCase));
        bool nameMatch = props.TryGetValue("name", out var nameObj) &&
                                 nameObj is string name &&
                                 IsProvisionalSpaceName(name);

        return categoryMatch && (ifcMatch || nameMatch);
    }
}