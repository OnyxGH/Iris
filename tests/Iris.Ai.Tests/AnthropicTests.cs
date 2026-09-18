using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Iris.Ai.Tests;

public class AnthropicTests
{
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Serve one Anthropic Messages SSE response whose message_start reports responseModel.</summary>
    private static async Task ServeAsync(HttpListener listener, string responseModel)
    {
        var context = await listener.GetContextAsync();
        using (var reader = new StreamReader(context.Request.InputStream)) await reader.ReadToEndAsync();
        context.Response.ContentType = "text/event-stream";
        string Event(string type, JsonObject data)
        {
            data["type"] = type;
            return $"event: {type}\ndata: {data.ToJsonString()}\n\n";
        }
        var body = string.Concat(
            Event("message_start", new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["id"] = "msg_1", ["model"] = responseModel,
                    ["usage"] = new JsonObject { ["input_tokens"] = 1_000_000, ["output_tokens"] = 0 },
                },
            }),
            Event("content_block_start", new JsonObject { ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } }),
            Event("content_block_delta", new JsonObject { ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = "hi" } }),
            Event("content_block_stop", new JsonObject { ["index"] = 0 }),
            Event("message_delta", new JsonObject { ["delta"] = new JsonObject { ["stop_reason"] = "end_turn" }, ["usage"] = new JsonObject { ["output_tokens"] = 0 } }),
            Event("message_stop", new JsonObject()));
        var bytes = Encoding.UTF8.GetBytes(body);
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    [Fact]
    public async Task Fallback_response_keeps_requested_model_and_records_response_model()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var server = ServeAsync(listener, "claude-opus-5");

        var model = new Model
        {
            Id = "claude-fable-5", Name = "Fable", Api = "anthropic-messages", Provider = "anthropic", BaseUrl = $"http://127.0.0.1:{port}",
            Input = ["text"], ContextWindow = 200000, MaxTokens = 1000, Cost = new ModelCost { Input = 10, Output = 50 },
            Compat = JsonNode.Parse("""{"allowedFallbackModels":[{"provider":"anthropic","model":"claude-opus-5","cost":{"input":5,"output":25,"cacheRead":0.5,"cacheWrite":6.25}}]}""")!.AsObject(),
        };
        var context = new Context { Messages = [new UserMessage(UserContent.FromText("hello"))] };
        var message = await IrisAi.CompleteAsync(model, context, new StreamOptions { ApiKey = "test" });
        await server;

        Assert.Equal(StopReason.Stop, message.StopReason);
        Assert.Equal("claude-fable-5", message.Model);
        Assert.Equal("claude-opus-5", message.ResponseModel);
        Assert.Equal(5, message.Usage.Cost.Input, 6);
    }
}
