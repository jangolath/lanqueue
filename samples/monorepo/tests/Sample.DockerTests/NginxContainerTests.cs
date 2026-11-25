using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Sample.DockerTests;

/// <summary>
/// Opt-in (not in Sample.slnx): starts a throwaway nginx container with its port 80 on a random host port, then removes it. Needs a
/// working Docker engine; lanq.extras.yml's docker job asks for a worker tagged docker. The image is pinned by digest.
/// </summary>
public sealed class NginxContainerTests : IAsyncLifetime
{
    private const string Image = "nginx:1.27.5-alpine@sha256:65645c7bb6a0661892a8b03b89d0743208a18dd2f3f17a54ef4b76fb8e2f2a10";

    private readonly IContainer _container = new ContainerBuilder(Image)
        .WithPortBinding(80, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(80)))
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task ServesTheDefaultPage()
    {
        using var http = new HttpClient();
        var url = new UriBuilder("http", _container.Hostname, _container.GetMappedPublicPort(80)).Uri;
        var body = await http.GetStringAsync(url);
        Assert.Contains("nginx", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunsACommandInside()
    {
        var result = await _container.ExecAsync(["nginx", "-v"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("nginx/1.27.5", result.Stderr + result.Stdout, StringComparison.Ordinal);
    }
}
