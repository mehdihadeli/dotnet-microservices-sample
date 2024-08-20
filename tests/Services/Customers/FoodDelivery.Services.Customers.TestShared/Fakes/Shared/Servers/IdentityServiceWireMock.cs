using System.Net;
using FoodDelivery.Services.Customers.Shared.Clients.Identity;
using FoodDelivery.Services.Customers.Shared.Clients.Identity.Dtos;
using FoodDelivery.Services.Customers.TestShared.Fakes.Shared.Dtos;
using FoodDelivery.Services.Shared.Identity.Users.Events.V1.Integration;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace FoodDelivery.Services.Customers.TestShared.Fakes.Shared.Servers;

//https://www.ontestautomation.com/api-mocking-in-csharp-with-wiremock-net/
//https://github.com/WireMock-Net/WireMock.Net/wiki
//https://pcholko.com/posts/2021-04-05/wiremock-integration-test/
//https://www.youtube.com/watch?v=YU3ohofu6UU
public class IdentityServiceWireMock(WireMockServer wireMockServer, IdentityApiClientOptions identityApiClientOptions)
{
    private IdentityApiClientOptions IdentityApiClientOptions { get; } = identityApiClientOptions;

    public (GetUserByEmailClientDto Response, string Endpoint) SetupGetUserByEmail(string? email = null)
    {
        var fakeIdentityUser = new FakeUserIdentityDto().Generate();
        if (!string.IsNullOrWhiteSpace(email))
            fakeIdentityUser = fakeIdentityUser with { Email = email };

        var response = new GetUserByEmailClientDto(fakeIdentityUser);

        //https://github.com/WireMock-Net/WireMock.Net/wiki/Request-Matching
        // we should put / in the beginning of the endpoint
        var endpointPath = $"/{IdentityApiClientOptions.UsersEndpoint}/by-email/{fakeIdentityUser.Email}";

        wireMockServer
            .Given(Request.Create().UsingGet().WithPath(endpointPath))
            .RespondWith(Response.Create().WithBodyAsJson(response).WithStatusCode(HttpStatusCode.OK));

        return (response, endpointPath);
    }

    public (GetUserByEmailClientDto Response, string Endpoint) SetupGetUserByEmail(UserRegisteredV1 userRegisteredV1)
    {
        var response = new GetUserByEmailClientDto(
            new IdentityUserClientDto(
                userRegisteredV1.IdentityId,
                userRegisteredV1.UserName,
                userRegisteredV1.Email,
                userRegisteredV1.PhoneNumber,
                userRegisteredV1.FirstName,
                userRegisteredV1.LastName
            )
        );

        //https://github.com/WireMock-Net/WireMock.Net/wiki/Request-Matching
        var endpointPath = $"/{IdentityApiClientOptions.UsersEndpoint}/by-email/{userRegisteredV1.Email}"; // we should put / in the beginning of the endpoint

        wireMockServer
            .Given(Request.Create().UsingGet().WithPath(endpointPath))
            .RespondWith(Response.Create().WithBodyAsJson(response).WithStatusCode(HttpStatusCode.OK));

        return (response, endpointPath);
    }
}
