using BuildingBlocks.Abstractions.Persistence;

namespace FoodDelivery.Services.Customers.Shared.Data.Extensions;

public static partial class WebApplicationExtensions
{
    public static async Task MigrateDatabases(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var migrationManager = scope.ServiceProvider.GetRequiredService<IMigrationManager>();

        await migrationManager.ExecuteAsync(CancellationToken.None);
    }
}
