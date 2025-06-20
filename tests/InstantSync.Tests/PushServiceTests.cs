using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using InstantSync.Core.Delta;
using InstantSync.Core.Services;
using NSubstitute;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace InstantSync.Tests
{
    public class PushServiceTests
    {
        [Fact]
        public async Task FlushImmediately_WritesToRepositories()
        {
            var channel = Channel.CreateUnbounded<ElementDto>();
            var repo = Substitute.For<IRepository>();
            var service = new PushService(channel.Reader, new[] { repo }, NullLogger<PushService>.Instance, 10, "./");

            await channel.Writer.WriteAsync(new ElementDto { Guid = System.Guid.NewGuid(), Category = "Test" });
            await service.FlushImmediately(CancellationToken.None);

            await repo.Received().SaveAsync(Arg.Any<DeltaPackage>(), Arg.Any<CancellationToken>());
        }
    }
}
