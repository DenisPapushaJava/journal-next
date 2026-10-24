using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Infrastructure.Access;

namespace Jornal.Desktop.ViewModels;

public partial class AccessHostHelpViewModel : ObservableObject
{
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasOpenError))] private string? _openError;
    public bool HasOpenError => OpenError is not null;

    public void ShowFor(Exception? exception)
    {
        OpenError = null;
        IsVisible = AccessHostBlockedException.Contains(exception);
    }
}
