using Jarvis.Core.Config;
using Jarvis.Core.Memory;
using Jarvis.Core.Modes;
using Jarvis.Core.Models;
using Jarvis.Core.Scheduling;
using Jarvis.Core.Search;
using Jarvis.Core.Tasks;
using Jarvis.Core.Tools;
using System.Text.Json;

namespace Jarvis.Core.Tests;

public sealed class InMemoryStores : IMemoryStore, IConversationStore, ITaskStore, IReminderStore
{
    public List<MemoryItem> Memories { get; } = [];
    public List<Conversation> Conversations { get; } = [];
    public List<ChatMessage> Messages { get; } = [];
    public List<AgentTask> Tasks { get; } = [];
    public List<Reminder> Reminders { get; } = [];

    Task<IReadOnlyList<MemoryItem>> IMemoryStore.GetAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MemoryItem>>([.. Memories]);
    Task IMemoryStore.AddAsync(MemoryItem item, CancellationToken ct) { Memories.Add(item); return Task.CompletedTask; }
    Task IMemoryStore.UpdateAsync(MemoryItem item, CancellationToken ct) { Memories.RemoveAll(m => m.Id == item.Id); Memories.Add(item); return Task.CompletedTask; }
    Task IMemoryStore.DeleteAsync(Guid id, CancellationToken ct) { Memories.RemoveAll(m => m.Id == id); return Task.CompletedTask; }
    Task IMemoryStore.ClearAllAsync(CancellationToken ct) { Memories.Clear(); return Task.CompletedTask; }

    Task<IReadOnlyList<Conversation>> IConversationStore.ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Conversation>>([.. Conversations]);
    Task<Conversation?> IConversationStore.GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Conversations.FirstOrDefault(c => c.Id == id));
    Task IConversationStore.SaveAsync(Conversation c, CancellationToken ct) { Conversations.RemoveAll(x => x.Id == c.Id); Conversations.Add(c); return Task.CompletedTask; }
    Task IConversationStore.DeleteAsync(Guid id, CancellationToken ct) { Conversations.RemoveAll(c => c.Id == id); return Task.CompletedTask; }
    Task IConversationStore.ClearAllAsync(CancellationToken ct) { Conversations.Clear(); Messages.Clear(); return Task.CompletedTask; }
    Task<IReadOnlyList<ChatMessage>> IConversationStore.GetMessagesAsync(Guid cid, CancellationToken ct) => Task.FromResult<IReadOnlyList<ChatMessage>>([.. Messages.Where(m => m.ConversationId == cid)]);
    Task IConversationStore.AddMessageAsync(ChatMessage m, CancellationToken ct) { Messages.Add(m); return Task.CompletedTask; }
    Task IConversationStore.UpdateSummaryAsync(Guid cid, string s, CancellationToken ct) => Task.CompletedTask;

    Task<IReadOnlyList<AgentTask>> ITaskStore.ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentTask>>([.. Tasks]);
    Task<AgentTask?> ITaskStore.GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Tasks.FirstOrDefault(t => t.Id == id));
    Task ITaskStore.SaveAsync(AgentTask t, CancellationToken ct) { Tasks.RemoveAll(x => x.Id == t.Id); Tasks.Add(t); return Task.CompletedTask; }
    Task ITaskStore.DeleteAsync(Guid id, CancellationToken ct) { Tasks.RemoveAll(t => t.Id == id); return Task.CompletedTask; }

    Task<IReadOnlyList<Reminder>> IReminderStore.ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Reminder>>([.. Reminders]);
    Task IReminderStore.SaveAsync(Reminder r, CancellationToken ct) { Reminders.RemoveAll(x => x.Id == r.Id); Reminders.Add(r); return Task.CompletedTask; }
    Task IReminderStore.DeleteAsync(Guid id, CancellationToken ct) { Reminders.RemoveAll(r => r.Id == id); return Task.CompletedTask; }
    Task IReminderStore.MarkCompletedAsync(Guid id, CancellationToken ct) { var r = Reminders.FirstOrDefault(x => x.Id == id); if (r is not null) { Reminders.Remove(r); Reminders.Add(r with { IsCompleted = true }); } return Task.CompletedTask; }
}

public class ToolRegistryTests
{
    [Fact]
    public void TrustedTool_MayExecuteWithoutConfirmation()
    {
        var registry = new ToolRegistry(["open_app"]);
        registry.Register(new ToolDefinition("open_app", "Open", RiskTier.Trusted, "{}", (_, _) => Task.FromResult("ok")));
        registry.Register(new ToolDefinition("delete_file", "Delete", RiskTier.Destructive, "{}", (_, _) => Task.FromResult("ok")));
        Assert.True(registry.MayExecuteWithoutConfirmation("open_app"));
        Assert.False(registry.MayExecuteWithoutConfirmation("delete_file"));
    }

    [Fact]
    public async Task UnknownTool_ReturnsFailureNotException()
    {
        var registry = new ToolRegistry([]);
        var result = await registry.ExecuteAsync("nope", JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task HandlerException_BecomesToolResult()
    {
        var registry = new ToolRegistry([]);
        registry.Register(new ToolDefinition("boom", "Boom", RiskTier.Trusted, "{}", (_, _) => throw new InvalidOperationException("kaboom")));
        var result = await registry.ExecuteAsync("boom", JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("kaboom", result.Error);
    }
}

public class MemoryTests
{
    [Fact]
    public async Task Remember_And_Recall_ByKeyword()
    {
        var store = new InMemoryStores();
        var memory = new MemoryService(store);
        await memory.RememberAsync(MemoryKind.Project, "project", "Jarvis desktop assistant", isExplicit: true);
        var hits = await memory.RecallAsync("desktop project");
        Assert.Single(hits);
    }

    [Fact]
    public async Task Remember_RejectsSecrets()
    {
        var memory = new MemoryService(new InMemoryStores());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            memory.RememberAsync(MemoryKind.Other, "key", "my api key is abc123", isExplicit: true));
    }
}

public class ModeTests
{
    [Fact]
    public void Modes_HaveDifferentToolSets()
    {
        Assert.True(OperatingModes.ToolAllowedInMode(OperatingMode.Development, "open_app"));
        Assert.False(OperatingModes.ToolAllowedInMode(OperatingMode.Research, "open_app"));
        Assert.True(OperatingModes.ToolAllowedInMode(OperatingMode.System, "get_system_info"));
    }

    [Fact]
    public void Parse_ModeNames()
    {
        Assert.Equal(OperatingMode.Study, OperatingModes.Parse("study"));
        Assert.Equal(OperatingMode.Development, OperatingModes.Parse("dev"));
        Assert.Equal(OperatingMode.Personal, OperatingModes.Parse("unknown"));
    }
}

public class ReminderTests
{
    [Fact]
    public void TryParseDue_Tomorrow7Pm()
    {
        var now = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
        var due = ReminderScheduler.TryParseDue("remind me tomorrow at 7pm", now);
        Assert.NotNull(due);
        Assert.Equal(15, due!.Value.Day);
        Assert.Equal(19, due.Value.Hour);
    }

    [Fact]
    public void TryParseDue_In2Hours()
    {
        var now = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
        var due = ReminderScheduler.TryParseDue("in 2 hours", now);
        Assert.Equal(now.AddHours(2), due);
    }

    [Fact]
    public async Task CheckDue_FiresOverdue_And_CompletesOneTime()
    {
        var store = new InMemoryStores();
        var fired = new List<Reminder>();
        var scheduler = new ReminderScheduler(store, r => { fired.Add(r); return Task.CompletedTask; });
        await ((IReminderStore)store).SaveAsync(new Reminder(Guid.NewGuid(), "due", DateTimeOffset.Now.AddMinutes(-5), ReminderKind.OneTime, null, false, DateTimeOffset.Now));
        await scheduler.CheckDueAsync();
        Assert.Single(fired);
        Assert.True((await ((IReminderStore)store).ListAsync()).Single().IsCompleted);
    }
}

public class TaskEngineTests
{
    [Fact]
    public async Task Task_RunsSteps_ToCompletion()
    {
        var store = new InMemoryStores();
        var registry = new ToolRegistry([]);
        registry.Register(new ToolDefinition("ping", "Ping", RiskTier.Trusted, "{}", (_, _) => Task.FromResult("pong")));
        var engine = new TaskEngine(store, registry);
        var task = await engine.CreateAsync("Test", "desc", [new TaskStep("Ping", "ping", "{}")]);
        await engine.StartAsync(task.Id);
        await Task.Delay(500);
        var done = await ((ITaskStore)store).GetAsync(task.Id);
        Assert.Equal(TaskStatus.Completed, done!.Status);
        Assert.Equal("pong", done.Result);
    }

    [Fact]
    public async Task RecoverAfterRestart_RunningBecomesPaused()
    {
        var store = new InMemoryStores();
        var engine = new TaskEngine(store, new ToolRegistry([]));
        var now = DateTimeOffset.Now;
        await ((ITaskStore)store).SaveAsync(new AgentTask(Guid.NewGuid(), "t", "d", TaskStatus.Running, [], 0, null, null, now, now));
        await engine.RecoverAfterRestartAsync();
        Assert.Equal(TaskStatus.Paused, (await ((ITaskStore)store).ListAsync()).Single().Status);
    }
}

public class SearchTests
{
    [Fact]
    public void Search_FindsFileByName_And_Content()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "notes.txt"), "semiconductor physics revision");
            File.WriteAllText(Path.Combine(dir.FullName, "other.txt"), "unrelated");
            var search = new FileSearchService();
            var hits = search.Search([dir.FullName], "semiconductor");
            Assert.Contains(hits, h => h.FileName == "notes.txt");
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void FindDuplicates_DetectsIdenticalContent()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.txt"), "same");
            File.WriteAllText(Path.Combine(dir.FullName, "b.txt"), "same");
            File.WriteAllText(Path.Combine(dir.FullName, "c.txt"), "different");
            var groups = new FileSearchService().FindDuplicates(dir.FullName);
            Assert.Single(groups);
            Assert.Equal(2, groups[0].Paths.Count);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Categorise_MapsExtensions()
    {
        Assert.Equal("Documents", FileSearchService.Categorise("report.pdf"));
        Assert.Equal("Images", FileSearchService.Categorise("photo.PNG"));
        Assert.Equal("Code", FileSearchService.Categorise("Program.cs"));
    }
}

public class SettingsTests
{
    [Fact]
    public void Settings_RoundTrip_PreservesValues()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            var settings = new JarvisSettings { GeminiModel = "gemini-2.5-flash", StartWithWindows = false };
            SettingsStore.Save(path, settings);
            var loaded = SettingsStore.Load(path);
            Assert.Equal("gemini-2.5-flash", loaded.GeminiModel);
            Assert.False(loaded.StartWithWindows);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Settings_CorruptFile_ReturnsDefaults()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            File.WriteAllText(path, "{ not json !!!");
            var loaded = SettingsStore.Load(path);
            Assert.NotNull(loaded);
        }
        finally { dir.Delete(recursive: true); }
    }
}
