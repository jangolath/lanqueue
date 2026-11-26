using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sample.Core;

/// <summary>A product as stored on disk.</summary>
public sealed record Product(string Sku, string Name, long PriceCents, IReadOnlyList<string> Tags);

[JsonSerializable(typeof(List<Product>))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class SampleJson : JsonSerializerContext;

/// <summary>Saves and loads products as one JSON file, written through a temp file and a rename.</summary>
public sealed class ProductStore(string path)
{
    public async Task SaveAsync(IReadOnlyList<Product> products, CancellationToken ct = default)
    {
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, products.ToList(), SampleJson.Default.ListProduct, ct).ConfigureAwait(false);
        }

        File.Move(tmp, path, overwrite: true);
    }

    public async Task<IReadOnlyList<Product>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(stream, SampleJson.Default.ListProduct, ct).ConfigureAwait(false) ?? [];
    }
}
