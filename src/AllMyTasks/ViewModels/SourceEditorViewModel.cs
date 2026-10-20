using System.Collections.ObjectModel;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AllMyTasks.ViewModels;

/// <summary>One input of the source form, built from a <see cref="SourceConfigField"/>.</summary>
public sealed partial class FieldViewModel(SourceConfigField field, bool isEdit) : ObservableObject
{
    public string Key { get; } = field.Key;

    public string Label { get; } = field.Label + (field.Required ? " *" : string.Empty);

    public string? Help { get; } = field.Help;

    public bool IsSecret { get; } = field.IsSecretField;

    public bool IsBool { get; } = field.Kind == FieldKind.Bool;

    public bool IsText => !IsBool;

    /// <summary>For switch fields: stored as <c>true</c> or empty.</summary>
    public bool BoolValue
    {
        get => string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : string.Empty;
    }

    public string? Placeholder { get; } = isEdit && field.IsSecretField ? "Leave blank to keep the saved value" : null;

    public char? PasswordChar { get; } = field.IsSecretField ? '●' : null;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(BoolValue));
}

/// <summary>Add or edit a source. The form is built from the provider's schema, so no provider-specific UI is needed.</summary>
public sealed partial class SourceEditorViewModel : ViewModelBase
{
    private readonly SourceRegistry _registry;
    private readonly ISourceRepository _repository;
    private readonly SourceInfo? _editing;

    public SourceEditorViewModel(SourceRegistry registry, SourceInfo? editing, ISourceRepository repository)
    {
        _registry = registry;
        _repository = repository;
        _editing = editing;
        Kinds = [.. registry.Factories];
        SelectedKind = (editing is null ? (Kinds.Count > 0 ? Kinds[0] : null) : registry.FactoryFor(editing.Kind))!;
        Name = editing?.DisplayName ?? string.Empty;
        Title = editing is null ? "Add a source" : "Edit source";
        BuildFields();
    }

    public IReadOnlyList<ISourceFactory> Kinds { get; }

    public bool IsEdit => _editing is not null;

    public string Title { get; }

    public ObservableCollection<FieldViewModel> Fields { get; } = [];

    public string? SavedSourceId { get; private set; }

    [ObservableProperty]
    public partial ISourceFactory SelectedKind { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string? NameError { get; set; }

    [ObservableProperty]
    public partial string? SaveError { get; set; }

    /// <summary>Loads the saved non-secret values when editing. Secrets are never read back into the form.</summary>
    public async Task LoadAsync()
    {
        if (_editing is null || await _repository.GetSourceConfigAsync(_editing.Id) is not { } config)
        {
            return;
        }

        foreach (var field in Fields.Where(f => !f.IsSecret))
        {
            field.Value = config.Get(field.Key) ?? string.Empty;
        }
    }

    partial void OnSelectedKindChanged(ISourceFactory value) => BuildFields();

    /// <summary>Validates and saves. Returns false (with errors set on the fields) if the input is not acceptable.</summary>
    public async Task<bool> SaveAsync()
    {
        SaveError = null;
        var values = Fields.ToDictionary(f => f.Key, f => f.Value);
        var errors = SourceRegistry.Validate(SelectedKind, Name, values, IsEdit);
        NameError = errors.FirstOrDefault(e => e.Key == "name")?.Message;
        foreach (var field in Fields)
        {
            field.Error = errors.FirstOrDefault(e => e.Key == field.Key)?.Message;
        }

        if (errors.Count > 0)
        {
            return false;
        }

        try
        {
            if (_editing is null)
            {
                SavedSourceId = (await _registry.AddAsync(SelectedKind.Kind, Name, values)).Id;
            }
            else
            {
                await _registry.UpdateAsync(_editing.Id, Name, values);
                SavedSourceId = _editing.Id;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
        {
            // Shows the type only: messages from storage layers must not reach the screen with private details.
            SaveError = $"The source could not be saved ({ex.GetType().Name}).";
            return false;
        }
    }

    private void BuildFields()
    {
        Fields.Clear();
        if (SelectedKind is null)
        {
            return;
        }

        foreach (var field in SelectedKind.ConfigSchema.Fields)
        {
            Fields.Add(new FieldViewModel(field, IsEdit));
        }
    }
}
