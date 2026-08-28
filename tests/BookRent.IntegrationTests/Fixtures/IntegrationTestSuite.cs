namespace BookRent.IntegrationTests.Fixtures;

/// <summary>
/// Compartilha a mesma instancia de containers entre todas as classes da colecao,
/// evitando subir PostgreSQL e Redis uma vez por classe de teste.
/// </summary>
[CollectionDefinition(Name)]
public sealed class IntegrationTestSuite : ICollectionFixture<BookRentApiFactory>
{
    public const string Name = "bookrent-integration";
}
