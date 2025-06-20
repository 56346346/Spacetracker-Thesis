using System;
using Newtonsoft.Json.Linq;

namespace InstantSync.Core.Delta
{
    /// <summary>
    /// Represents a Revit element delta.
    /// </summary>
    public record ElementDto
    {
        /// <summary>
        /// Gets or sets the unique identifier for this element.
        /// </summary>
        public Guid Guid { get; init; }

        /// <summary>
        /// Gets or sets the element category.
        /// </summary>
        public string Category { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets the element data (parameters and geometry).
        /// </summary>
        public JObject Data { get; init; } = new JObject();
    }
}
