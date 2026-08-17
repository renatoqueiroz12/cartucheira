using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CartucheiraVideo.Presentation.Mvvm;

/// <summary>
/// Classe base para os ViewModels da aplicacao. Implementa notificacao de mudanca de
/// propriedade (INotifyPropertyChanged), usada pelos bindings do WPF.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
