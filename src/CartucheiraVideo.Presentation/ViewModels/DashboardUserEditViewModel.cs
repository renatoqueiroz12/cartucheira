using System.Collections.ObjectModel;
using System.Linq;
using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>Uma opção de Programa no checklist de permissão do <see cref="Views.DashboardUserWindow"/> — Sprint 5 (2026-11-16).</summary>
public sealed class ProgramPermissionOptionViewModel : ViewModelBase
{
    private bool _isSelected;

    public ProgramPermissionOptionViewModel(string id, string name, bool isSelected)
    {
        Id = id;
        Name = name;
        _isSelected = isSelected;
    }

    public string Id { get; }

    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// ViewModel do diálogo de criar/editar usuário da dashboard de Playlist
/// (<see cref="Views.DashboardUserWindow"/>) — Sprint 5 (2026-11-16, pedido do usuário "criar um
/// usuário admin... aí vamos criar os usuários, dando permissão"). <see cref="Username"/> só é
/// editável ao CRIAR (<see cref="IsNewUser"/>) — editando um usuário já existente, o nome fica
/// travado (renomear usuário não é suportado nesta sprint, decisão de simplicidade; redefinir senha
/// e trocar permissões continuam disponíveis editando, ver <see cref="Views.DashboardUserWindow"/>).
/// <see cref="ProgramOptions"/> só tem efeito prático quando <see cref="IsAdmin"/> é falso — um
/// Admin sempre vê/edita todos os Programas, independente do que estiver marcado aqui (mesma regra
/// de <c>UserStore.CreateUser</c>/<c>UpdateUserPermissions</c>, que ignora a lista quando
/// <c>isAdmin</c> é true).
/// </summary>
public sealed class DashboardUserEditViewModel : ViewModelBase
{
    private string _username;
    private bool _isAdmin;
    private string _errorMessage = string.Empty;

    public DashboardUserEditViewModel(
        bool isNewUser,
        string username,
        bool isAdmin,
        IReadOnlyList<string> allowedProgramIds,
        IReadOnlyList<(string Id, string Name)> allPrograms)
    {
        IsNewUser = isNewUser;
        _username = username;
        _isAdmin = isAdmin;

        foreach (var (id, name) in allPrograms)
        {
            ProgramOptions.Add(new ProgramPermissionOptionViewModel(id, name, allowedProgramIds.Contains(id, StringComparer.OrdinalIgnoreCase)));
        }
    }

    public bool IsNewUser { get; }

    public string Title => IsNewUser ? "Novo usuário da dashboard" : $"Editar usuário — {Username}";

    public string Username
    {
        get => _username;
        set
        {
            if (SetProperty(ref _username, value))
            {
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    public bool IsAdmin
    {
        get => _isAdmin;
        set => SetProperty(ref _isAdmin, value);
    }

    public ObservableCollection<ProgramPermissionOptionViewModel> ProgramOptions { get; } = new();

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public IReadOnlyList<string> SelectedProgramIds => ProgramOptions.Where(p => p.IsSelected).Select(p => p.Id).ToList();
}
