namespace AllMyTasks.ViewModels;

/// <summary>A view model for one of the five main views. The shell calls <see cref="LoadAsync"/> whenever its inputs change.</summary>
public abstract class ContentViewModel : ViewModelBase
{
    public abstract Task LoadAsync(ViewContext context, CancellationToken cancellationToken);
}
