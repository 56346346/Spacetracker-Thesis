using System.Threading;
using System.Threading.Tasks;

namespace InstantSync.Core.Delta
{
    /// <summary>
    /// Repository for persisting delta packages.
    /// </summary>
    public interface IRepository
    {
        /// <summary>
        /// Saves a delta package asynchronously.
        /// </summary>
        /// <param name="pkg">The package.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task representing the operation.</returns>
        Task SaveAsync(DeltaPackage pkg, CancellationToken ct);
    }
}
