using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Abstractions;
using NKeeper.Plugins.Databases;

namespace NKeeper.Plugins.SqlServer;

/// <summary>
/// The mssql plugin (docs/architecture.md §7.33): the <c>mssql</c> service type and its database tab, which db-core runs
/// and draws (§7.32). Core opens the connections and signs in (§7.31); this plugin never sees the password.
/// </summary>
public sealed class SqlServerPlugin : IPlugin
{
    public const string ServiceType = "mssql";

    public const string SessionType = "mssql";

    public void Contribute(IPluginContributions contributions, PluginContext context)
    {
        contributions.AddServiceType(new ServiceType(ServiceType, "SQL Server") { DefaultPort = 1433, Health = HealthProbe.Tcp });
        contributions.AddSessionType(new SessionType(SessionType, "SQL Server", typeof(DatabaseSessionHandler<SqlServerEngine>)) { ServiceTypes = [ServiceType] });
        contributions.AddHostAction(new HostAction("mssql.query", "SQL Server", SessionType: SessionType));
    }

    public void ConfigureServices(IServiceCollection services, PluginContext context) => services.AddDatabaseEngine<SqlServerEngine>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
    }
}
