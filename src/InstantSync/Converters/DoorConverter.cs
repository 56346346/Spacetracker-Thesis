using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using InstantSync.Core.Delta;
using Newtonsoft.Json.Linq;

namespace InstantSync.Core.Converters
{
    /// <summary>
    /// Converter for door elements.
    /// </summary>
    public class DoorConverter : IElementConverter<ElementDto>
    {
        /// <inheritdoc />
        public bool CanConvert(Element element) => element is FamilyInstance fi && fi.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Doors;

        /// <inheritdoc />
        public ElementDto ToDto(Element element, Document doc)
        {
            FamilyInstance fi = (FamilyInstance)element;
            var obj = new JObject
            {
                ["Name"] = fi.Name,
            };
            return new ElementDto { Category = "Door", Data = obj };
        }

        /// <inheritdoc />
        public Element? FromDto(ElementDto dto, Document doc, IDictionary<Guid, ElementId> idMap)
        {
            FamilyInstance? fi = null;
            if (idMap.TryGetValue(dto.Guid, out var existingId))
            {
                fi = doc.GetElement(existingId) as FamilyInstance;
            }

            if (fi == null)
            {
                var symbol = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .FirstOrDefault() as FamilySymbol;
                var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstOrDefault() as Level;
                if (symbol != null && level != null)
                {
                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                    }

                    fi = doc.Create.NewFamilyInstance(XYZ.Zero, symbol, level, StructuralType.NonStructural);
                    idMap[dto.Guid] = fi.Id;
                }
            }

            return fi;
        }
    }
}
