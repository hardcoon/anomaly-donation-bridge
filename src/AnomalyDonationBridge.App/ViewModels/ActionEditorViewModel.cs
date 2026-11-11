using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App.ViewModels;

public sealed class ActionEditorViewModel(ActionViewModel source, Func<ActionViewModel, Task> save,
    Func<Task<string>>? resetActorBoost = null, Action<TemplateSourceLink>? openSource = null,
    IEnumerable<ActionDefinition>? activeTemplates = null) : ObservableObject
{
    private bool _isSaving;
    private bool _isResetting;
    private string _error = string.Empty;
    private string _resetStatus = string.Empty;
    public ActionViewModel Draft { get; } = source.CreateEditorDraft(activeTemplates);
    public IReadOnlyList<TemplateSourceLink> SourceLinks => TemplateSourceLinks.For(Draft.HandlerId);
    public bool ShowSourceLinks => SourceLinks.Count > 0;
    public void OpenSource(TemplateSourceLink link)
    {
        if (!SourceLinks.Contains(link) || IsBusy) return;
        Error = string.Empty;
        try { openSource?.Invoke(link); }
        catch (Exception exception) { Error = exception.Message; }
    }
    public bool IsSaving { get => _isSaving; private set { if (SetProperty(ref _isSaving, value)) NotifyBusyChanged(); } }
    public bool IsResetting { get => _isResetting; private set { if (SetProperty(ref _isResetting, value)) NotifyBusyChanged(); } }
    public bool IsBusy => IsSaving || IsResetting;
    public bool ShowActorBoostReset => Draft.HandlerId == "actor_boost";
    public string SaveHint => ShowActorBoostReset
        ? "Настройки применятся после сохранения. Сброс выполняется сразу."
        : "Изменения применятся только после сохранения.";
    public bool CanResetActorBoost => ShowActorBoostReset && resetActorBoost is not null && !IsBusy;
    public string ResetStatus { get => _resetStatus; private set => SetProperty(ref _resetStatus, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    public async Task<bool> SaveAsync()
    {
        if (IsBusy) return false;
        Error = string.Empty;
        if (!Draft.TryCommitPendingEdits(out var error)) { Error = error; return false; }
        IsSaving = true;
        try { await save(Draft); return true; }
        catch (Exception exception) { Error = exception.Message; return false; }
        finally { IsSaving = false; }
    }

    public async Task<bool> ResetActorBoostAsync()
    {
        if (!CanResetActorBoost) return false;
        Error = ResetStatus = string.Empty;
        IsResetting = true;
        try { ResetStatus = await resetActorBoost!(); return true; }
        catch (Exception exception) { Error = exception.Message; return false; }
        finally { IsResetting = false; }
    }

    private void NotifyBusyChanged()
    { RaisePropertyChanged(nameof(IsBusy)); RaisePropertyChanged(nameof(CanResetActorBoost)); }
}
