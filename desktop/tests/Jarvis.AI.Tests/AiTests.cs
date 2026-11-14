using System.Net;
using System.Text;
using Jarvis.AI;
using Jarvis.Core.Config;
using Jarvis.Core.Memory;
using Jarvis.Core.Models;
using Jarvis.Core.Tools;
using Jarvis.Core.Tests;

namespace Jarvis.AI.Tests;

public sealed class StubHandler(string responseJson) : HttpMessageHandler
{
    public string? LastRequestBody { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequestBody = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        });
    }
}

public class GeminiClientTests
{
    [Fact]
    public async Task SendAsync_ParsesTextReply()
    {
        var handler = new StubHandler("""{"candidates":[{"content":{"parts":[{"text":"Hello there"}]}}]}""");
        var client = new GeminiClient(new HttpClient(handler), "test-key", "gemini-2.5-flash");
        var reply = await client.SendAsync([], "sys", "hi", null);
        Assert.Equal("Hello there", reply.Text);
        Assert.Null(reply.Error);
    }

    [Fact]
    public async Task SendAsync_ParsesFunctionCall()
    {
        var handler = new StubHandler("""{"candidates":[{"content":{"parts":[{"functionCall":{"name":"open_app","args":{"name":"notepad"}}}]}}]}""");
        var client = new GeminiClient(new HttpClient(handler), "test-key", "gemini-2.5-flash");
        var reply = await client.SendAsync([], "sys", "open notepad", null);
        Assert.NotNull(reply.FunctionCall);
        Assert.Equal("open_app", reply.FunctionCall!.Name);
    }

    [Fact]
    public async Task SendAsync_NoKey_ReturnsClearError()
    {
        var client = new GeminiClient(new HttpClient(), "", "gemini-2.5-flash");
        var reply = await client.SendAsync([], "sys", "hi", null);
        Assert.Contains("No Gemini API key", reply.Error);
    }
}

public class ConversationServiceTests
{
    [Fact]
    public async Task OfflineFallback_AnswersLocally_And_LabelsProvenance()
    {
        var store = new InMemoryStores();
        var memory = new MemoryService(store);
        var settings = new JarvisSettings();
        var service = new ConversationService(null, store, memory, new ToolRegistry([]), settings);
        var convId = Guid.NewGuid();
        await ((IConversationStore)store).SaveAsync(new Conversation(convId, "New conversation", DateTimeOffset.Now, DateTimeOffset.Now));
        var result = await service.HandleUserMessageAsync(convId, "what can you do");
        Assert.Equal(ReplyProvenance.Local, result.Provenance);
        Assert.Contains("locally", result.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OfflineFallback_Remember_StoresMemory()
    {
        var store = new InMemoryStores();
        var memory = new MemoryService(store);
        var service = new ConversationService(null, store, memory, new ToolRegistry([]), new JarvisSettings());
        var convId = Guid.NewGuid();
        await ((IConversationStore)store).SaveAsync(new Conversation(convId, "New conversation", DateTimeOffset.Now, DateTimeOffset.Now));
        await service.HandleUserMessageAsync(convId, "remember project: Jarvis desktop");
        var all = await memory.ListAllAsync();
        Assert.Contains(all, m => m.Key == "project");
    }
}
