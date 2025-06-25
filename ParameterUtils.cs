using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace SpaceTracker;

#nullable enable

[SupportedOSPlatform("windows")]
public static class ParameterUtils
{
        // Ersetzt problematische Zeichen in Parameternamen durch Unterstriche.

    private static string Sanitize(string name)
    {
        return name.Replace(" ", "_").Replace("-", "_").Replace(".", "_").Replace(":", "_");
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
            "Provisional Space"
        };

        foreach (var pat in patterns)
        {
            if (name.Contains(pat, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;

    }
        // Erkennt ProvisionalSpaces anhand mehrerer Kriterien.

    public static bool IsProvisionalSpace(Element elem)
    {
        if (IsProvisionalSpaceName(elem.Name))
            return true;

        if (elem is FamilyInstance fi)
        {
            if (IsProvisionalSpaceName(fi.Symbol?.Name) ||
                IsProvisionalSpaceName(fi.Symbol?.FamilyName))
                return true;
        }

        string ifc = GetIfcEntity(elem);
        if (!string.IsNullOrEmpty(ifc) &&
            ifc.Contains("provspace", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}