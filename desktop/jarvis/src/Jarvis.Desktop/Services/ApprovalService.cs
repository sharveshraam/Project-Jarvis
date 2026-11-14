using Jarvis.Core.Tools;
using Jarvis.Desktop.Mvvm;

namespace Jarvis.Desktop.Services;

/// <summary>One pending "may I do this?" question shown to the user.</summary>
public sealed class ApprovalPrompt : ObservableObject
{
    private bool _remember;

    public ApprovalPrompt(ApprovalRequest request)
    {
        Request = request;
        Completion = new TaskCompletionSource<ApprovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public ApprovalRequest Request { get; }
    internal TaskCompletionSource<ApprovalResult> Completion { get; }

    public string Summary => Request.Summary;
    public IReadOnlyList<string> Details => Request.Details;
    public bool HasDetails => Request.Details.Count > 0;
    public bool CanRemember => Request.CanRemember;
    public string Origin => Request.IsBackground ? "A background task wants to:" : "JARVIS wants to:";

    public string RiskText => Request.Risk switch
    {
        ToolRisk.Consequential => "This action is hard to undo, affects many items, or shares data — it always needs your approval.",
        ToolRisk.Routine => "Routine action. You chose to be asked before it runs.",
        _ => "You chose to be asked before this runs."
    };

    public bool Remember
    {
        get => _remember;
        set => Set(ref _remember, value);
    }
}

/// <summary>
/// Shows approval questions one at a time in the assistant window and waits for the answer.
/// There is no timeout: an unanswered question simply keeps the action waiting.
/// </summary>
public sealed class ApprovalService : IApprovalService
{
    private readonly SemaphoreSlim _queue = new SemaphoreSlim(1, 1);

    /// <summary>Raised with the prompt to show, or null when no question is pending. May fire on any thread.</summary>
    public event Action<ApprovalPrompt?>? PromptChanged;

    public async Task<ApprovalResult> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        await _queue.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var prompt = new ApprovalPrompt(request);
            PromptChanged?.Invoke(prompt);
            using (ct.Register(() => prompt.Completion.TrySetCanceled(ct)))
            {
                return await prompt.Completion.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            PromptChanged?.Invoke(null);
            _queue.Release();
        }
    }

    public void Resolve(ApprovalPrompt prompt, bool approved)
        => prompt.Completion.TrySetResult(new ApprovalResult(approved, approved && prompt.Remember && prompt.CanRemember));
}
