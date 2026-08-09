using FallbackPlan.TestSupport;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// Exercises the store-key grammar (specification 01 §2): traversal and
/// hidden-path shapes are unconstructible, and parse–render is a bijection.
/// </summary>
[TestClass]
public sealed class ObjectKeyTests
{
    [TestMethod]
    [DataRow("repository-format")]
    [DataRow("blobs/data/abcd/n7do2wykywpzljfjg3epzyaura")]
    [DataRow("blobs/data/abcd/n7do2wykywpzljfjg3epzyaura.footer")]
    [DataRow("index/delta/0000000000000001/delta-1")]
    [DataRow("keys/key_1")]
    public void Valid_keys_parse_and_render_unchanged(string value)
    {
        Assert.IsTrue(ObjectKey.TryParse(value, out var key));
        Assert.AreEqual(value, key.Value);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("/leading")]
    [DataRow("trailing/")]
    [DataRow("double//component")]
    [DataRow("..")]
    [DataRow("blobs/../keys")]
    [DataRow(".hidden")]
    [DataRow("blobs/.fbp-tmp/x")]
    [DataRow("Uppercase")]
    [DataRow("with space")]
    [DataRow("back\\slash")]
    [DataRow("percent%20")]
    public void Invalid_keys_are_unconstructible(string value)
    {
        Assert.IsFalse(ObjectKey.TryParse(value, out _));
        Assert.ThrowsExactly<ArgumentException>(() => ObjectKey.Parse(value));
    }

    [TestMethod]
    public void A_key_longer_than_the_maximum_is_refused()
    {
        var component = new string('a', ObjectKey.MaximumComponentLength);
        var value = string.Join('/', Enumerable.Repeat(component, 5)); // 1279 chars

        Assert.IsFalse(ObjectKey.TryParse(value, out _));
    }

    [TestMethod]
    public void A_component_longer_than_the_maximum_is_refused()
    {
        Assert.IsFalse(ObjectKey.TryParse(new string('a', ObjectKey.MaximumComponentLength + 1), out _));
    }

    [TestMethod]
    public void Prefixes_match_by_ordinal_string_prefix()
    {
        var key = ObjectKey.Parse("blobs/data/abcd/object");

        Assert.IsTrue(ObjectPrefix.Parse("blobs/").Matches(key));
        Assert.IsTrue(ObjectPrefix.Parse("blobs/data/ab").Matches(key));
        Assert.IsTrue(ObjectPrefix.All.Matches(key));
        Assert.IsFalse(ObjectPrefix.Parse("blobs/meta/").Matches(key));
    }

    [TestMethod]
    public void Parse_of_a_rendered_key_returns_an_equal_key() =>
        PropertyCheck.Holds(this);

    public static bool Parse_of_a_rendered_key_returns_an_equal_keyProperty(byte[]? componentSeeds, byte componentCount)
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
