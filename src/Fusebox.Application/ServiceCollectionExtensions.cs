using Fusebox.Application.Alerting;
using Fusebox.Application.Budgeting;
using Fusebox.Application.Detection;
using Fusebox.Application.Diagnosis;
using Fusebox.Application.Llm;
using Fusebox.Application.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fusebox.Application;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the use cases. The ports they depend on are registered by the outer layers.</summary>
    public static IServiceCollection AddFuseboxApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<BudgetGuardOptions>();

        // Outage events are always logged. Other listeners, such as alerting, are registered alongside.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEnforcementOutageListener, LoggingOutageListener>());

        services.AddSingleton<ITenantAuthenticator, TenantAuthenticator>();
        services.AddSingleton<IBudgetGuard, BudgetGuard>();
        services.AddSingleton<LlmProtocolCatalog>();
        services.AddSingleton<AnomalyDetector>();
        services.AddSingleton<Fusebox.Application.Admin.CatalogAdminService>();
        services.AddSingleton<Fusebox.Application.Reconciliation.ReconciliationService>();
        services.AddSingleton<NarrationService>();
        services.AddSingleton<DiagnosisRunner>();
        services.AddSingleton<AlertRouter>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<AlertDispatcher>();
        services.AddSingleton<AlertSourceRunner>();

        return services;
    }
}
