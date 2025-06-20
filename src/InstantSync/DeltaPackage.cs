using System;
using System.Collections.Generic;

namespace InstantSync.Core.Delta
{
    /// <summary>
    /// Represents a batch of element deltas.
    /// </summary>
    public record DeltaPackage
    {
        /// <summary>
        /// Gets or initializes the package identifier.
        /// </summary>
        public Guid PackageId { get; init; } = Guid.NewGuid();

        /// <summary>
        /// Gets the collection of element deltas.
        /// </summary>
        public IList<ElementDto> Elements { get; init; } = new List<ElementDto>();
    }
}
