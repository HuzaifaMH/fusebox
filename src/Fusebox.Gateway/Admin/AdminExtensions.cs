using Microsoft.Extensions.Options;

namespace Fusebox.Gateway.Admin;

internal static class AdminExtensions
{
    public static IServiceCollection AddFuseboxAdmin(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AdminOptions>()
            .Bind(configuration.GetSection(AdminOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AdminOptions>, AdminOptionsValidator>();

        services.AddOpenApi("admin", options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "Fusebox admin API";
                document.Info.Description = "Manage tenants, budgets, keys and prices, and read anomalies and alerts. Send the admin key in the X-Admin-Key header.";
                return Task.CompletedTask;
            });
        });

        return services;
    }

    /// <summary>Maps the admin routes, the API description and Swagger UI, but only when the admin API is switched on.</summary>
    public static WebApplication UseFuseboxAdmin(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!app.Services.GetRequiredService<IOptions<AdminOptions>>().Value.Enabled)
        {
            return app;
        }

        app.MapFuseboxAdmin();
        app.MapOpenApi("/admin/openapi/{documentName}.json");
        app.UseSwaggerUI(ui =>
        {
            ui.RoutePrefix = "admin/swagger";
            ui.DocumentTitle = "Fusebox admin API";
            ui.SwaggerEndpoint("/admin/openapi/admin.json", "Fusebox admin API");
        });

        return app;
    }
}
