using ClashBarX.Core.Mihomo;

namespace ClashBarX.Core.Tests.Mihomo;

public class MihomoUrlBuilderTests
{
    [Theory]
    [InlineData("127.0.0.1:9090", false, "http://127.0.0.1:9090")]
    [InlineData("http://127.0.0.1:9090", false, "http://127.0.0.1:9090")]
    [InlineData("https://core.lan:9090", false, "https://core.lan:9090")]
    [InlineData("127.0.0.1:9090", true, "ws://127.0.0.1:9090")]
    [InlineData("ws://127.0.0.1:9090", true, "ws://127.0.0.1:9090")]
    [InlineData("wss://core.lan:9090", true, "wss://core.lan:9090")]
    [InlineData("http://127.0.0.1:9090", true, "ws://127.0.0.1:9090")]
    [InlineData("https://core.lan:9090", true, "wss://core.lan:9090")]
    public void 归一化控制器地址(string controller, bool forWebSocket, string expected)
    {
        Assert.Equal(expected, MihomoUrlBuilder.NormalizeControllerAddress(controller, forWebSocket));
    }

    [Theory]
    [InlineData("proxy", "proxy")]
    [InlineData("HongKong-01_v2~", "HongKong-01_v2~")]
    [InlineData("a b", "a%20b")]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("a?b", "a%3Fb")]
    [InlineData("a#b", "a%23b")]
    [InlineData("a%b", "a%25b")]
    [InlineData("a+b", "a+b")]
    [InlineData("a:b", "a:b")]
    [InlineData("a@b", "a@b")]
    [InlineData("a[b]c", "a[b]c")]
    [InlineData("!$&'()*+,;=", "!$&'()*+,;=")]
    [InlineData("中国", "%E4%B8%AD%E5%9B%BD")]
    [InlineData("香港 01", "%E9%A6%99%E6%B8%AF%2001")]
    [InlineData("🇯🇵", "%F0%9F%87%AF%F0%9F%87%B5")]
    [InlineData("", "")]
    public void 路径段转义对齐urlPathSegmentEscaped语义(string segment, string expected)
    {
        Assert.Equal(expected, MihomoUrlBuilder.EscapePathSegment(segment));
    }

    [Fact]
    public void 查询值转义覆盖结构字符()
    {
        Assert.Equal(
            "https%3A%2F%2Fwww.gstatic.com%2Fgenerate_204%3Fx%3D1%26y%3D2",
            MihomoUrlBuilder.EscapeQueryValue("https://www.gstatic.com/generate_204?x=1&y=2"));
    }

    [Fact]
    public void 构建HTTPUrl_控制器路径被端点路径替换()
    {
        // 无 scheme、尾斜杠、路径前缀都按规格处理：控制器路径段整体让位
        string url = MihomoUrlBuilder.BuildHttpUrl("127.0.0.1:9090/", MihomoEndpoint.Version());
        Assert.Equal("http://127.0.0.1:9090/version", url);

        url = MihomoUrlBuilder.BuildHttpUrl("http://127.0.0.1:9090/base", MihomoEndpoint.Version());
        Assert.Equal("http://127.0.0.1:9090/version", url);
    }

    [Fact]
    public void 构建URL_端点无查询时保留控制器原有查询()
    {
        string url = MihomoUrlBuilder.BuildHttpUrl("http://127.0.0.1:9090/?token=1", MihomoEndpoint.Version());
        Assert.Equal("http://127.0.0.1:9090/version?token=1", url);
    }

    [Fact]
    public void 构建URL_端点查询覆盖控制器原有查询()
    {
        string url = MihomoUrlBuilder.BuildHttpUrl(
            "http://127.0.0.1:9090/?token=1",
            MihomoEndpoint.Connections(1000));
        Assert.Equal("http://127.0.0.1:9090/connections?interval=1000", url);
    }

    [Fact]
    public void 构建WebSocketUrl_scheme替换为ws()
    {
        string url = MihomoUrlBuilder.BuildWebSocketUrl("http://127.0.0.1:9090", MihomoEndpoint.Traffic());
        Assert.Equal("ws://127.0.0.1:9090/traffic", url);

        url = MihomoUrlBuilder.BuildWebSocketUrl("https://core.lan:9090", MihomoEndpoint.Logs("info"));
        Assert.Equal("wss://core.lan:9090/logs?level=info", url);
    }
}
