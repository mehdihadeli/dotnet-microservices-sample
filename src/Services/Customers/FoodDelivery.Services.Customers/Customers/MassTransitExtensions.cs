using BuildingBlocks.Abstractions.Events;
using BuildingBlocks.Core.Messaging;
using BuildingBlocks.Integration.MassTransit;
using FoodDelivery.Services.Shared.Customers.Customers.Events.V1.Integration;
using Humanizer;
using MassTransit;

namespace FoodDelivery.Services.Customers.Customers;

internal static class MassTransitExtensions
{
    internal static void ConfigureCustomerMessagesTopology(this IRabbitMqBusFactoryConfigurator cfg)
    {
        // https://masstransit.io/documentation/transports/rabbitmq
        cfg.Message<IEventEnvelope<CustomerCreatedV1>>(e =>
        {
            // https://masstransit.io/documentation/configuration/topology/message
            // Name of the `primary exchange` for type based message publishing and sending
            // e.SetEntityName($"{nameof(CustomerCreatedV1).Underscore()}{MessagingConstants.PrimaryExchangePostfix}");
            e.SetEntityNameFormatter(new CustomEntityNameFormatter<IEventEnvelope<CustomerCreatedV1>>());
        });

        // configuration for MessagePublishTopologyConfiguration and using IPublishEndpoint
        cfg.Publish<IEventEnvelope<CustomerCreatedV1>>(e =>
        {
            // we configured some shared settings for all publish message in masstransit publish topologies

            // // setup primary exchange
            // e.Durable = true;
            // e.ExchangeType = ExchangeType.Direct;
        });

        // configuration for MessageSendTopologyConfiguration and using ISendEndpointProvider
        cfg.Send<IEventEnvelope<CustomerCreatedV1>>(e =>
        {
            // route by message type to binding fanout exchange (exchange to exchange binding)
            e.UseRoutingKeyFormatter(context => context.Message.GetType().Name.Underscore());
        });
    }
}
