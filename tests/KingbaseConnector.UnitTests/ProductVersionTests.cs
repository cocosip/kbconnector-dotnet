namespace KingbaseConnector.UnitTests;

public class ProductVersionTests
{
    [Theory]
    [InlineData("V009R001C001B0024", null, null, null)]
    [InlineData("V009R002C003M004B0056", 4, null, null)]
    [InlineData("V009R002C003M004Customer_123B0056PS007", 4, "Customer_123", 7)]
    [InlineData("V009R002C003M004B123BB0056PS007", 4, "B123B", 7)]
    [InlineData("V009R002C003B0056PS007", null, null, 7)]
    public void FullVersionRetainsAllFixedWidthComponentsAndRawText(string token, int? custom, string? customer, int? patch)
    {
        var raw = "KingbaseES " + token + " on test-platform, compiled by test compiler";
        Assert.True(KingbaseProductVersion.TryParse(raw, out var parsed));
        var version = Assert.IsType<KingbaseProductVersion>(parsed);
        Assert.Equal(raw, version.RawText);
        Assert.Equal(token, version.Token);
        Assert.Equal(token, version.ToString());
        Assert.Equal(9, version.Major);
        Assert.Equal(int.Parse(token.Substring(5, 3)), version.Release);
        Assert.Equal(int.Parse(token.Substring(9, 3)), version.GeneralRelease);
        Assert.Equal(custom, version.Customization);
        Assert.Equal(customer, version.CustomerCode);
        Assert.Equal(token == "V009R001C001B0024" ? 2 : 5, version.Build);
        Assert.Equal(token == "V009R001C001B0024" ? 4 : 6, version.BuildRevision);
        Assert.Equal(patch, version.PatchSet);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("14.0")]
    [InlineData("PostgreSQL 14.0 KingbaseES V009R001C001B0024")]
    [InlineData("OtherDB V009R001C001B0024")]
    [InlineData(" KingbaseES V009R001C001B0024")]
    [InlineData("KingbaseES V009R001B0024")]
    [InlineData("KingbaseES V9R001C001B0024")]
    [InlineData("KingbaseES V009R01C001B0024")]
    [InlineData("KingbaseES V009R001C01B0024")]
    [InlineData("KingbaseES V009R001C001B024")]
    [InlineData("KingbaseES V009R001C001B00245")]
    [InlineData("KingbaseES V009R001C001B0024PS1")]
    [InlineData("KingbaseES V009R001C001B0024PS0001")]
    [InlineData("KingbaseES V009R001C001B0024-unknown")]
    [InlineData("KingbaseES V009R001C001M01B0024")]
    [InlineData("KingbaseES V009R001C001M001客户B0024")]
    [InlineData("KingbaseES V009R001C001M001customer-nameB0024")]
    [InlineData("KingbaseES V009R001C001M001123456789012345678901B0024")]
    [InlineData("KingbaseES V００９R001C001B0024")]
    [InlineData("KingbaseES V009R001C001B0024\0")]
    public void MalformedUnbrandedAndGenericVersionsAreNotKingbaseIdentity(string? text)
    {
        Assert.False(KingbaseProductVersion.TryParse(text, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void CustomerCodeMaximumAndRawTextLimitAreBounded()
    {
        var text = "KingbaseES V009R001C001M001" + new string('B', 20) + "B0024";
        Assert.True(KingbaseProductVersion.TryParse(text, out var version));
        Assert.Equal(new string('B', 20), version!.CustomerCode);
        Assert.False(KingbaseProductVersion.TryParse(text + " " + new string('x', 4096), out _));
    }
}
