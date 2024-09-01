using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using AutoBogus;
using BuildingBlocks.Abstractions.Commands;
using BuildingBlocks.Abstractions.Events;
using BuildingBlocks.Abstractions.Messaging;
using BuildingBlocks.Abstractions.Messaging.PersistMessage;
using BuildingBlocks.Abstractions.Queries;
using BuildingBlocks.Core.Events;
using BuildingBlocks.Core.Extensions;
using BuildingBlocks.Core.Messaging.MessagePersistence;
using BuildingBlocks.Core.Persistence.Extensions;
using BuildingBlocks.Core.Types;
using BuildingBlocks.Integration.MassTransit;
using BuildingBlocks.Persistence.EfCore.Postgres;
using BuildingBlocks.Persistence.Mongo;
using FluentAssertions;
using FluentAssertions.Extensions;
using MassTransit;
using MassTransit.Context;
using MassTransit.Testing;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Serilog;
using Tests.Shared.Auth;
using Tests.Shared.Extensions;
using Tests.Shared.Factory;
using WireMock.Server;
using Xunit.Sdk;
using IExternalEventBus = BuildingBlocks.Abstractions.Messaging.IExternalEventBus;

namespace Tests.Shared.Fixtures;

public class SharedFixture<TEntryPoint> : IAsyncLifetime
    where TEntryPoint : class
{
    private readonly IMessageSink _messageSink;
    private ITestHarness? _harness;
    private IHttpContextAccessor? _httpContextAccessor;
    private IServiceProvider? _serviceProvider;
    private IConfiguration? _configuration;
    private HttpClient? _adminClient;
    private HttpClient? _normalClient;
    private HttpClient? _guestClient;

    public Func<Task>? OnSharedFixtureInitialized;
    public Func<Task>? OnSharedFixtureDisposed;
    public bool AlreadyMigrated { get; set; }

    public ILogger Logger { get; }
    public PostgresContainerFixture PostgresContainerFixture { get; }
    public MongoContainerFixture MongoContainerFixture { get; }

    public Mongo2GoFixture Mongo2GoFixture { get; } = default!;
    public RabbitMQContainerFixture RabbitMqContainerFixture { get; }
    public CustomWebApplicationFactory<TEntryPoint> Factory { get; private set; }
    public IServiceProvider ServiceProvider => _serviceProvider ??= Factory.Services;

    public IConfiguration Configuration => _configuration ??= ServiceProvider.GetRequiredService<IConfiguration>();

    public ITestHarness MasstransitHarness => _harness ??= ServiceProvider.GetRequiredService<ITestHarness>();

    public IHttpContextAccessor HttpContextAccessor =>
        _httpContextAccessor ??= ServiceProvider.GetRequiredService<IHttpContextAccessor>();

    /// <summary>
    /// We should not dispose this GuestClient, because we reuse it in our tests
    /// </summary>
    public HttpClient GuestClient
    {
        get
        {
            if (_guestClient == null)
            {
                _guestClient = Factory.CreateClient();
                // Set the media type of the request to JSON - we need this for getting problem details result for all http calls because problem details just return response for request with media type JSON
                _guestClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            }

            return _guestClient;
        }
    }

    /// <summary>
    /// We should not dispose this AdminHttpClient, because we reuse it in our tests
    /// </summary>
    public HttpClient AdminHttpClient => _adminClient ??= CreateAdminHttpClient();

    /// <summary>
    /// We should not dispose this NormalUserHttpClient, because we reuse it in our tests
    /// </summary>
    public HttpClient NormalUserHttpClient => _normalClient ??= CreateNormalUserHttpClient();

    public WireMockServer WireMockServer { get; }
    public string WireMockServerUrl { get; } = null!;

    //https://github.com/xunit/xunit/issues/565
    //https://github.com/xunit/xunit/pull/1705
    //https://xunit.net/docs/capturing-output#output-in-extensions
    //https://andrewlock.net/tracking-down-a-hanging-xunit-test-in-ci-building-a-custom-test-framework/
    public SharedFixture(IMessageSink messageSink)
    {
        _messageSink = messageSink;
        messageSink.OnMessage(new DiagnosticMessage("Constructing SharedFixture..."));

        //https://github.com/trbenning/serilog-sinks-xunit
        Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.TestOutput(messageSink)
            .CreateLogger()
            .ForContext<SharedFixture<TEntryPoint>>();

        // //https://github.com/testcontainers/testcontainers-dotnet/blob/8db93b2eb28bc2bc7d579981da1651cd41ec03f8/docs/custom_configuration/index.md#enable-logging
        // TestcontainersSettings.Logger = new Serilog.Extensions.Logging.SerilogLoggerFactory(Logger).CreateLogger(
        //     "TestContainer"
        // );

        // Service provider will build after getting with get accessors, we don't want to build our service provider here
        PostgresContainerFixture = new PostgresContainerFixture(messageSink);
        MongoContainerFixture = new MongoContainerFixture(messageSink);
        //Mongo2GoFixture = new Mongo2GoFixture(messageSink);
        RabbitMqContainerFixture = new RabbitMQContainerFixture(messageSink);

        AutoFaker.Configure(b =>
        {
            // configure global AutoBogus settings here
            b.WithRecursiveDepth(3).WithTreeDepth(1).WithRepeatCount(1);
        });

        // close to equivalency required to reconcile precision differences between EF and Postgres
        AssertionOptions.AssertEquivalencyUsing(options =>
        {
            options
                .Using<DateTime>(ctx => ctx.Subject.Should().BeCloseTo(ctx.Expectation, 1.Seconds()))
                .WhenTypeIs<DateTime>();
            options
                .Using<DateTimeOffset>(ctx => ctx.Subject.Should().BeCloseTo(ctx.Expectation, 1.Seconds()))
                .WhenTypeIs<DateTimeOffset>();

            return options;
        });

        // new WireMockServer() is equivalent to call WireMockServer.Start()
        WireMockServer = WireMockServer.Start();
        WireMockServerUrl = WireMockServer.Url!;

        Factory = new CustomWebApplicationFactory<TEntryPoint>();
    }

    public async Task InitializeAsync()
    {
        _messageSink.OnMessage(new DiagnosticMessage("SharedFixture Started..."));

        // Service provider will build after getting with get accessors, we don't want to build our service provider here
        await Factory.InitializeAsync();
        await PostgresContainerFixture.InitializeAsync();
        await MongoContainerFixture.InitializeAsync();
        //await Mongo2GoFixture.InitializeAsync();
        await RabbitMqContainerFixture.InitializeAsync();

        // with `AddOverrideEnvKeyValues` config changes are accessible during services registration
        Factory.AddOverrideEnvKeyValues(
            new Dictionary<string, string>
            {
                {
                    $"{nameof(PostgresOptions)}:{nameof(PostgresOptions.ConnectionString)}",
                    PostgresContainerFixture.Container.GetConnectionString()
                },
                {
                    $"{nameof(MessagePersistenceOptions)}:{nameof(PostgresOptions.ConnectionString)}",
                    PostgresContainerFixture.Container.GetConnectionString()
                },
                {
                    $"{nameof(MongoOptions)}:{nameof(MongoOptions.ConnectionString)}",
                    MongoContainerFixture.Container.GetConnectionString()
                },
                {
                    $"{nameof(MongoOptions)}:{nameof(MongoOptions.DatabaseName)}",
                    MongoContainerFixture.MongoContainerOptions.DatabaseName
                },
                //{"MongoOptions:ConnectionString", Mongo2GoFixture.MongoDbRunner.ConnectionString}, //initialize mongo2go connection
                {
                    $"{nameof(RabbitMqOptions)}:{nameof(RabbitMqOptions.UserName)}",
                    RabbitMqContainerFixture.RabbitMqContainerOptions.UserName
                },
                {
                    $"{nameof(RabbitMqOptions)}:{nameof(RabbitMqOptions.Password)}",
                    RabbitMqContainerFixture.RabbitMqContainerOptions.Password
                },
                {
                    $"{nameof(RabbitMqOptions)}:{nameof(RabbitMqOptions.Host)}",
                    RabbitMqContainerFixture.Container.Hostname
                },
                {
                    $"{nameof(RabbitMqOptions)}:{nameof(RabbitMqOptions.Port)}",
                    RabbitMqContainerFixture.HostPort.ToString()
                },
            }
        );

        // with `AddOverrideInMemoryConfig` config changes are accessible after services registration and build process
        Factory.AddOverrideInMemoryConfig(new Dictionary<string, string>());
        Factory.ConfigurationAction += cfg =>
        {
            // Or we can override configuration explicitly, and it is accessible via IOptions<> and Configuration
            cfg["WireMockUrl"] = WireMockServerUrl;
        };

        var initCallback = OnSharedFixtureInitialized?.Invoke();
        if (initCallback != null)
            await initCallback;
    }

    public async Task DisposeAsync()
    {
        await PostgresContainerFixture.DisposeAsync();
        await MongoContainerFixture.DisposeAsync();
        //await Mongo2GoFixture.DisposeAsync();
        await RabbitMqContainerFixture.DisposeAsync();
        WireMockServer.Stop();
        AdminHttpClient.Dispose();
        NormalUserHttpClient.Dispose();
        GuestClient.Dispose();

        var disposeCallback = OnSharedFixtureDisposed?.Invoke();
        if (disposeCallback != null)
            await disposeCallback;

        await Factory.DisposeAsync();

        _messageSink.OnMessage(new DiagnosticMessage("SharedFixture Stopped..."));
    }

    public async Task CleanupMessaging(CancellationToken cancellationToken = default)
    {
        await RabbitMqContainerFixture.CleanupQueuesAsync(cancellationToken);
    }

    public async Task ResetDatabasesAsync(CancellationToken cancellationToken = default)
    {
        await PostgresContainerFixture.ResetDbAsync(cancellationToken);
        await MongoContainerFixture.ResetDbAsync(cancellationToken);
        //await Mongo2GoFixture.ResetDbAsync(cancellationToken); //get new connection for mongo2go with clean db

        ////Mongo2Go - set new connection with clean db from mongo2go to our app
        // var mongoOptions = ServiceProvider.GetRequiredService<MongoOptions>();
        // mongoOptions.ConnectionString = Mongo2GoFixture.MongoDbRunner.ConnectionString;
    }

    /// <summary>
    /// We could use `WithWebHostBuilder` method for specific config and customize existing `CustomWebApplicationFactory`
    /// </summary>
    public CustomWebApplicationFactory<TEntryPoint> WithWebHostBuilder(Action<IWebHostBuilder> builder)
    {
        Factory = Factory.WithWebHostBuilder(builder);
        return Factory;
    }

    public CustomWebApplicationFactory<TEntryPoint> WithHostBuilder(Action<IHostBuilder> builder)
    {
        Factory = Factory.WithHostBuilder(builder);
        return Factory;
    }

    public CustomWebApplicationFactory<TEntryPoint> WithConfigureAppConfigurations(
        Action<HostBuilderContext, IConfigurationBuilder> cfg
    )
    {
        Factory.WithConfigureAppConfigurations(cfg);
        return Factory;
    }

    public void ConfigureTestConfigureApp(Action<HostBuilderContext, IConfigurationBuilder>? configBuilder)
    {
        if (configBuilder is not null)
            Factory.TestConfigureApp += configBuilder;
    }

    public void ConfigureTestServices(Action<IServiceCollection>? services)
    {
        if (services is not null)
            Factory.TestConfigureServices += services;
    }

    public void SetOutputHelper(ITestOutputHelper outputHelper)
    {
        // var loggerFactory = ServiceProvider.GetRequiredService<ILoggerFactory>();
        // loggerFactory.AddXUnit(outputHelper);
        Factory.SetOutputHelper(outputHelper);
    }

    public void SetAdminUser()
    {
        var admin = CreateAdminUserMock();
        var identity = new ClaimsIdentity(admin.Claims);
        var claimsPrincipal = new ClaimsPrincipal(identity);

        var httpContext = Substitute.For<HttpContext>();
        httpContext.User.Returns(_ => claimsPrincipal);

        var httpContextAccessor = ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        httpContextAccessor.HttpContext = httpContext;
    }

    public async Task ExecuteScopeAsync(Func<IServiceProvider, Task> action)
    {
        await using var scope = ServiceProvider.CreateAsyncScope();
        await action(scope.ServiceProvider);
    }

    public async Task<T> ExecuteScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = ServiceProvider.CreateAsyncScope();

        var result = await action(scope.ServiceProvider);

        return result;
    }

    public async Task<TResponse> SendAsync<TResponse>(
        IRequest<TResponse> request,
        CancellationToken cancellationToken = default
    )
    {
        return await ExecuteScopeAsync(async sp =>
        {
            var mediator = sp.GetRequiredService<IMediator>();

            return await mediator.Send(request, cancellationToken);
        });
    }

    public async Task<TResponse> SendAsync<TResponse>(
        ICommand<TResponse> request,
        CancellationToken cancellationToken = default
    )
        where TResponse : class
    {
        return await ExecuteScopeAsync(async sp =>
        {
            var commandBus = sp.GetRequiredService<ICommandBus>();

            return await commandBus.SendAsync(request, cancellationToken);
        });
    }

    public async Task SendAsync<T>(T request, CancellationToken cancellationToken = default)
        where T : class, ICommand
    {
        await ExecuteScopeAsync(async sp =>
        {
            var commandBus = sp.GetRequiredService<ICommandBus>();

            await commandBus.SendAsync(request, cancellationToken);
        });
    }

    public async Task<TResponse> QueryAsync<TResponse>(
        IQuery<TResponse> query,
        CancellationToken cancellationToken = default
    )
        where TResponse : class
    {
        return await ExecuteScopeAsync(async sp =>
        {
            var queryProcessor = sp.GetRequiredService<IQueryBus>();

            return await queryProcessor.SendAsync(query, cancellationToken);
        });
    }

    public async ValueTask PublishMessageAsync<TMessage>(
        TMessage message,
        CancellationToken cancellationToken = default
    )
        where TMessage : class, IMessage
    {
        await ExecuteScopeAsync(async sp =>
        {
            var bus = sp.GetRequiredService<IExternalEventBus>();

            await bus.PublishAsync(message, cancellationToken);
        });
    }

    public async ValueTask PublishMessageAsync<TMessage>(
        IEventEnvelope<TMessage> eventEnvelope,
        CancellationToken cancellationToken = default
    )
        where TMessage : class, IMessage
    {
        await ExecuteScopeAsync(async sp =>
        {
            var bus = sp.GetRequiredService<IExternalEventBus>();

            await bus.PublishAsync(eventEnvelope, cancellationToken);
        });
    }

    // Ref: https://tech.energyhelpline.com/in-memory-testing-with-masstransit/
    public async ValueTask WaitUntilConditionMet(
        Func<Task<bool>> conditionToMet,
        int? timeoutSecond = null,
        string? exception = null
    )
    {
        var time = timeoutSecond ?? 300;

        var startTime = DateTime.Now;
        var timeoutExpired = false;
        var meet = await conditionToMet.Invoke();
        while (!meet)
        {
            if (timeoutExpired)
            {
                throw new TimeoutException(
                    exception ?? $"Condition not met for the test in the '{timeoutExpired}' second."
                );
            }

            await Task.Delay(100);
            meet = await conditionToMet.Invoke();
            timeoutExpired = DateTime.Now - startTime > TimeSpan.FromSeconds(time);
        }
    }

    public async Task WaitForPublishing<T>(CancellationToken cancellationToken = default)
        where T : class, IMessage
    {
        // will block the thread until there is a publishing message
        await MasstransitHarness.Published.Any(
            message =>
            {
                var messageFilter = new PublishedMessageFilter();
                var faultMessageFilter = new PublishedMessageFilter();

                messageFilter.Includes.Add<T>();
                messageFilter.Includes.Add<EventEnvelope<T>>();
                messageFilter.Includes.Add<IEventEnvelope<T>>();
                messageFilter.Includes.Add<IEventEnvelope>(x =>
                    (x.MessageObject as IEventEnvelope)!.Message.GetType() == typeof(T)
                );

                faultMessageFilter.Includes.Add<Fault<EventEnvelope<T>>>();
                faultMessageFilter.Includes.Add<Fault<IEventEnvelope<T>>>();
                faultMessageFilter.Includes.Add<T>();

                var faulty = faultMessageFilter.Any(message);
                var published = messageFilter.Any(message);

                return published & !faulty;
            },
            cancellationToken
        );
    }

    public async Task WaitForSending<T>(CancellationToken cancellationToken = default)
        where T : class, IMessage
    {
        // will block the thread until there is a publishing message
        await MasstransitHarness.Sent.Any(
            message =>
            {
                var messageFilter = new SentMessageFilter();
                var faultMessageFilter = new SentMessageFilter();

                messageFilter.Includes.Add<T>();
                messageFilter.Includes.Add<EventEnvelope<T>>();
                messageFilter.Includes.Add<IEventEnvelope<T>>();
                messageFilter.Includes.Add<IEventEnvelope>(x =>
                    (x.MessageObject as IEventEnvelope)!.Message.GetType() == typeof(T)
                );

                faultMessageFilter.Includes.Add<Fault<EventEnvelope<T>>>();
                faultMessageFilter.Includes.Add<Fault<IEventEnvelope<T>>>();
                faultMessageFilter.Includes.Add<Fault<T>>();

                var faulty = faultMessageFilter.Any(message);
                var published = messageFilter.Any(message);

                return published & !faulty;
            },
            cancellationToken
        );
    }

    public async Task WaitForConsuming<T>(CancellationToken cancellationToken = default)
        where T : class, IMessage
    {
        // will block the thread until there is a consuming message
        await MasstransitHarness.Consumed.Any(
            message =>
            {
                var messageFilter = new ReceivedMessageFilter();
                var faultMessageFilter = new ReceivedMessageFilter();

                messageFilter.Includes.Add<IEventEnvelope<T>>();
                messageFilter.Includes.Add<EventEnvelope<T>>();
                messageFilter.Includes.Add<IEventEnvelope>(x =>
                    (x.MessageObject as IEventEnvelope)!.Message.GetType() == typeof(T)
                );
                messageFilter.Includes.Add<T>();

                faultMessageFilter.Includes.Add<Fault<EventEnvelope<T>>>();
                faultMessageFilter.Includes.Add<Fault<T>>();
                faultMessageFilter.Includes.Add<Fault<IEventEnvelope<T>>>();

                var faulty = faultMessageFilter.Any(message);
                var published = messageFilter.Any(message);

                return published & !faulty;
            },
            cancellationToken
        );
    }

    public async Task WaitForConsuming<TMessage, TConsumedBy>(CancellationToken cancellationToken = default)
        where TMessage : class, IMessage
        where TConsumedBy : class, IConsumer
    {
        var consumerHarness = ServiceProvider.GetRequiredService<IConsumerTestHarness<TConsumedBy>>();

        // will block the thread until there is a consuming message
        await consumerHarness.Consumed.Any(
            message =>
            {
                var messageFilter = new ReceivedMessageFilter();
                var faultMessageFilter = new ReceivedMessageFilter();

                messageFilter.Includes.Add<IEventEnvelope<TMessage>>();
                messageFilter.Includes.Add<EventEnvelope<TMessage>>();
                messageFilter.Includes.Add<IEventEnvelope>(x =>
                    (x.MessageObject as IEventEnvelope)!.Message.GetType() == typeof(TMessage)
                );
                messageFilter.Includes.Add<TMessage>();

                faultMessageFilter.Includes.Add<Fault<EventEnvelope<TMessage>>>();
                faultMessageFilter.Includes.Add<Fault<TMessage>>();
                faultMessageFilter.Includes.Add<Fault<IEventEnvelope<TMessage>>>();

                var faulty = faultMessageFilter.Any(message);
                var published = messageFilter.Any(message);

                return published & !faulty;
            },
            cancellationToken
        );
    }

    // public async ValueTask<IHypothesis<TMessage>> ShouldConsumeWithNewConsumer<TMessage>(
    //     Predicate<TMessage>? match = null)
    //     where TMessage : class, IMessage
    // {
    //     var hypothesis = Hypothesis
    //         .For<TMessage>()
    //         .Any(match ?? (_ => true));
    //
    //     ////https://stackoverflow.com/questions/55169197/how-to-use-masstransit-test-harness-to-test-consumer-with-constructor-dependency
    //     // Harness.Consumer(() => hypothesis.AsConsumer());
    //
    //     await Harness.SubscribeHandler<TMessage>(ctx =>
    //     {
    //         hypothesis.Test(ctx.Message).GetAwaiter().GetResult();
    //         return true;
    //     });
    //
    //     return hypothesis;
    // }
    //
    // public  async ValueTask<IHypothesis<TMessage>> ShouldConsumeWithNewConsumer<TMessage, TConsumer>(
    //     Predicate<TMessage>? match = null)
    //     where TMessage : class, IMessage
    //     where TConsumer : class, IConsumer<TMessage>
    // {
    //     var hypothesis = Hypothesis
    //         .For<TMessage>()
    //         .Any(match ?? (_ => true));
    //
    //     //https://stackoverflow.com/questions/55169197/how-to-use-masstransit-test-harness-to-test-consumer-with-constructor-dependency
    //     Harness.Consumer(() => hypothesis.AsConsumer<TMessage, TConsumer>(ServiceProvider));
    //
    //     return hypothesis;
    // }

    public async ValueTask ShouldProcessedOutboxPersistMessage<TMessage>(CancellationToken cancellationToken = default)
        where TMessage : class, IMessage
    {
        await WaitUntilConditionMet(async () =>
        {
            return await ExecuteScopeAsync(async sp =>
            {
                var messagePersistenceService = sp.GetService<IMessagePersistenceService>();
                messagePersistenceService.NotBeNull();

                var filter = await messagePersistenceService.GetByFilterAsync(
                    x =>
                        x.DeliveryType == MessageDeliveryType.Outbox
                        && TypeMapper.GetFullTypeName(typeof(TMessage)) == x.DataType,
                    cancellationToken
                );

                var res = filter.Any(x => x.MessageStatus == MessageStatus.Processed);

                if (res is true) { }

                return res;
            });
        });
    }

    public async ValueTask ShouldProcessedPersistInternalCommand<TInternalCommand>(
        CancellationToken cancellationToken = default
    )
        where TInternalCommand : class, IInternalCommand
    {
        await WaitUntilConditionMet(async () =>
        {
            return await ExecuteScopeAsync(async sp =>
            {
                var messagePersistenceService = sp.GetService<IMessagePersistenceService>();
                messagePersistenceService.NotBeNull();

                var filter = await messagePersistenceService.GetByFilterAsync(
                    x =>
                        x.DeliveryType == MessageDeliveryType.Internal
                        && TypeMapper.GetFullTypeName(typeof(TInternalCommand)) == x.DataType,
                    cancellationToken
                );

                var res = filter.Any(x => x.MessageStatus == MessageStatus.Processed);

                return res;
            });
        });
    }

    private HttpClient CreateAdminHttpClient()
    {
        var adminClient = Factory.CreateClient();

        // Set the media type of the request to JSON - we need this for getting problem details result for all http calls because problem details just return response for request with media type JSON
        adminClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        //https://github.com/webmotions/fake-authentication-jwtbearer/issues/14
        var claims = CreateAdminUserMock().Claims;

        adminClient.SetFakeJwtBearerClaims(claims);

        return adminClient;
    }

    private HttpClient CreateNormalUserHttpClient()
    {
        var userClient = Factory.CreateClient();

        // Set the media type of the request to JSON - we need this for getting problem details result for all http calls because problem details just return response for request with media type JSON
        userClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        //https://github.com/webmotions/fake-authentication-jwtbearer/issues/14
        var claims = CreateNormalUserMock().Claims;

        userClient.SetFakeJwtBearerClaims(claims);

        return userClient;
    }

    private MockAuthUser CreateAdminUserMock()
    {
        var roleClaim = new Claim(ClaimTypes.Role, Constants.Users.Admin.Role);
        var otherClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Constants.Users.Admin.UserId),
            new(ClaimTypes.Name, Constants.Users.Admin.UserName),
            new(ClaimTypes.Email, Constants.Users.Admin.Email)
        };

        return _ = new MockAuthUser(otherClaims.Concat(new[] { roleClaim }).ToArray());
    }

    private MockAuthUser CreateNormalUserMock()
    {
        var roleClaim = new Claim(ClaimTypes.Role, Constants.Users.NormalUser.Role);
        var otherClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Constants.Users.NormalUser.UserId),
            new(ClaimTypes.Name, Constants.Users.NormalUser.UserName),
            new(ClaimTypes.Email, Constants.Users.NormalUser.Email)
        };

        return _ = new MockAuthUser(otherClaims.Concat(new[] { roleClaim }).ToArray());
    }
}
