using System.Text;
using LanQueue.Util;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq's managed SHA-256 (no OpenSSL needed) matches the platform one and the FIPS 180-4 test vectors.</summary>
public sealed class Sha256Tests
{
    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1")]
    public void MatchesTheStandardVectors(string input, string expected)
        => Assert.Equal(expected, Convert.ToHexStringLower(Sha256.Hash(Encoding.ASCII.GetBytes(input))));

    [Fact]
    public void MatchesThePlatformHashAtEveryLengthAroundTheBlockEdges()
    {
        var random = new Random(8);
        for (var length = 0; length <= 300; length++)
        {
            var data = new byte[length];
            random.NextBytes(data);
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(data), Sha256.Hash(data));
        }

        var big = new byte[1_000_003];
        random.NextBytes(big);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(big), Sha256.Hash(big));
    }
}
