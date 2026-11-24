using System.Text;
using Fusebox.Application.Llm;
using Fusebox.Infrastructure.Llm;

namespace Fusebox.Infrastructure.Tests;

public class OpenAiChatProtocolTests
{
    private readonly OpenAiChatProtocol _protocol = new();

    private static ReadOnlyMemory<byte> Json(string json) => Encoding.UTF8.GetBytes(json);

    /// <summary>Parses a body the test expects to be valid.</summary>
    private LlmRequestInfo Parse(string json)
    {
        Assert.True(_protocol.TryParseRequest(Json(json), out var request));
        return request!;
    }

    [Fact]
    public void Reads_the_cost_relevant_facts_from_a_request()
    {
        var parsed = _protocol.TryParseRequest(
            Json("""{"model":"gpt-4o-mini","max_tokens":200,"messages":[{"role":"user","content":"Hello there"}]}"""),
            out var request);

        Assert.True(parsed);
        Assert.Equal("gpt-4o-mini", request!.Model);
        Assert.Equal(200, request.MaxOutputTokens);
        Assert.Equal(1, request.Choices);
        Assert.Equal("Hello there".Length, request.PromptCharacters);
        Assert.False(request.IsStreaming);
    }

    [Fact]
    public void Prefers_max_completion_tokens_over_the_older_max_tokens()
    {
        var request = Parse("""{"model":"m","max_tokens":10,"max_completion_tokens":20,"messages":[]}""");

        Assert.Equal(20, request.MaxOutputTokens);
    }

    [Fact]
    public void Reports_no_output_cap_when_none_is_set()
    {
        var request = Parse("""{"model":"m","messages":[]}""");

        Assert.Null(request.MaxOutputTokens);
    }

    [Fact]
    public void Reads_the_number_of_choices_and_the_streaming_flag()
    {
        var request = Parse("""{"model":"m","n":3,"stream":true,"messages":[]}""");

        Assert.Equal(3, request.Choices);
        Assert.True(request.IsStreaming);
    }

    [Fact]
    public void Counts_text_in_both_string_and_multi_part_content()
    {
        var request = Parse("""
            {"model":"m","messages":[
              {"role":"system","content":"abcd"},
              {"role":"user","content":[{"type":"text","text":"efghij"},{"type":"image_url","image_url":{"url":"x"}}]}
            ]}
            """);

        Assert.Equal(10, request.PromptCharacters);
    }

    [Fact]
    public void Counts_tool_definitions_as_part_of_the_prompt()
    {
        var request = Parse("""{"model":"m","messages":[],"tools":[{"type":"function","function":{"name":"lookup"}}]}""");

        Assert.True(request.PromptCharacters > 20);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"messages":[]}""")]
    [InlineData("""{"model":"","messages":[]}""")]
    [InlineData("""{"model":5,"messages":[]}""")]
    [InlineData("""{"model":"m"}""")]
    [InlineData("""{"model":"m","messages":"hi"}""")]
    [InlineData("""{"model":"m","messages":[],"max_tokens":0}""")]
    [InlineData("""{"model":"m","messages":[],"max_tokens":-5}""")]
    [InlineData("""{"model":"m","messages":[],"max_tokens":"lots"}""")]
    [InlineData("""{"model":"m","messages":[],"max_tokens":99999999999}""")]
    [InlineData("""{"model":"m","messages":[],"n":100000}""")]
    public void Rejects_a_body_that_is_not_a_valid_request(string body)
    {
        Assert.False(_protocol.TryParseRequest(Json(body), out var request));
        Assert.Null(request);
    }

    [Fact]
    public void Reads_token_usage_from_a_response()
    {
        var found = _protocol.TryExtractUsage(
            Json("""{"id":"x","choices":[],"usage":{"prompt_tokens":12,"completion_tokens":34,"total_tokens":46}}"""),
            out var usage);

        Assert.True(found);
        Assert.Equal(12, usage.InputTokens);
        Assert.Equal(34, usage.OutputTokens);
    }

    [Theory]
    [InlineData("""{"model":"m","messages":[],"stream":true}""", false)]
    [InlineData("""{"model":"m","messages":[],"stream":true,"stream_options":{}}""", false)]
    [InlineData("""{"model":"m","messages":[],"stream":true,"stream_options":{"include_usage":false}}""", false)]
    [InlineData("""{"model":"m","messages":[],"stream":true,"stream_options":{"include_usage":true}}""", true)]
    public void Knows_whether_the_caller_asked_for_usage_in_the_stream(string body, bool expected)
    {
        Assert.Equal(expected, Parse(body).ClientRequestedStreamUsage);
    }

    [Fact]
    public void Asks_the_vendor_to_report_usage_in_the_stream_and_keeps_everything_else()
    {
        var rewritten = Encoding.UTF8.GetString(_protocol.WithStreamUsage(
            Json("""{"model":"m","stream":true,"messages":[{"role":"user","content":"hi"}],"stream_options":{"include_obfuscation":false,"include_usage":false}}""")).Span);

        using var document = System.Text.Json.JsonDocument.Parse(rewritten);
        var root = document.RootElement;

        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.False(root.GetProperty("stream_options").GetProperty("include_obfuscation").GetBoolean());
        Assert.Equal("m", root.GetProperty("model").GetString());
        Assert.Equal("hi", root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void Adds_stream_options_when_the_request_had_none()
    {
        var rewritten = Encoding.UTF8.GetString(_protocol.WithStreamUsage(Json("""{"model":"m","messages":[]}""")).Span);

        Assert.Contains("\"include_usage\":true", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_a_body_it_cannot_read_unchanged()
    {
        Assert.Equal("not json", Encoding.UTF8.GetString(_protocol.WithStreamUsage(Json("not json")).Span));
    }

    [Fact]
    public void Reads_usage_from_the_final_streamed_event()
    {
        var found = _protocol.TryReadStreamUsage(
            Json("""{"id":"c1","choices":[],"usage":{"prompt_tokens":11,"completion_tokens":22}}"""), out var usage);

        Assert.True(found);
        Assert.Equal(11, usage.Tokens.InputTokens);
        Assert.Equal(22, usage.Tokens.OutputTokens);
        Assert.True(usage.IsUsageOnly);
    }

    [Fact]
    public void Does_not_call_an_event_usage_only_when_it_also_carries_content()
    {
        Assert.True(_protocol.TryReadStreamUsage(
            Json("""{"choices":[{"delta":{"content":"hi"}}],"usage":{"prompt_tokens":1,"completion_tokens":2}}"""), out var usage));
        Assert.False(usage.IsUsageOnly);
    }

    [Theory]
    [InlineData("""{"choices":[{"delta":{"content":"hi"}}],"usage":null}""")]
    [InlineData("""{"choices":[{"delta":{"content":"hi"}}]}""")]
    [InlineData("[DONE]")]
    [InlineData("not json")]
    [InlineData("""{"choices":[],"usage":{"prompt_tokens":1}}""")]
    public void Reports_no_usage_for_streamed_events_that_carry_none(string data)
    {
        Assert.False(_protocol.TryReadStreamUsage(Json(data), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"usage":null}""")]
    [InlineData("""{"usage":{"prompt_tokens":1}}""")]
    [InlineData("""{"usage":{"prompt_tokens":-1,"completion_tokens":2}}""")]
    [InlineData("""{"usage":{"prompt_tokens":"1","completion_tokens":2}}""")]
    public void Reports_no_usage_when_the_response_has_none_it_can_trust(string body)
    {
        Assert.False(_protocol.TryExtractUsage(Json(body), out _));
    }
}
