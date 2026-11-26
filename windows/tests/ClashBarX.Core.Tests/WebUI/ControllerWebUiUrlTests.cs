using ClashBarX.Core.WebUI;

namespace ClashBarX.Core.Tests.WebUI;

/// <summary>WebUI 入口地址构建规格（源：makeControllerUIURL 两分支逐行对照）。</summary>
public class ControllerWebUiUrlTests
{
    [Fact]
    public void Configured_ExternalUi_PointsAtControllerUiPath()
    {
        string url = ControllerWebUiUrl.Build(
            "127.0.0.1:9090", secret: null, externalUiConfigured: true, externalUiName: null);

        Assert.Equal("http://127.0.0.1:9090/ui", url);
    }

    [Fact]
    public void Configured_ExternalUiWithAppendsEncodedName()
    {
        string url = ControllerWebUiUrl.Build(
            "192.168.1.5:9090", secret: null, externalUiConfigured: true, externalUiName: "metacubexd");

        Assert.Equal("http://192.168.1.5:9090/ui/metacubexd", url);
    }

    [Fact]
    public void Unconfigured_UsesMetaCubeXdSetupGuide()
    {
        // 源：未配置 external-ui → metacubexd setup 页带 controller 参数（引导对应物）
        string url = ControllerWebUiUrl.Build(
            "127.0.0.1:9090", secret: "s3cret", externalUiConfigured: false, externalUiName: null);

        Assert.StartsWith(ControllerWebUiUrl.MetaCubeXdSetupBaseUrl + "#/setup?", url);
        Assert.Contains("http=true", url);
        Assert.Contains("hostname=127.0.0.1", url);
        Assert.Contains("port=9090", url);
        Assert.Contains("secret=s3cret", url);
    }

    [Fact]
    public void Unconfigured_HttpsControllerUsesHttpFalseAndDefaultPort()
    {
        string url = ControllerWebUiUrl.Build(
            "https://core.example.com", secret: null, externalUiConfigured: false, externalUiName: null);

        Assert.Contains("http=false", url);
        Assert.Contains("hostname=core.example.com", url);
        Assert.Contains("port=443", url);
        Assert.Contains("secret=", url);   // 空 secret 保留空值
    }

    [Theory]
    [InlineData("127.0.0.1:9090", "http://127.0.0.1:9090")]
    [InlineData("http://127.0.0.1:9090", "http://127.0.0.1:9090")]
    [InlineData("https://core.example.com", "https://core.example.com")]
    [InlineData("  127.0.0.1:9090  ", "http://127.0.0.1:9090")]
    public void NormalizeControllerAddress_PreservesSchemeAndAddsHttp(string input, string expected)
    {
        Assert.Equal(expected, ControllerWebUiUrl.NormalizeControllerAddress(input));
    }
}
