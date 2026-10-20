using AllMyTasks.Core.Model;
using AllMyTasks.Core.Storage;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AllMyTasks.ViewModels;

/// <summary>A checkbox in the filter menu that shows or hides one collection everywhere.</summary>
public sealed partial class CollectionToggle(CollectionInfo collection, ISourceRepository repository, Func<Task> changed) : ObservableObject
{
    public string Name { get; } = collection.DisplayName;

    public IBrush Accent { get; } = Brush.Parse(Core.Views.ColorUtil.OrDefault(collection.Color));

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = collection.IsVisible;

    async partial void OnIsVisibleChanged(bool value)
    {
        await repository.SetCollectionPreferencesAsync(collection.SourceId, collection.Id, value, null);
        await changed();
    }
}
