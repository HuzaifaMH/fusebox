using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Fusebox.Application.Llm;
using Fusebox.Domain;
using Fusebox.Gateway.Middleware;

namespace Fusebox.Gateway.Tests;

public sealed class SseUsageStreamTests
{
    private const string Token = "data: {\"tok\":\"a\",\"usage\":null}\n\n";
    private const string UsageEvent = "data: {\"usage\":{\"in\":5,\"out\":7},\"only\":true}\n\n";

    private readonly MemoryStream _client = new();

    private SseUsageStream Create(bool hideUsageEvent = true, int maxPendingBytes = 4096) =>
        new(_client, new FakeProtocol(), hideUsageEvent, maxPendingBytes);

    private string Received => Encoding.UTF8.GetString(_client.ToArray());

    private static async Task WriteAsync(Stream stream, string text) =>
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Forwards_ordinary_events_unchanged()
    {
        var stream = Create();

        await WriteAsync(stream, Token + Token + "data: [DONE]\n\n");

        Assert.Equal(Token + Token + "data: [DONE]\n\n", Received);
        Assert.Null(stream.Usage);
    }

    [Fact]
    public async Task Reads_usage_and_withholds_the_usage_event_from_a_caller_who_did_not_ask_for_it()
    {
        var stream = Create(hideUsageEvent: true);

        await WriteAsync(stream, Token + UsageEvent + "data: [DONE]\n\n");

        Assert.Equal(new TokenUsage(5, 7), stream.Usage);
        Assert.Equal(Token + "data: [DONE]\n\n", Received);
    }

    [Fact]
    public async Task Forwards_the_usage_event_to_a_caller_who_asked_for_it()
    {
        var stream = Create(hideUsageEvent: false);

        await WriteAsync(stream, Token + UsageEvent);

        Assert.Equal(new TokenUsage(5, 7), stream.Usage);
        Assert.Equal(Token + UsageEvent, Received);
    }

    [Fact]
    public async Task Holds_an_event_back_only_until_it_is_complete()
    {
        var stream = Create();

        await WriteAsync(stream, "data: {\"tok\":\"a\",");
        Assert.Equal(string.Empty, Received);

        await WriteAsync(stream, "\"usage\":null}\n\ndata: {\"tok\"");
        Assert.Equal(Token, Received);

        await WriteAsync(stream, ":\"b\"}\n\n");
        Assert.Equal(Token + "data: {\"tok\":\"b\"}\n\n", Received);
    }

    [Fact]
    public async Task Finds_usage_in_an_event_split_across_writes()
    {
        var stream = Create();

        await WriteAsync(stream, "data: {\"usage\":{\"in\":5,");
        await WriteAsync(stream, "\"out\":7},\"only\":true}\n");
        await WriteAsync(stream, "\n");

        Assert.Equal(new TokenUsage(5, 7), stream.Usage);
        Assert.Equal(string.Empty, Received);
    }

    [Fact]
    public async Task Understands_events_that_end_with_carriage_returns()
    {
        var stream = Create();

        await WriteAsync(stream, "data: {\"usage\":{\"in\":1,\"out\":2},\"only\":true}\r\n\r\ndata: [DONE]\r\n\r\n");

        Assert.Equal(new TokenUsage(1, 2), stream.Usage);
        Assert.Equal("data: [DONE]\r\n\r\n", Received);
    }

    [Fact]
    public async Task Forwards_an_unfinished_final_event_when_completed()
    {
        var stream = Create();

        await WriteAsync(stream, Token + "data: {\"tok\":\"tail\"}");
        Assert.Equal(Token, Received);

        await stream.CompleteAsync();

        Assert.Equal(Token + "data: {\"tok\":\"tail\"}", Received);
    }

    [Fact]
    public async Task Stops_holding_back_an_event_that_grows_too_large_and_says_usage_may_be_missed()
    {
        var stream = Create(maxPendingBytes: 64);
        var huge = "data: " + new string('x', 200);

        await WriteAsync(stream, huge);

        Assert.Equal(huge, Received);
        Assert.True(stream.Overflowed);
    }

    [Fact]
    public async Task Does_not_misread_an_event_that_only_mentions_usage_in_text()
    {
        var stream = Create();
        var text = "data: {\"tok\":\"the word \\\"usage\\\" appears here\"}\n\n";

        await WriteAsync(stream, text);

        Assert.Null(stream.Usage);
        Assert.Equal(text, Received);
    }

    /// <summary>A minimal wire format: an event is usage if it has {"usage":{"in":N,"out":N}}.</summary>
    private sealed class FakeProtocol : ILlmProtocol
    {
        public string Name => "fake";

        public bool TryParseRequest(ReadOnlyMemory<byte> body, [NotNullWhen(true)] out LlmRequestInfo? request) =>
            throw new NotSupportedException();

        public bool TryExtractUsage(ReadOnlyMemory<byte> responseBody, out TokenUsage usage) =>
            throw new NotSupportedException();

        public ReadOnlyMemory<byte> WithStreamUsage(ReadOnlyMemory<byte> requestBody) =>
            throw new NotSupportedException();

        public bool TryReadStreamUsage(ReadOnlyMemory<byte> eventData, out StreamUsage usage)
        {
            usage = default;

            try
            {
                using var document = JsonDocument.Parse(eventData);

                if (!document.RootElement.TryGetProperty("usage", out var element) || element.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                usage = new StreamUsage(
                    new TokenUsage(element.GetProperty("in").GetInt64(), element.GetProperty("out").GetInt64()),
                    document.RootElement.TryGetProperty("only", out var only) && only.GetBoolean());
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
