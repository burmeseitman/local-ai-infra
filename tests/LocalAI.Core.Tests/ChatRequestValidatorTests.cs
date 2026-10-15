using LocalAI.Core.Models;
using LocalAI.Core.Services;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Tests;

public class ChatRequestValidatorTests
{
    private static readonly ChatOptions Limits = new()
    {
        MaxMessageLength = 20,
        MaxConversationLength = 30,
        MaxMessages = 3,
        DefaultMaxTokens = 100,
        MaxTokensLimit = 500,
    };

    private static readonly ChatRequestValidator Validator = new(Options.Create(Limits));

    private static UnvalidatedChatRequest Request(
        string? model = "tiny:1b",
        IReadOnlyList<UnvalidatedChatMessage>? messages = null,
        double? temperature = null,
        int? maxTokens = null) =>
        new(model, messages ?? [new("user", "Hello")], temperature, maxTokens);

    [Fact]
    public void Valid_request_is_normalized_and_uses_default_max_tokens()
    {
        var result = Validator.Validate(Request(model: "  tiny:1b ", messages: [new("system", "Be brief"), new("USER", "Hi")]));

        Assert.True(result.IsValid);
        Assert.Equal("tiny:1b", result.Request!.Model);
        Assert.Equal([ChatRole.System, ChatRole.User], result.Request.Messages.Select(m => m.Role));
        Assert.Equal(100, result.Request.Options.MaxTokens);
        Assert.Null(result.Request.Options.Temperature);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_model_is_rejected(string? model)
    {
        var result = Validator.Validate(Request(model: model));

        Assert.False(result.IsValid);
        Assert.Contains("model", result.Errors.Keys);
    }

    [Theory]
    [InlineData("bad model")]
    [InlineData("../etc/passwd:x")]
    [InlineData(":latest")]
    [InlineData("model;rm -rf")]
    [InlineData("tiny:1b:extra")]
    public void Invalid_model_identifiers_are_rejected(string model)
    {
        var result = Validator.Validate(Request(model: model));

        Assert.Equal(["The model identifier is not valid."], result.Errors["model"]);
    }

    [Theory]
    [InlineData("qwen2.5:0.5b")]
    [InlineData("llama3.2")]
    [InlineData("library/llama3.2:1b-instruct-q4_K_M")]
    public void Realistic_model_identifiers_are_accepted(string model) =>
        Assert.True(ChatRequestValidator.IsValidModelName(model));

    [Fact]
    public void Model_identifier_longer_than_200_chars_is_rejected() =>
        Assert.False(ChatRequestValidator.IsValidModelName(new string('a', 201)));

    [Fact]
    public void Missing_or_empty_message_list_is_rejected()
    {
        Assert.Contains("messages", Validator.Validate(new UnvalidatedChatRequest("tiny:1b", null, null, null)).Errors.Keys);
        Assert.Contains("messages", Validator.Validate(Request(messages: [])).Errors.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n ")]
    public void Empty_or_whitespace_content_is_rejected(string? content)
    {
        var result = Validator.Validate(Request(messages: [new("user", content)]));

        Assert.Contains("messages[0].content", result.Errors.Keys);
    }

    [Fact]
    public void Excessively_long_message_is_rejected()
    {
        var result = Validator.Validate(Request(messages: [new("user", new string('x', 21))]));

        Assert.Contains("at most 20 characters", Assert.Single(result.Errors["messages[0].content"]), StringComparison.Ordinal);
    }

    [Fact]
    public void Conversation_over_total_length_is_rejected()
    {
        var result = Validator.Validate(Request(messages: [new("user", new string('x', 20)), new("assistant", "y"), new("user", new string('z', 15))]));

        Assert.Contains(result.Errors["messages"], e => e.Contains("in total", StringComparison.Ordinal));
    }

    [Fact]
    public void Too_many_messages_are_rejected()
    {
        var result = Validator.Validate(Request(messages: Enumerable.Repeat(new UnvalidatedChatMessage("user", "x"), 4).ToArray()));

        Assert.Contains("at most 3 messages", Assert.Single(result.Errors["messages"]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("1")]
    [InlineData(null)]
    public void Unsupported_roles_are_rejected(string? role)
    {
        var result = Validator.Validate(Request(messages: [new(role, "x"), new("user", "y")]));

        Assert.Contains("messages[0].role", result.Errors.Keys);
    }

    [Fact]
    public void Last_message_must_come_from_the_user()
    {
        var result = Validator.Validate(Request(messages: [new("user", "x"), new("assistant", "y")]));

        Assert.Contains("The last message must have the 'user' role.", result.Errors["messages"]);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(2.01)]
    [InlineData(double.NaN)]
    public void Temperature_out_of_range_is_rejected(double temperature) =>
        Assert.Contains("temperature", Validator.Validate(Request(temperature: temperature)).Errors.Keys);

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void Max_tokens_out_of_range_is_rejected(int maxTokens) =>
        Assert.Contains("maxTokens", Validator.Validate(Request(maxTokens: maxTokens)).Errors.Keys);

    [Fact]
    public void Options_validator_rejects_inconsistent_limits()
    {
        var result = new ChatOptionsValidator().Validate(null, new ChatOptions
        {
            DefaultMaxTokens = 10,
            MaxTokensLimit = 5,
            DefaultModel = "not valid!",
        });

        Assert.True(result.Failed);
        Assert.Equal(2, result.Failures!.Count());
    }
}
