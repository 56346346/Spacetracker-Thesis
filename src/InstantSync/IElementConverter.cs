using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace InstantSync.Core.Delta
{
    /// <summary>
    /// Converter interface for translating between Revit elements and DTOs.
    /// </summary>
    /// <typeparam name="TDto">DTO type.</typeparam>
    public interface IElementConverter<TDto>
    {
        /// <summary>
        /// Determines whether the converter can convert the specified element.
        /// </summary>
        /// <param name="element">The Revit element.</param>
        /// <returns>True if conversion is supported.</returns>
        bool CanConvert(Element element);

        /// <summary>
        /// Converts an element to DTO.
        /// </summary>
        /// <param name="element">Element to convert.</param>
        /// <param name="doc">Current document.</param>
        /// <returns>The DTO.</returns>
        TDto ToDto(Element element, Document doc);

        /// <summary>
        /// Creates or updates an element from the DTO.
        /// </summary>
        /// <param name="dto">Source DTO.</param>
        /// <param name="doc">Destination document.</param>
        /// <param name="idMap">Map of referenced element GUIDs to ElementIds.</param>
        /// <returns>The created or updated element.</returns>
        Element? FromDto(TDto dto, Document doc, IDictionary<Guid, ElementId> idMap);
    }
}
