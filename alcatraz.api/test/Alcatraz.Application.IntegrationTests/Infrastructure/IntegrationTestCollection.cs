namespace Alcatraz.Application.IntegrationTests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class IntegrationTestCollection : ICollectionFixture<IntegrationTestWebAppFactory>
{
    public const string Name = "Integration";
}
