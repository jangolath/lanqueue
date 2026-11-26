using System.Text.Json;
using Sample.Core;
using Xunit;

namespace Sample.IntegrationTests;

/// <summary>A temp folder per test class, removed afterwards.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sample-it-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public sealed class ProductStoreTests(TempDir temp) : IClassFixture<TempDir>
{
    private static readonly Product[] Products =
    [
        new("A-1", "Anvil", 4999, ["heavy", "iron"]),
        new("B-2", "Bucket", 799, []),
    ];

    [Fact]
    public async Task SavesAndLoads()
    {
        var store = new ProductStore(Path.Combine(temp.Path, "round-trip.json"));
        await store.SaveAsync(Products);
        var loaded = await store.LoadAsync();
        Assert.Equal(Products.Select(p => (p.Sku, p.Name, p.PriceCents, string.Join('|', p.Tags))), loaded.Select(p => (p.Sku, p.Name, p.PriceCents, string.Join('|', p.Tags))));
    }

    [Fact]
    public async Task MissingFileIsEmpty() => Assert.Empty(await new ProductStore(Path.Combine(temp.Path, "none.json")).LoadAsync());

    [Fact]
    public async Task WritesCamelCaseJsonAndNoTempFile()
    {
        var file = Path.Combine(temp.Path, "shape.json");
        await new ProductStore(file).SaveAsync(Products);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        Assert.Equal("Anvil", doc.RootElement[0].GetProperty("name").GetString());
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public async Task OverwritesAnOlderFile()
    {
        var file = Path.Combine(temp.Path, "overwrite.json");
        var store = new ProductStore(file);
        await store.SaveAsync(Products);
        await store.SaveAsync([Products[1]]);
        Assert.Equal("B-2", Assert.Single(await store.LoadAsync()).Sku);
    }
}

public sealed class CsvFileTests(TempDir temp) : IClassFixture<TempDir>
{
    [Fact]
    public async Task WritesAndReadsACsvFile()
    {
        var file = Path.Combine(temp.Path, "rows.csv");
        string[][] rows = [["sku", "name"], ["A-1", "Anvil, large"], ["B-2", "Bucket \"blue\""]];
        await File.WriteAllLinesAsync(file, rows.Select(r => CsvLine.Format(r)));
        var back = (await File.ReadAllLinesAsync(file)).Select(CsvLine.Parse).ToList();
        Assert.Equal(rows.Length, back.Count);
        Assert.Equal("Anvil, large", back[1][1]);
        Assert.Equal("Bucket \"blue\"", back[2][1]);
    }

    [Fact]
    public async Task CountsWordsInAFile()
    {
        var file = Path.Combine(temp.Path, "words.txt");
        await File.WriteAllTextAsync(file, "one two two three three three\n");
        Assert.Equal(3, Tokenizer.Counts(await File.ReadAllTextAsync(file))["three"]);
    }
}

public sealed class EchoServerTests : IAsyncLifetime
{
    private readonly EchoServer _server = new();

    public Task InitializeAsync()
    {
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task AnswersUpperCase() => Assert.Equal("HELLO", await EchoServer.AskAsync(_server.Port, "hello"));

    [Fact]
    public async Task ServesClientsInParallel()
    {
        var answers = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => EchoServer.AskAsync(_server.Port, $"client-{i}")));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"CLIENT-{i}"), answers);
    }

    [Fact]
    public void ListensOnARandomLoopbackPort() => Assert.InRange(_server.Port, 1, 65535);
}

public sealed class InventoryWorkflowTests
{
    [Fact]
    public void OrderReservesPaysAndShips()
    {
        var inv = new Inventory();
        inv.Receive("A-1", 5);
        var order = new OrderStateMachine();
        Assert.True(inv.TryReserve("A-1", 2));
        order.Move(OrderState.Paid);
        inv.Commit("A-1", 2);
        order.Move(OrderState.Shipped);
        Assert.Equal((3, OrderState.Shipped), (inv.OnHand("A-1"), order.State));
    }

    [Fact]
    public void CancelledOrderReleasesStock()
    {
        var inv = new Inventory();
        inv.Receive("A-1", 5);
        inv.TryReserve("A-1", 5);
        var order = new OrderStateMachine();
        order.Move(OrderState.Cancelled);
        inv.Release("A-1", 5);
        Assert.Equal(5, inv.Available("A-1"));
    }
}

/// <summary>A medium class: file I/O in a loop (a couple of seconds).</summary>
public sealed class ManyFilesTests(TempDir temp) : IClassFixture<TempDir>
{
    [Fact]
    public async Task WritesAndReadsManySmallFiles()
    {
        for (var i = 0; i < 200; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(temp.Path, $"f{i}.txt"), Slug.From($"File number {i}"));
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(200, Directory.GetFiles(temp.Path, "f*.txt").Length);
        Assert.Equal("file-number-7", await File.ReadAllTextAsync(Path.Combine(temp.Path, "f7.txt")));
    }
}
