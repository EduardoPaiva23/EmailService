using EmailService.Application.Security;

namespace EmailService.UnitTests.Application;

public class ApiKeyHasherTests
{
    [Fact]
    public void Hash_IsLowercaseHexSha256()
    {
        // SHA-256("abc") — vetor de teste conhecido (FIPS 180-2).
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ApiKeyHasher.Hash("abc"));
    }

    [Fact]
    public void Hash_DifferentKeys_ProduceDifferentHashes()
    {
        Assert.NotEqual(ApiKeyHasher.Hash("chave-a"), ApiKeyHasher.Hash("chave-b"));
    }

    [Fact]
    public void GetPrefix_ReturnsFirstCharacters()
    {
        var key = "es_0123456789abcdefghijklmnopqrstuv";

        Assert.Equal(key[..ApiKeyHasher.PrefixLength], ApiKeyHasher.GetPrefix(key));
    }

    [Fact]
    public void GetPrefix_RejectsKeysShorterThanMinimum()
    {
        var shortKey = new string('a', ApiKeyHasher.MinimumKeyLength - 1);

        Assert.Throws<ArgumentException>(() => ApiKeyHasher.GetPrefix(shortKey));
    }
}
