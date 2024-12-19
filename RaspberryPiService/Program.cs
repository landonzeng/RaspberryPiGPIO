using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace RaspberryPiService;

public class Program
{
    public static void Main(string[] args)
    {
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseSerilog((context, configuration) =>
            {
                configuration
                    .ReadFrom.Configuration(context.Configuration)
                    .WriteTo.Async(x => x.Console())
                    .MinimumLevel.Debug();
            })
            .ConfigureServices((hostContext, services) =>
            {
                var configuration = hostContext.Configuration;
                services.AddHostedService<GpioService>();
                services.AddSingleton(configuration);
            });
}
