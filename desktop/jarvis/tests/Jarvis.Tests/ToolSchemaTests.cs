using System.Text.Json.Nodes;
using Jarvis.Core.Tools;

namespace Jarvis.Tests;

public class ToolSchemaTests
{
    private static ToolDefinition Definition() =>
        new ToolDefinition("demo", "A demo tool.", ToolCategory.Planner, ToolRisk.Routine)
            .Str("title", "Title.", required: true)
            .Int("minutes", "Minutes.", min: 1, max: 240)
            .Enum("priority", "Priority.", false, "low", "high")
            .Bool("flag", "A flag.")
            .StrArray("tags", "Tags.");

    private static JsonArray Strings(params string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            JsonNode node = JsonValue.Create(value)!;
            array.Add(node);
        }
        return array;
    }

    [Fact]
    public void Well_formed_arguments_pass()
    {
        var args = new JsonObject
        {
            ["title"] = "Write tests",
            ["minutes"] = 30,
            ["priority"] = "high",
            ["flag"] = true,
            ["tags"] = Strings("a", "b")
        };
        Assert.Empty(SchemaValidator.Validate(Definition(), args));
    }

    [Fact]
    public void Only_required_arguments_are_needed()
    {
        Assert.Empty(SchemaValidator.Validate(Definition(), new JsonObject { ["title"] = "x" }));
    }

    [Fact]
    public void Missing_required_argument_is_reported()
    {
        var errors = SchemaValidator.Validate(Definition(), new JsonObject());
        Assert.Contains("Missing required argument 'title'.", errors);
    }

    [Fact]
    public void Unknown_arguments_are_rejected()
    {
        var errors = SchemaValidator.Validate(Definition(), new JsonObject { ["title"] = "x", ["bogus"] = 1 });
        Assert.Contains("Unknown argument 'bogus'.", errors);
    }

    [Fact]
    public void Numbers_are_range_checked()
    {
        var tooHigh = SchemaValidator.Validate(Definition(), new JsonObject { ["title"] = "x", ["minutes"] = 500 });
        Assert.Single(tooHigh);
        Assert.Contains("at most", tooHigh[0]);

        var tooLow = SchemaValidator.Validate(Definition(), new JsonObject { ["title"] = "x", ["minutes"] = 0 });
        Assert.Single(tooLow);
        Assert.Contains("at least", tooLow[0]);
    }

    [Fact]
    public void Integers_must_be_whole_numbers()
    {
        var errors = SchemaValidator.Validate(Definition(), new JsonObject { ["title"] = "x", ["minutes"] = 1.5 });
        Assert.Single(errors);
        Assert.Contains("whole number", errors[0]);
    }

    [Fact]
    public void Wrong_types_are_rejected()
    {
        var args = new JsonObject
        {
            ["title"] = 7,
            ["minutes"] = "thirty",
            ["flag"] = "yes",
            ["tags"] = "a,b"
        };
        var errors = SchemaValidator.Validate(Definition(), args);
        Assert.Contains("'title' must be a string.", errors);
        Assert.Contains("'minutes' must be a number.", errors);
        Assert.Contains("'flag' must be true or false.", errors);
        Assert.Contains("'tags' must be an array of strings.", errors);
    }

    [Fact]
    public void Enum_values_are_enforced()
    {
        var errors = SchemaValidator.Validate(Definition(), new JsonObject { ["title"] = "x", ["priority"] = "urgent" });
        Assert.Single(errors);
        Assert.Contains("must be one of", errors[0]);
    }

    [Fact]
    public void Arrays_may_only_contain_strings()
    {
        var mixed = new JsonArray();
        JsonNode number = JsonValue.Create(1);
        mixed.Add(number);
        var errors = SchemaValidator.Validate(Definition(), new JsonObject { ["title"] = "x", ["tags"] = mixed });
        Assert.Single(errors);
        Assert.Contains("only strings", errors[0]);
    }

    [Fact]
    public void Empty_required_text_is_rejected()
    {
        var errors = SchemaValidator.Validate(Definition(), new JsonObject { ["title"] = "   " });
        Assert.Contains("'title' must not be empty.", errors);
    }

    [Fact]
    public void Json_schema_lists_properties_types_and_required_names()
    {
        var schema = Definition().ToJsonSchema();

        Assert.Equal("object", schema["type"]!.GetValue<string>());
        var properties = schema["properties"]!.AsObject();
        Assert.Equal(5, properties.Count);
        Assert.Equal("string", properties["title"]!["type"]!.GetValue<string>());
        Assert.Equal("integer", properties["minutes"]!["type"]!.GetValue<string>());
        Assert.Equal("boolean", properties["flag"]!["type"]!.GetValue<string>());
        Assert.Equal("array", properties["tags"]!["type"]!.GetValue<string>());
        Assert.Equal("string", properties["tags"]!["items"]!["type"]!.GetValue<string>());
        Assert.Equal(2, properties["priority"]!["enum"]!.AsArray().Count);

        var requiredNames = schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "title" }, requiredNames);
    }

    [Fact]
    public void Arguments_are_read_defensively()
    {
        var args = new JsonObject
        {
            ["text"] = "  hello  ",
            ["count"] = 42,
            ["count_text"] = "17",
            ["yes"] = true,
            ["yes_text"] = "TRUE",
            ["items"] = Strings(" a ", "", "b")
        };

        Assert.Equal("hello", args.Required("text"));
        Assert.Equal("42", args.Str("count"));
        Assert.Null(args.Str("missing"));
        Assert.Equal(42, args.Int("count", 0));
        Assert.Equal(17, args.Int("count_text", 0));
        Assert.Equal(5, args.Int("missing", 5));
        Assert.True(args.Bool("yes"));
        Assert.True(args.Bool("yes_text"));
        Assert.False(args.Bool("missing"));
        Assert.Equal(new[] { "a", "b" }, args.StrList("items"));
        Assert.Empty(args.StrList("missing"));
        Assert.Throws<ToolArgumentException>(() => args.Required("missing"));
    }

    [Fact]
    public void Argument_descriptions_are_compact()
    {
        Assert.Equal("", ToolArgs.Describe(new JsonObject()));
        Assert.Equal("(name=\"notepad\", count=2)", ToolArgs.Describe(new JsonObject { ["name"] = "notepad", ["count"] = 2 }));

        var longText = ToolArgs.Describe(new JsonObject { ["text"] = new string('a', 300) });
        Assert.True(longText.Length < 100);
        Assert.EndsWith("...)", longText);
    }

    [Fact]
    public void Tool_results_serialise_for_the_model()
    {
        var ok = JsonNode.Parse(ToolResult.Ok("Opened Notepad.", verified: true).ToModelJson())!.AsObject();
        Assert.True(ok["success"]!.GetValue<bool>());
        Assert.True(ok["verified"]!.GetValue<bool>());
        Assert.Equal("Opened Notepad.", ok["message"]!.GetValue<string>());
        Assert.False(ok.ContainsKey("denied"));

        var refused = JsonNode.Parse(ToolResult.Refused("Blocked.").ToModelJson())!.AsObject();
        Assert.False(refused["success"]!.GetValue<bool>());
        Assert.True(refused["denied"]!.GetValue<bool>());
    }

    [Fact]
    public void Registry_rejects_duplicate_names_and_lists_tools_in_order()
    {
        var registry = new ToolRegistry();
        ITool Make(string name) => new DelegateTool(
            new ToolDefinition(name, "x", ToolCategory.General, ToolRisk.Safe),
            (args, context, ct) => Task.FromResult(ToolResult.Ok("ok")));

        registry.Register(Make("zeta"));
        registry.Register(Make("alpha"));

        Assert.Throws<InvalidOperationException>(() => registry.Register(Make("alpha")));
        Assert.Equal(new[] { "alpha", "zeta" }, registry.All.Select(t => t.Definition.Name).ToArray());
        Assert.True(registry.TryGet("zeta", out _));
        Assert.False(registry.TryGet("missing", out _));
    }
}
