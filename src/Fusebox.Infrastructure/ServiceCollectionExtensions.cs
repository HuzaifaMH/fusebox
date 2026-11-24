using Fusebox.Application.Admin;
using Fusebox.Application.Alerting;
using Fusebox.Application.Budgeting;
using Fusebox.Application.Detection;
using Fusebox.Application.Diagnosis;
using Fusebox.Application.Llm;
using Fusebox.Application.Pricing;
using Fusebox.Application.Reconciliation;
using Fusebox.Application.Telemetry;
using Fusebox.Application.Tenancy;
using Fusebox.Application.Upstream;
using Fusebox.Infrastructure.Alerting;
using Fusebox.Infrastructure.Alerting.Notifiers;
using Fusebox.Infrastructure.Alerting.Postgres;
using Fusebox.Infrastructure.Catalog;
using Fusebox.Infrastructure.Catalog.Postgres;
using Fusebox.Infrastructure.Detection;
using Fusebox.Infrastructure.Detection.Postgres;
using Fusebox.Infrastructure.Diagnosis;
using Fusebox.Infrastructure.Diagnosis.Postgres;
using Fusebox.Infrastructure.Enforcement;
using Fusebox.Infrastructure.Llm;
using Fusebox.Infrastructure.Redis;
using Fusebox.Infrastructure.Security;
using Fusebox.Infrastructure.Telemetry;
using Fusebox.Infrastructure.Telemetry.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Fusebox.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the implementations of the ports declared by the Application layer.</summary>
    public static IServiceCollection AddFuseboxInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CatalogOptions>()
            .Bind(configuration.GetSection(CatalogOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CatalogOptions>, CatalogOptionsValidator>();

        services.AddOptions<EnforcementOptions>()
            .Bind(configuration.GetSection(EnforcementOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<EnforcementOptions>, EnforcementOptionsValidator>();

        services.AddOptions<DegradedModeOptions>()
            .Bind(configuration.GetSection(DegradedModeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<CatalogSourceOptions>()
            .Bind(configuration.GetSection(CatalogSourceOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CatalogSourceOptions>, CatalogSourceOptionsValidator>();

        services.AddSingleton<IApiKeyHasher, Sha256ApiKeyHasher>();
        services.AddSingleton<ILlmProtocol, OpenAiChatProtocol>();

        // The gateway serves tenants and prices from an in-memory snapshot that the refresher keeps current.
        services.AddSingleton<CatalogSnapshotProvider>();
        services.AddSingleton<ITenantDirectory, SnapshotTenantDirectory>();
        services.AddSingleton<IPriceCatalog, SnapshotPriceCatalog>();
        services.AddSingleton<ICatalogSource>(CreateCatalogSource);
        services.AddSingleton<ICatalogCache>(CreateCatalogCache);
        services.AddSingleton<CatalogRefresher>();
        services.AddHostedService(provider => provider.GetRequiredService<CatalogRefresher>());
        services.AddSingleton<ICatalogReload>(provider => provider.GetRequiredService<CatalogRefresher>());
        services.AddSingleton<ICatalogAdminStore>(CreateCatalogAdminStore);

        services.AddSingleton<IConnectionMultiplexer>(CreateRedisConnection);
        services.AddSingleton<IEnforcementStore>(CreateEnforcementStore);
        services.AddSingleton<IEnforcementStatus>(provider =>
            provider.GetRequiredService<IEnforcementStore>() as IEnforcementStatus ?? new AlwaysHealthyStatus());

        AddTelemetry(services, configuration);
        AddDetection(services, configuration);
        AddDiagnosis(services, configuration);
        AddAlerting(services, configuration);

        return services;
    }

    // Requests hand their record to an in-memory queue. A writer saves it in batches and a maintenance job totals it,
    // both in the background, so recording never adds latency to a call.
    private static void AddTelemetry(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TelemetryOptions>()
            .Bind(configuration.GetSection(TelemetryOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<TelemetryOptions>, TelemetryOptionsValidator>();

        services.AddSingleton<TelemetryMetrics>();
        services.AddSingleton<ChannelTelemetrySink>();
        services.AddSingleton<ITelemetrySink>(CreateTelemetrySink);
        services.AddSingleton<ITelemetryStore>(CreateTelemetryStore);
        services.AddHostedService<TelemetryWriter>();
        services.AddHostedService<TelemetryMaintenance>();
    }

    // Anomaly detection reads the per-minute totals telemetry keeps and records what it finds. It is off until switched on.
    private static void AddDetection(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DetectionOptions>()
            .Bind(configuration.GetSection(DetectionOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DetectionOptions>, DetectionOptionsValidator>();

        services.AddSingleton<IAnomalyStore>(CreateAnomalyStore);
        services.AddHostedService<AnomalyDetectionJob>();
    }

    private static IAnomalyStore CreateAnomalyStore(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<DetectionOptions>>().Value.Enabled
        && provider.GetRequiredService<IOptions<TelemetryOptions>>().Value.Store == TelemetryStoreKind.Postgres
            ? ActivatorUtilities.CreateInstance<PostgresAnomalyStore>(provider)
            : new NullAnomalyStore();

    // Diagnosis explains each anomaly the detector finds: arithmetic finds what changed, and a template or a checked LLM
    // puts it into words. It is off until switched on, and the LLM is a further switch.
    private static void AddDiagnosis(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DiagnosisOptions>()
            .Bind(configuration.GetSection(DiagnosisOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<LlmOptions>().Bind(configuration.GetSection(LlmOptions.SectionName));
        services.AddSingleton<IValidateOptions<DiagnosisOptions>, DiagnosisOptionsValidator>();

        services.AddSingleton<ILlmClient, OpenAiCompatibleLlmClient>();
        services.AddSingleton<PostgresDiagnosisStore>(provider => ActivatorUtilities.CreateInstance<PostgresDiagnosisStore>(provider));
        services.AddSingleton<IDiagnosisStore>(CreateDiagnosisStore);
        services.AddSingleton<IEvidenceSource>(provider => (IEvidenceSource)provider.GetRequiredService<IDiagnosisStore>());
        services.AddHostedService<DiagnosisJob>();
    }

    private static IDiagnosisStore CreateDiagnosisStore(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<DiagnosisOptions>>().Value.Enabled
        && provider.GetRequiredService<IOptions<TelemetryOptions>>().Value.Store == TelemetryStoreKind.Postgres
            ? provider.GetRequiredService<PostgresDiagnosisStore>()
            : new NullDiagnosisStore();

    // Alerts are written to PostgreSQL first and sent after, so one that is raised is never lost. Critical ones page through
    // PagerDuty, the rest go to Slack, and a heartbeat tells an outside monitor that Fusebox is alive. Off until switched on.
    private static void AddAlerting(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AlertOptions>()
            .Bind(configuration.GetSection(AlertOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AlertOptions>, AlertOptionsValidator>();

        services.AddSingleton<AlertMetrics>();
        services.AddSingleton<NotifierCatalog>();
        services.AddSingleton<IEnumerable<INotifier>>(provider => provider.GetRequiredService<NotifierCatalog>().Notifiers);

        services.AddSingleton<PostgresAlertStore>(provider => ActivatorUtilities.CreateInstance<PostgresAlertStore>(provider));
        services.AddSingleton<IAlertStore>(CreateAlertStore);
        services.AddSingleton<IUsageCostReader>(CreateUsageCostReader);
        services.AddSingleton<IAlertFeed>(provider => (IAlertFeed)provider.GetRequiredService<IAlertStore>());

        services.AddSingleton<AlertInbox>();
        services.AddSingleton<IAlertInbox>(provider => provider.GetRequiredService<AlertInbox>());
        services.AddSingleton<IEnforcementOutageListener, AlertingOutageListener>();
        services.AddSingleton<IUpstreamHealthListener, AlertingUpstreamListener>();

        services.AddSingleton<HeartbeatSender>(provider => new HeartbeatSender(
            provider.GetRequiredService<NotifierCatalog>().Client,
            provider.GetRequiredService<AlertDispatcher>(),
            provider.GetRequiredService<IOptions<AlertOptions>>(),
            provider.GetRequiredService<ILogger<HeartbeatSender>>()));

        services.AddHostedService<AlertInboxWriter>();
        services.AddHostedService<AlertSourceJob>();
        services.AddHostedService<AlertDispatcherJob>();
        services.AddHostedService<HeartbeatJob>();
    }

    // Only reconciliation asks for this, and it needs the per-hour totals that the Postgres telemetry store keeps.
    private static IUsageCostReader CreateUsageCostReader(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<TelemetryOptions>>().Value.Store == TelemetryStoreKind.Postgres
            ? ActivatorUtilities.CreateInstance<PostgresUsageCostReader>(provider)
            : throw new InvalidOperationException("Reconciliation compares against recorded totals, so it needs Fusebox:Telemetry:Store to be Postgres.");

    private static IAlertStore CreateAlertStore(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<TelemetryOptions>>().Value.Store == TelemetryStoreKind.Postgres
            ? provider.GetRequiredService<PostgresAlertStore>()
            : new NullAlertStore();

    private static ITelemetrySink CreateTelemetrySink(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<TelemetryOptions>>().Value.Store == TelemetryStoreKind.Postgres
            ? provider.GetRequiredService<ChannelTelemetrySink>()
            : NullTelemetrySink.Instance;

    private static ITelemetryStore CreateTelemetryStore(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<TelemetryOptions>>().Value.Store == TelemetryStoreKind.Postgres
            ? ActivatorUtilities.CreateInstance<PostgresTelemetryStore>(provider)
            : new NullTelemetryStore();

    // Only the admin API asks for this, and it refuses to be switched on without a Postgres catalog.
    private static ICatalogAdminStore CreateCatalogAdminStore(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<CatalogSourceOptions>>().Value.Kind == CatalogSourceKind.Postgres
            ? ActivatorUtilities.CreateInstance<PostgresCatalogAdminStore>(provider)
            : throw new InvalidOperationException("Tenants and prices can only be managed when Fusebox:CatalogSource:Kind is Postgres.");

    private static ICatalogSource CreateCatalogSource(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<CatalogSourceOptions>>().Value.Kind == CatalogSourceKind.Postgres
            ? ActivatorUtilities.CreateInstance<PostgresCatalogSource>(provider)
            : ActivatorUtilities.CreateInstance<ConfigurationCatalogSource>(provider);

    // The catalog is shared through Redis when Redis is the enforcement store. Otherwise there is only this instance.
    private static ICatalogCache CreateCatalogCache(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<EnforcementOptions>>().Value.Store == EnforcementStoreKind.Redis
            ? ActivatorUtilities.CreateInstance<RedisCatalogCache>(provider)
            : new NoCatalogCache();

    private static IEnforcementStore CreateEnforcementStore(IServiceProvider provider)
    {
        if (provider.GetRequiredService<IOptions<EnforcementOptions>>().Value.Store != EnforcementStoreKind.Redis)
        {
            var single = ActivatorUtilities.CreateInstance<InMemoryEnforcementStore>(provider);
            single.WarnNotShared();
            return single;
        }

        // Redis is the shared ledger. If it goes away the gateway degrades to a private in-memory limit.
        return new DegradedEnforcementStore(
            ActivatorUtilities.CreateInstance<RedisEnforcementStore>(provider),
            ActivatorUtilities.CreateInstance<InMemoryEnforcementStore>(provider),
            provider.GetRequiredService<IOptions<DegradedModeOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetServices<IEnforcementOutageListener>(),
            provider.GetRequiredService<ILogger<DegradedEnforcementStore>>());
    }

    // Resolved only when the Redis store is selected. The gateway starts even if Redis is briefly
    // unreachable and reconnects in the background; commands fail fast until it does.
    private static ConnectionMultiplexer CreateRedisConnection(IServiceProvider provider)
    {
        var settings = provider.GetRequiredService<IOptions<EnforcementOptions>>().Value.Redis;
        var configuration = ConfigurationOptions.Parse(settings.ConnectionString);

        configuration.AbortOnConnectFail = false;
        configuration.AsyncTimeout = settings.CommandTimeoutMilliseconds;
        configuration.SyncTimeout = settings.CommandTimeoutMilliseconds;

        return ConnectionMultiplexer.Connect(configuration);
    }
}
