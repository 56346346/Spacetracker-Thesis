using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace SpaceTracker;

#nullable enable

[SupportedOSPlatform("windows")]
public static class ParameterUtils
{
    private static string Sanitize(string name)
    {
        return name.Replace(" ", "_").Replace("-", "_").Replace(".", "_").Replace(":", "_");
    }

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
}