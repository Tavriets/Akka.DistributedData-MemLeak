using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Microsoft.Extensions.Hosting;
using System.Threading;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Net.NetworkInformation;
using Akka.DistributedData;
using Microsoft.Extensions.Logging;

namespace Akka.Console
{
	/// <summary>
	/// <see cref="IHostedService"/> that runs and manages <see cref="ActorSystem"/> in background of application.
	/// </summary>
	public class AkkaService : BackgroundService
	{
		private ActorSystem ClusterSystem;
		private readonly IServiceProvider serviceProvider;

		private readonly ILogger<AkkaService> logger;
		private static readonly IWriteConsistency wConsistency = new WriteAll(TimeSpan.FromSeconds(5));
		private IActorRef replicator;
		private Cluster.Cluster cluster;
		private LWWRegister<byte[]> initialRegister;
		private readonly Random random = new Random(Guid.NewGuid().GetHashCode());
		private const int seedPort = 4055;

		public AkkaService(IServiceProvider serviceProvider, ILogger<AkkaService> logger)
		{
			this.serviceProvider = serviceProvider;
			this.logger = logger;
		}

		public override async Task StartAsync(CancellationToken cancellationToken)
		{
			var port = IsAvailable(seedPort) ? seedPort : 0;
			var config = ConfigurationFactory.ParseString(@"
akka {
	log-dead-letters = off

	actor {
		provider = cluster
	}

	remote {
		dot-netty.tcp {
			public-hostname = localhost
			hostname = 0.0.0.0
			port = "+port+@"
			enable-pooling = false

			send-buffer-size = 2560000b # Def: 256000b
			receive-buffer-size = 2560000b # Def: 256000b
			maximum-frame-size = 1280000b
		}
	}

	cluster {
		downing-provider-class = ""Akka.Cluster.SplitBrainResolver, Akka.Cluster""
		split-brain-resolver {
			active-strategy = keep-majority
		}

		seed-nodes = [""akka.tcp://ClusterSys@localhost:"+seedPort+@"""]
		roles = []
	}
}
");
			var bootstrap = BootstrapSetup.Create().WithConfig(config);

			ClusterSystem = ActorSystem.Create("ClusterSys", bootstrap);

			replicator = DistributedData.DistributedData.Get(ClusterSystem).Replicator;
			cluster = Cluster.Cluster.Get(ClusterSystem);
			initialRegister = new LWWRegister<byte[]>(cluster.SelfUniqueAddress, Array.Empty<byte>());

			await base.StartAsync(cancellationToken);
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			while (!stoppingToken.IsCancellationRequested)
			{
				logger.LogInformation("Working...");
				for (var i = 0; i < 25; i++)
				{
					var key = "abyr-" + i;
					try
					{
						var ddKey = GetDdKey(key);

						var data = new byte[5120];
						random.NextBytes(data);

						replicator.Tell(Dsl.Update(ddKey, initialRegister, wConsistency, old =>
						{
							var res = old.WithValue(cluster.SelfUniqueAddress, data);
							return res;
						}));
					}
					catch (Exception e)
					{
						logger.LogError(e, e.Message);
						throw;
					}
				}

				await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
			}
		}

		public override async Task StopAsync(CancellationToken cancellationToken)
		{
			await base.StopAsync(cancellationToken);
			// strictly speaking this may not be necessary - terminating the ActorSystem would also work
			// but this call guarantees that the shutdown of the cluster is graceful regardless
			await CoordinatedShutdown.Get(ClusterSystem).Run(CoordinatedShutdown.ClrExitReason.Instance);
		}

		private static LWWRegisterKey<byte[]> GetDdKey(string key)
		{
			return new LWWRegisterKey<byte[]>($"dd:[{key}]");
		}

		private static bool IsAvailable(ushort port)
		{
			var ipProperties = IPGlobalProperties.GetIPGlobalProperties();
			var usedPorts = Enumerable.Empty<int>()
				.Concat(ipProperties.GetActiveTcpConnections().Select(c => c.LocalEndPoint.Port))
				.Concat(ipProperties.GetActiveTcpListeners().Select(l => l.Port))
				.Concat(ipProperties.GetActiveUdpListeners().Select(l => l.Port))
				.Select(p => (ushort)p)
				.ToImmutableHashSet();
			return !usedPorts.Contains(port);
		}
	}
}