using System.Text.Json;
using FluentAssertions;
using KodaClaw.ChannelHub.Connectors.WeChat;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using Xunit;

namespace KodaClaw.ContractTests.Channels;

public sealed class WeChatApiContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ILinkGetUpdatesResponse_should_deserialize_from_api_shape()
    {
        const string json = """
            {
              "ret": 0,
              "msgs": [
                {
                  "message_id": 10010001,
                  "from_user_id": "USER_A",
                  "context_token": "CTX_TOKEN_001",
                  "item_list": [
                    { "type": 1, "text_item": { "text": "hello from wechat" } }
                  ]
                }
              ],
              "get_updates_buf": "NEXT_CURSOR_OPAQUE"
            }
            """;

        var result = JsonSerializer.Deserialize<ILinkGetUpdatesResponse>(json, JsonOptions);

        result.Should().NotBeNull();
        result!.Ret.Should().Be(0);
        result.Msgs.Should().ContainSingle();
        result.Msgs[0].MessageId.Should().Be(10010001L);
        result.Msgs[0].FromUserId.Should().Be("USER_A");
        result.Msgs[0].ContextToken.Should().Be("CTX_TOKEN_001");
        result.Msgs[0].ItemList.Should().ContainSingle();
        result.Msgs[0].ItemList[0].Type.Should().Be(1);
        result.Msgs[0].ItemList[0].TextItem!.Text.Should().Be("hello from wechat");
        result.GetUpdatesBuf.Should().Be("NEXT_CURSOR_OPAQUE");
    }

    [Fact]
    public void ILinkQrCodeResponse_should_deserialize_from_api_shape()
    {
        const string json = """
            {
              "qrcode": "QRCODE_TOKEN_001",
              "qrcode_img_content": "data:image/png;base64,ABC123=="
            }
            """;

        var result = JsonSerializer.Deserialize<ILinkQrCodeResponse>(json, JsonOptions);

        result.Should().NotBeNull();
        result!.Qrcode.Should().Be("QRCODE_TOKEN_001");
        result.QrcodeImgContent.Should().Be("data:image/png;base64,ABC123==");
    }

    [Fact]
    public void ILinkQrCodeStatusResponse_should_deserialize_wait_and_confirmed_shape()
    {
        const string waitJson = """{"status":"wait"}""";
        const string confirmedJson = """
            {
              "status": "confirmed",
              "bot_token": "BOT_TOKEN_XYZ",
              "ilink_bot_id": "BOT_ID_001"
            }
            """;

        var wait = JsonSerializer.Deserialize<ILinkQrCodeStatusResponse>(waitJson, JsonOptions);
        var confirmed = JsonSerializer.Deserialize<ILinkQrCodeStatusResponse>(confirmedJson, JsonOptions);

        wait.Should().NotBeNull();
        wait!.Status.Should().Be("wait");
        wait.BotToken.Should().BeNullOrEmpty();

        confirmed.Should().NotBeNull();
        confirmed!.Status.Should().Be("confirmed");
        confirmed.BotToken.Should().Be("BOT_TOKEN_XYZ");
        confirmed.ILinkBotId.Should().Be("BOT_ID_001");
    }

    [Fact]
    public void WeChatQrCodeResult_and_WeChatQrCodeStatus_contracts_should_json_round_trip()
    {
        var qrResult = new WeChatQrCodeResult(
            Qrcode: "QR_TOKEN_001",
            QrcodeImgUrl: "data:image/png;base64,ABC==");

        var qrStatus = new WeChatQrCodeStatus(
            Status: "confirmed",
            BotToken: "BOT_TOKEN_001");

        var qrResultJson = JsonSerializer.Serialize(qrResult, JsonOptions);
        var qrStatusJson = JsonSerializer.Serialize(qrStatus, JsonOptions);

        var qrResultRound = JsonSerializer.Deserialize<WeChatQrCodeResult>(qrResultJson, JsonOptions);
        var qrStatusRound = JsonSerializer.Deserialize<WeChatQrCodeStatus>(qrStatusJson, JsonOptions);

        qrResultJson.Should().Contain("\"qrcode\"");
        qrResultJson.Should().Contain("\"qrcodeImgUrl\"");
        qrResultRound.Should().NotBeNull();
        qrResultRound!.Qrcode.Should().Be("QR_TOKEN_001");

        qrStatusJson.Should().Contain("\"status\":\"confirmed\"");
        qrStatusJson.Should().Contain("\"botToken\":\"BOT_TOKEN_001\"");
        qrStatusRound.Should().NotBeNull();
        qrStatusRound!.Status.Should().Be("confirmed");
        qrStatusRound.BotToken.Should().Be("BOT_TOKEN_001");
    }
}
