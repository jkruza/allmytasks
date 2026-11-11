using AllMyTasks.Core.Views;

namespace AllMyTasks.ViewModels;

/// <summary>A view model for one of the five main views. The shell calls <see cref="LoadAsync"/> whenever its inputs change.</summary>
public abstract class ContentViewModel : ViewModelBase
{
    /// <summary>The overlapping events of what the view shows. Empty when highlighting is off or the view has no events.</summary>
    public ConflictMap Conflicts { get; protected set; } = ConflictMap.Empty;

    public abstract Task LoadAsync(ViewContext context, CancellationToken cancellationToken);
}
