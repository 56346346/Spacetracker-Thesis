using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using InstantSync.Core.Delta;
using Newtonsoft.Json.Linq;

namespace InstantSync.Core.Converters
{
    /// <summary>
    /// Converter for wall elements.
    /// </summary>
    public class WallConverter : IElementConverter<ElementDto>
    {
        /// <inheritdoc />
        public bool CanConvert(Element element) => element is Wall;

        /// <inheritdoc />
        public ElementDto ToDto(Element element, Document doc)
        {
            Wall wall = (Wall)element;
            var obj = new JObject
            {
                ["Name"] = wall.Name,
                ["WallType"] = wall.WallType.Name,
            };
            return new ElementDto { Category = "Wall", Data = obj };
        }

        /// <inheritdoc />
        public Element? FromDto(ElementDto dto, Document doc, IDictionary<Guid, ElementId> idMap)
        {
            Wall? wall = null;
            if (idMap.TryGetValue(dto.Guid, out var existingId))
            {
                wall = doc.GetElement(existingId) as Wall;
            }

            if (wall == null)
            {
                var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstOrDefault() as Level;
                wall = Wall.Create(doc, Line.CreateBound(XYZ.Zero, new XYZ(1, 0, 0)), level.Id, false);
                idMap[dto.Guid] = wall.Id;
            }

            if (dto.Data.TryGetValue("WallType", out var typeToken))
            {
                var wallType = new FilteredElementCollector(doc).OfClass(typeof(WallType))
                    .FirstOrDefault(e => e.Name == typeToken.ToString()) as WallType;
                if (wallType != null)
                {
                    wall.ChangeTypeId(wallType.Id);
                }
            }

            return wall;
        }
    }
}
