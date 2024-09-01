using BuildingBlocks.Abstractions.Events;
using BuildingBlocks.Abstractions.Persistence;
using BuildingBlocks.Core.Extensions;
using BuildingBlocks.Persistence.EfCore.Postgres;
using BuildingBlocks.Persistence.Mongo.Extensions;
using FoodDelivery.Services.Catalogs.Shared.Contracts;
using FoodDelivery.Services.Catalogs.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FoodDelivery.Services.Catalogs.Shared.Extensions.WebApplicationBuilderExtensions;

public static partial class WebApplicationBuilderExtensions
{
    public static WebApplicationBuilder AddStorage(this WebApplicationBuilder builder)
    {
        AddPostgresWriteStorage(builder.Services, builder.Configuration);
        AddMongoReadStorage(builder.Services, builder.Configuration);

        return builder;
    }

    private static void AddPostgresWriteStorage(IServiceCollection services, IConfiguration configuration)
    {
        var option = configuration.BindOptions<PostgresOptions>();
        if (option.UseInMemory)
        {
            services.AddDbContext<CatalogDbContext>(options =>
                options.UseInMemoryDatabase("FoodDelivery.Services.Catalogs")
            );

            services.TryAddScoped<IDbFacadeResolver>(provider => provider.GetService<CatalogDbContext>()!);
            services.TryAddScoped<IDomainEventContext>(provider => provider.GetService<CatalogDbContext>()!);
        }
        else
        {
            services.AddPostgresDbContext<CatalogDbContext>(configuration);

            services.TryAddScoped<IMigrationExecutor, CatalogsMigrationExecutor>();
        }

        services.TryAddScoped<ICatalogDbContext>(provider => provider.GetRequiredService<CatalogDbContext>());
    }

    private static void AddMongoReadStorage(IServiceCollection services, IConfiguration configuration)
    {
        services.AddMongoDbContext<CatalogReadDbContext>();
    }
}
