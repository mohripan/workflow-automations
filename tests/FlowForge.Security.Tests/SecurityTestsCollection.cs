namespace FlowForge.Security.Tests;

/// <summary>
/// One factory (and one set of containers) shared across all security tests.
/// </summary>
[CollectionDefinition("SecurityTests")]
public class SecurityTestsCollection : ICollectionFixture<TestWebAppFactory> { }
