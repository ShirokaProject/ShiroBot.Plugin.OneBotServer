using System.Text.Json;
using ShiroBot.Plugin.OneBotServer.Protocol;

namespace ShiroBot.Plugin.OneBotServer.Tests;

[TestClass]
public sealed class ProtocolTests
{
    [TestMethod]
    public void Parameters_CoerceProtocolValuesAndRejectInvalidShapes()
    {
        using var document = JsonDocument.Parse("{\"id\":\"42\",\"enabled\":\"1\",\"name\":false}");
        var values = OneBotParameters.ObjectOrEmpty(document.RootElement);
        Assert.AreEqual(42L, OneBotParameters.RequiredInt(values, "id"));
        Assert.IsTrue(OneBotParameters.Bool(values, "enabled"));
        Assert.AreEqual("False", OneBotParameters.String(values, "name"));
        using var invalid = JsonDocument.Parse("[]");
        Assert.ThrowsExactly<OneBotParameterException>(() => OneBotParameters.ObjectOrEmpty(invalid.RootElement));
    }

    [TestMethod]
    public void Response_CarriesSuccessAndFailureProtocolFields()
    {
        var success = OneBotResponse<int>.Ok(7);
        var failure = OneBotResponse<object?>.Failed(1404, "unsupported");
        Assert.AreEqual("ok", success.Status);
        Assert.AreEqual(7, success.Data);
        Assert.AreEqual("failed", failure.Status);
        Assert.AreEqual(1404, failure.RetCode);
        Assert.AreEqual("unsupported", failure.Wording);
        Assert.AreEqual("{\"status\":\"ok\",\"retcode\":0,\"data\":7,\"message\":null,\"wording\":null,\"echo\":null}", JsonSerializer.Serialize(success));
    }
}
