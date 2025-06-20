using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using InstantSync.Core.Delta;

namespace InstantSync.Core.Services
{
    /// <summary>
    /// Background service that persists deltas.
    /// </summary>
    public class PushService : BackgroundService
    {
        private readonly ChannelReader<ElementDto> _reader;
        private readonly IEnumerable<IRepository> _repositories;
        private readonly ILogger<PushService> _logger;
        private readonly int _batchSize;
        private readonly string _jsonPath;

        /// <summary>
        /// Initializes a new instance of the <see cref="PushService"/> class.
        /// </summary>
        /// <param name="reader">Channel reader.</param>
        /// <param name="repositories">Repositories.</param>
        /// <param name="logger">Logger.</param>
        /// <param name="batchSize">Batch size.</param>
        /// <param name="jsonPath">Path for JSON files.</param>
        public PushService(ChannelReader<ElementDto> reader, IEnumerable<IRepository> repositories, ILogger<PushService> logger, int batchSize, string jsonPath)
        {
            _reader = reader;
            _repositories = repositories;
            _logger = logger;
            _batchSize = batchSize;
            _jsonPath = jsonPath;
        }

        /// <summary>
        /// Flushes the channel immediately.
        /// </summary>
        public async Task FlushImmediately(CancellationToken ct)
        {
            await ProcessBatchAsync(ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ProcessBatchAsync(stoppingToken).ConfigureAwait(false);
            }
        }

        private async Task ProcessBatchAsync(CancellationToken ct)
        {
            var batch = new List<ElementDto>();
            while (batch.Count < _batchSize && await _reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_reader.TryRead(out var item))
                {
                    batch.Add(item);
                    if (batch.Count >= _batchSize)
                    {
                        break;
                    }
                }
            }

            if (batch.Count == 0)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                return;
            }

            var pkg = new DeltaPackage { Elements = batch };
            string json = JsonSerializer.Serialize(pkg);
            Directory.CreateDirectory(_jsonPath);
            string file = Path.Combine(_jsonPath, $"{pkg.PackageId}.json");
            await File.WriteAllTextAsync(file, json, ct).ConfigureAwait(false);

            File.WriteAllText(file, json);

            using (_logger.BeginScope(pkg.PackageId))
            {
                foreach (var repo in _repositories)
                {
                    await repo.SaveAsync(pkg, ct).ConfigureAwait(false);
                }

                _logger.LogInformation("Persisted delta package with {Count} elements", batch.Count);
            }
        }
    }
}
