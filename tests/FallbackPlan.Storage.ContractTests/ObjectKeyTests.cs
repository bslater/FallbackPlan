using FallbackPlan.Storage.Abstractions;
using FsCheck.Xunit;
using Xunit;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// Exercises the store-key grammar (specification 01 §2): traversal and
/// hidden-path shapes are unconstructible, and parse–render is a bijection.
/// </summary>
public sealed class ObjectKeyTests
{
    [Theory]
    [InlineData("repository-format")]
    [InlineData("blobs/data/abcd/n7do2wykywpzljfjg3epzyaura")]
    [InlineData("blobs/data/abcd/n7do2wykywpzljfjg3epzyaura.footer")]
    [InlineData("index/delta/0000000000000001/delta-1")]
    [InlineData("keys/key_1")]
    public void Valid_keys_parse_and_render_unchanged(string value)
    {
        Assert.True(ObjectKey.TryParse(value, out var key));
        Assert.Equal(value, key.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    [InlineData("double//component")]
    [InlineData("..")]
    [InlineData("blobs/../keys")]
    [InlineData(".hidden")]
    [InlineData("blobs/.fbp-tmp/x")]
    [InlineData("Uppercase")]
    [InlineData("with space")]
    [InlineData("back\\slash")]
    [InlineData("percent%20")]
    public void Invalid_keys_are_unconstructible(string value)
    {
        Assert.False(ObjectKey.TryParse(value, out _));
        Assert.Throws<ArgumentException>(() => ObjectKey.Parse(value));
    }

    [Fact]
    public void A_key_longer_than_the_maximum_is_refused()
    {
        var component = new string('a', ObjectKey.MaximumComponentLength);
        var value = string.Join('/', Enumerable.Repeat(component, 5)); // 1279 chars

        Assert.False(ObjectKey.TryParse(value, out _));
    }

    [Fact]
    public void A_component_longer_than_the_maximum_is_refused()
    {
        Assert.False(ObjectKey.TryParse(new string('a', ObjectKey.MaximumComponentLength + 1), out _));
    }

    [Fact]
    public void Prefixes_match_by_ordinal_string_prefix()
    {
        var key = ObjectKey.Parse("blobs/data/abcd/object");

        Assert.True(ObjectPrefix.Parse("blobs/").Matches(key));
        Assert.True(ObjectPrefix.Parse("blobs/data/ab").Matches(key));
        Assert.True(ObjectPrefix.All.Matches(key));
        Assert.False(ObjectPrefix.Parse("blobs/meta/").Matches(key));
    }

    [Property]
    public bool Parse_of_a_rendered_key_returns_an_equal_key(byte[]? componentSeeds, byte componentCount)
    {
        // Deterministically map arbitrary bytes onto the valid alphabet so the
        // property explores the grammar's full space without rejection bias.
        var seeds = componentSeeds is { Length: > 0 } ? componentSeeds : [0x01];
        var components = Enumerable.Range(0, (componentCount % 5) + 1)
            .Select(index => new string(
            [
                .. seeds
                    .Skip(index)
                    .Take(8)
                    .DefaultIfEmpty((byte)(index + 1))
                    .Select(seed => "abcdefghijklmnopqrstuvwxyz0123456789-_"[seed % 38]),
            ]))
            .ToArray();

        var value = string.Join('/', components);

        return ObjectKey.TryParse(value, out var key)
            && key.Value == value
            && ObjectKey.Parse(key.Value) == key;
    }
}
