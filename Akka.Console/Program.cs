﻿using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Akka.Console
{
	public class Program
	{
		public static async Task Main(string[] args)
		{
			var host = new HostBuilder()
				.ConfigureServices((hostContext, services) =>
				{
					services.AddLogging();
					services.AddHostedService<AkkaService>();
				})
				.ConfigureLogging((hostContext, configLogging) => { configLogging.AddConsole(); })
				.UseConsoleLifetime()
				.Build();

			await host.RunAsync();
		}
	}
}