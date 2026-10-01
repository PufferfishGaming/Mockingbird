using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Formatting.Compact;
using TriAsr.Application;

namespace TriAsr.Infrastructure;

public static class ServiceRegistration
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string dataRoot)
    {
        var paths = new StoragePaths(dataRoot);
        paths.EnsureDirectories();
        services.AddSingleton<IStoragePaths>(paths);
        services.AddSingleton<ActivityFeed>();
        services.AddSingleton<InteractiveTerminal>();
        services.AddSerilog((provider, configuration) => configuration
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Sink(provider.GetRequiredService<ActivityFeed>())
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(paths.Logs, "triasr-.clef"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                shared: true, fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true), preserveStaticLogger: true);
        return services;
    }
}
