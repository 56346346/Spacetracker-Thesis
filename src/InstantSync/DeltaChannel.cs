using System.Threading.Channels;

namespace InstantSync.Core.Delta
{
    /// <summary>
    /// Provides access to the delta channel.
    /// </summary>
    public static class DeltaChannel
    {
        /// <summary>
        /// Gets the shared channel instance.
        /// </summary>
        public static Channel<ElementDto> Instance { get; } = Channel.CreateUnbounded<ElementDto>();
    }
}
