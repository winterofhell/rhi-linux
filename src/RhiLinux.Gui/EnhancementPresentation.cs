using System.ComponentModel;
using System.Runtime.CompilerServices;
using RhiLinux.Mods;

namespace RhiLinux.Gui;

public sealed record EnhancementCardViewModel(
    EnhancementKind? Kind,
    string Name,
    string Summary,
    string State,
    string Detail,
    string? ActionText,
    bool CanRemove,
    bool IsApplicable,
    bool IsInstalled,
    bool HasUpdate,
    bool IsShaderCard)
{
    public bool HasAction => ActionText is not null;
    public bool HasDetail => Detail.Length > 0;
    public string Glyph => Kind switch
    {
        EnhancementKind.ShaderPacks => "FX",
        EnhancementKind.ReLimiter => "FPS",
        EnhancementKind.DisplayCommander => "DC",
        EnhancementKind.ReFramework => "RE",
        EnhancementKind.OptiPatcher => "OP",
        _ => "!"
    };

    public static EnhancementCardViewModel Failed(string message) =>
        new(null, "Enhancements", "Could not read the enhancement state for this game.", "Unavailable", message,
            null, false, false, false, false, false);

    public static IReadOnlyList<EnhancementCardViewModel> Create(
        EnhancementSnapshot snapshot,
        IReadOnlyList<EnhancementUpdate> updates)
    {
        var cards = new List<EnhancementCardViewModel>();
        var shaderUpdate = updates.FirstOrDefault(update => update.Kind == EnhancementKind.ShaderPacks);
        cards.Add(new(
            EnhancementKind.ShaderPacks,
            "Shader packs",
            "Curated ReShade effect collections, pinned to exact upstream commits and isolated per pack.",
            snapshot.ShaderTreeManaged
                ? shaderUpdate is null ? $"{snapshot.ShaderPacks.Count} installed" : "Update available"
                : snapshot.ForeignShaderTree ? "Existing folder" : "Not installed",
            snapshot.ShaderTreeManaged
                ? shaderUpdate is null
                    ? string.Join(", ", snapshot.ShaderPacks.Take(6).Select(pack => ShaderPackCatalog.Find(pack.Id)?.Name ?? pack.Id)) +
                      (snapshot.ShaderPacks.Count > 6 ? $" and {snapshot.ShaderPacks.Count - 6} more" : string.Empty)
                    : $"{shaderUpdate.AvailableVersion} since installation"
                : snapshot.ForeignShaderTree
                    ? "Your current reshade-shaders folder is preserved and restored when managed packs are removed."
                    : string.Empty,
            snapshot.ShaderTreeManaged ? shaderUpdate is null ? "Choose packs" : "Update packs" : "Choose packs",
            snapshot.ShaderTreeManaged,
            true,
            snapshot.ShaderTreeManaged,
            shaderUpdate is not null,
            true));
        foreach (var component in snapshot.Components)
        {
            var update = updates.FirstOrDefault(item => item.Kind == component.Kind);
            string state;
            string detail;
            string? action;
            if (!component.Applicable)
            {
                state = component.Installed ? "Installed" : "Not available";
                detail = component.NotApplicableReason ?? string.Empty;
                action = null;
            }
            else if (component.Managed)
            {
                state = update is null ? "Installed" : "Update available";
                detail = update is null
                    ? $"Version {component.Version}"
                    : $"{component.Version} → {update.AvailableVersion}";
                action = update is null ? "Reinstall" : "Update";
            }
            else if (component.Installed)
            {
                state = "Installed manually";
                detail = "This file was not placed by RHI Linux, so it is left untouched.";
                action = null;
            }
            else
            {
                state = "Not installed";
                detail = component.Conflict is { } conflict ? $"{conflict}; it will be replaced by this component." : string.Empty;
                action = "Install";
            }
            cards.Add(new(
                component.Kind,
                component.Name,
                component.Summary,
                state,
                detail,
                action,
                component.Managed,
                component.Applicable,
                component.Installed,
                update is not null,
                false));
        }
        return cards.OrderByDescending(card => card.IsShaderCard).ThenByDescending(card => card.IsApplicable).ToArray();
    }
}

public sealed class ShaderPackItemViewModel : INotifyPropertyChanged
{
    private bool isSelected;

    public ShaderPackItemViewModel(ShaderPackDefinition definition, bool installed)
    {
        Definition = definition;
        IsInstalled = installed;
        isSelected = installed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ShaderPackDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public string Description => Definition.Description;
    public string Category => Definition.Category.ToString();
    public string Source => Definition.Repository;
    public bool IsInstalled { get; }
    public string Requirement => Definition.Requires.Count == 0
        ? string.Empty
        : "Requires " + string.Join(", ", Definition.Requires.Select(id => ShaderPackCatalog.Find(id)?.Name ?? id));
    public bool HasRequirement => Definition.Requires.Count > 0;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class ShaderPackPickerViewModel : INotifyPropertyChanged
{
    private readonly IReadOnlyList<ShaderPackItemViewModel> items;
    private string searchText = string.Empty;
    private string category = "All";
    private IReadOnlyList<ShaderPackItemViewModel> visibleItems;

    public ShaderPackPickerViewModel(IReadOnlyCollection<string> installed)
    {
        var installedSet = installed.ToHashSet(StringComparer.Ordinal);
        items = ShaderPackCatalog.All
            .Select(definition => new ShaderPackItemViewModel(definition, installedSet.Contains(definition.Id)))
            .ToArray();
        foreach (var item in items) item.PropertyChanged += (_, _) => RaiseSelection();
        visibleItems = items;
        HasInstalledPacks = installedSet.Count > 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<string> Categories { get; } = ["All", "Essential", "Recommended", "Extra", "Selected"];
    public bool HasInstalledPacks { get; }
    public IReadOnlyList<ShaderPackItemViewModel> VisibleItems { get => visibleItems; private set => Set(ref visibleItems, value); }
    public bool HasVisibleItems => VisibleItems.Count > 0;

    public string SearchText
    {
        get => searchText;
        set
        {
            if (Set(ref searchText, value ?? string.Empty)) ApplyFilter();
        }
    }

    public string Category
    {
        get => category;
        set
        {
            if (Set(ref category, value ?? "All")) ApplyFilter();
        }
    }

    public int SelectedCount => items.Count(item => item.IsSelected);
    public string SelectionSummary
    {
        get
        {
            var selected = SelectedIds;
            var resolved = selected.Count == 0 ? 0 : ShaderPackCatalog.ResolveWithDependencies(selected).Count;
            var extra = resolved - selected.Count;
            return selected.Count == 0
                ? HasInstalledPacks ? "No packs selected · applying removes the managed shader folder" : "No packs selected"
                : $"{selected.Count} selected" + (extra > 0 ? $" · {extra} required " + (extra == 1 ? "dependency" : "dependencies") : string.Empty);
        }
    }

    public bool CanApply => SelectedCount > 0 || HasInstalledPacks;
    public string ApplyText => SelectedCount == 0 ? "Remove packs" : "Review changes";

    public IReadOnlyList<string> SelectedIds => items.Where(item => item.IsSelected).Select(item => item.Id).ToArray();

    public void ApplyRecommended()
    {
        var recommended = ShaderPackCatalog.RecommendedSelection.ToHashSet(StringComparer.Ordinal);
        foreach (var item in items) item.IsSelected = recommended.Contains(item.Id) || item.IsInstalled;
    }

    public void Clear()
    {
        foreach (var item in items) item.IsSelected = false;
    }

    private void ApplyFilter()
    {
        var query = searchText.Trim();
        VisibleItems = items.Where(item =>
                (category == "All" ||
                 category == "Selected" && item.IsSelected ||
                 item.Category.Equals(category, StringComparison.Ordinal)) &&
                (query.Length == 0 ||
                 item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 item.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 item.Source.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        OnPropertyChanged(nameof(HasVisibleItems));
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(ApplyText));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record EnhancementStepViewModel(int Number, string Description, string Kind);

public sealed class EnhancementPlanDialogViewModel : INotifyPropertyChanged
{
    private EnhancementPlan? plan;
    private bool isPreparing = true;
    private bool isRunning;
    private bool hasResult;
    private bool succeeded;
    private string progressText = "Resolving upstream releases…";
    private string resultTitle = string.Empty;
    private string resultMessage = string.Empty;
    private IReadOnlyList<string> warnings = [];
    private IReadOnlyList<string> blockers = [];
    private IReadOnlyList<EnhancementStepViewModel> steps = [];
    private IReadOnlyList<string> downloads = [];

    public EnhancementPlanDialogViewModel(string title) => Title = title;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Title { get; private set; }
    public EnhancementPlan? Plan => plan;
    public bool IsPreparing { get => isPreparing; private set { if (Set(ref isPreparing, value)) RaiseAvailability(); } }
    public bool IsRunning { get => isRunning; private set { if (Set(ref isRunning, value)) RaiseAvailability(); } }
    public bool IsWorking => IsPreparing || IsRunning;
    public bool HasResult { get => hasResult; private set { if (Set(ref hasResult, value)) RaiseAvailability(); } }
    public bool Succeeded { get => succeeded; private set => Set(ref succeeded, value); }
    public string ProgressText { get => progressText; set => Set(ref progressText, value); }
    public string ResultTitle { get => resultTitle; private set => Set(ref resultTitle, value); }
    public string ResultMessage { get => resultMessage; private set => Set(ref resultMessage, value); }
    public IReadOnlyList<string> Warnings { get => warnings; private set { if (Set(ref warnings, value)) OnPropertyChanged(nameof(HasWarnings)); } }
    public IReadOnlyList<string> Blockers { get => blockers; private set { if (Set(ref blockers, value)) OnPropertyChanged(nameof(HasBlockers)); } }
    public IReadOnlyList<EnhancementStepViewModel> Steps { get => steps; private set { if (Set(ref steps, value)) OnPropertyChanged(nameof(StepCount)); } }
    public IReadOnlyList<string> Downloads { get => downloads; private set { if (Set(ref downloads, value)) OnPropertyChanged(nameof(HasDownloads)); } }
    public bool HasWarnings => Warnings.Count > 0;
    public bool HasBlockers => Blockers.Count > 0;
    public bool HasDownloads => Downloads.Count > 0;
    public bool HasSteps => Steps.Count > 0;
    public string StepCount => Steps.Count == 1 ? "1 step" : $"{Steps.Count} steps";
    public bool CanExecute => plan is { CanExecute: true } && !IsWorking && !HasResult;
    public bool CanClose => !IsRunning;
    public string CloseText => HasResult || plan is { CanExecute: false } ? "Close" : "Cancel";
    public string ActionText => plan?.Title.StartsWith("Remove", StringComparison.Ordinal) == true ? "Remove" : "Apply";
    public string Footnote => HasBlockers
        ? "Nothing will change until the blocking issues are resolved."
        : "Every change is journaled and rolled back automatically if a step fails.";

    public void Load(EnhancementPlan prepared)
    {
        plan = prepared;
        Title = prepared.Title;
        Warnings = prepared.Warnings.ToArray();
        Blockers = prepared.Blockers.ToArray();
        Steps = prepared.Steps.Select((step, index) => new EnhancementStepViewModel(index + 1, step.Description, StepLabel(step.Type))).ToArray();
        Downloads = prepared.Downloads
            .Select(download => $"{download.Name} · {download.Version}" +
                                (download.Size is { } size ? $" · {FormatSize(size)}" : string.Empty) +
                                (download.Cached ? " · cached" : string.Empty))
            .ToArray();
        IsPreparing = false;
        if (!prepared.CanExecute && Blockers.Count == 0 && Steps.Count == 0)
        {
            ResultTitle = "Nothing to change";
            ResultMessage = "The game already matches the requested state.";
            HasResult = true;
            Succeeded = true;
        }
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasSteps));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(Footnote));
        RaiseAvailability();
    }

    public void FailPreparation(string message)
    {
        IsPreparing = false;
        ResultTitle = "Could not prepare the plan";
        ResultMessage = message;
        Succeeded = false;
        HasResult = true;
    }

    public void Begin()
    {
        IsRunning = true;
        ProgressText = "Applying changes…";
    }

    public void Complete(EnhancementExecutionResult result)
    {
        IsRunning = false;
        Succeeded = result.Succeeded;
        ResultTitle = result.Succeeded ? "Changes applied" : "Operation failed";
        ResultMessage = result.Succeeded
            ? "Files and settings were written and recorded. They can be removed cleanly at any time."
            : (result.Error ?? "The operation did not complete.") + " Completed steps were rolled back.";
        HasResult = true;
    }

    private static string StepLabel(EnhancementStepType type) => type switch
    {
        EnhancementStepType.PlaceFile => "ADD",
        EnhancementStepType.RemoveFile => "DEL",
        EnhancementStepType.PreserveOriginalShaders => "KEEP",
        EnhancementStepType.InstallShaderTree => "ADD",
        EnhancementStepType.RemoveShaderTree => "DEL",
        EnhancementStepType.RestoreOriginalShaders => "BACK",
        EnhancementStepType.SetIniValue => "INI",
        EnhancementStepType.RestoreIniValue => "INI",
        _ => "STEP"
    };

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:0.#} MiB"
        : $"{Math.Max(1, bytes / 1024)} KiB";

    private void RaiseAvailability()
    {
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(CanClose));
        OnPropertyChanged(nameof(CloseText));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
