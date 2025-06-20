using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using ArchitectureRoom = Autodesk.Revit.DB.Architecture.Room;

using InstantSync.Core.Delta;
using Newtonsoft.Json.Linq;

namespace InstantSync.Core.Converters
{
    /// <summary>
    /// Converter for room elements.
    /// </summary>
    public class RoomConverter : IElementConverter<ElementDto>
    {
        /// <inheritdoc />
        public bool CanConvert(Element element) => element is ArchitectureRoom;

        /// <inheritdoc />
        public ElementDto ToDto(Element element, Document doc)
        {
            ArchitectureRoom room = (ArchitectureRoom)element;
            var obj = new JObject
            {
                ["Name"] = room.Name,
                ["Number"] = room.Number,
            };
            return new ElementDto { Category = "Room", Data = obj };
        }

        /// <inheritdoc />
        public Element? FromDto(ElementDto dto, Document doc, IDictionary<Guid, ElementId> idMap)
        {
            ArchitectureRoom? room = null;
            if (idMap.TryGetValue(dto.Guid, out var existingId))
            {
                room = doc.GetElement(existingId) as ArchitectureRoom;
            }

            if (room == null)
            {
                var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstOrDefault() as Level;
                if (level == null)
                {
                    return null;
                }

                room = doc.Create.NewRoom(level, new UV(0, 0));
                idMap[dto.Guid] = room.Id;
            }

            if (dto.Data.TryGetValue("Name", out var name))
            {
                room.Name = name.ToString();
            }
            if (dto.Data.TryGetValue("Number", out var number))
            {
                room.Number = number.ToString();
            }

            return room;
        }
    }
}
