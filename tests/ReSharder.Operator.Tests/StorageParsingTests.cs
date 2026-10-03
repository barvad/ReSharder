using ReSharder.Operator.Services;

namespace ReSharder.Operator.Tests;

public class StorageParsingTests
{
    [Theory]
    [InlineData("1Ki", 1024L)]
    [InlineData("1Mi", 1024L * 1024)]
    [InlineData("1Gi", 1024L * 1024 * 1024)]
    [InlineData("10Gi", 10L * 1024 * 1024 * 1024)]
    [InlineData("50Gi", 50L * 1024 * 1024 * 1024)]
    [InlineData("1Ti", 1024L * 1024 * 1024 * 1024)]
    public void ParseStorageToBytes_ValidQuantities(string input, long expectedBytes)
    {
        var result = CnpgClusterManager.ParseStorageToBytes(input);
        Assert.Equal(expectedBytes, result);
    }

    [Theory]
    [InlineData("100MB")]
    [InlineData("10G")]
    [InlineData("abc")]
    public void ParseStorageToBytes_InvalidFormat_Throws(string input)
    {
        Assert.Throws<FormatException>(() => CnpgClusterManager.ParseStorageToBytes(input));
    }

    [Theory]
    [InlineData(1024L * 1024 * 1024, "1Gi")]
    [InlineData(10L * 1024 * 1024 * 1024, "10Gi")]
    [InlineData(1536L * 1024 * 1024, "2Gi")]       // 1.5 Gi rounds up to 2
    [InlineData(100L * 1024 * 1024, "1Gi")]          // < 1 Gi rounds up to 1
    public void FormatBytesToGi_ReturnsExpected(long bytes, string expected)
    {
        var result = CnpgClusterManager.FormatBytesToGi(bytes);
        Assert.Equal(expected, result);
    }
}
