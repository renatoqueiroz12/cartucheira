using System.Collections.ObjectModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Infrastructure.Playlist;
using CartucheiraVideo.Presentation.Mvvm;
using CartucheiraVideo.Presentation.Views;
using Microsoft.Win32;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>Uma linha da lista de usuários da dashboard, em Configurações → Playlist → Usuários da Dashboard — Sprint 5 (2026-11-16).</summary>
public sealed class DashboardUserRowViewModel
{
    public DashboardUserRowViewModel(DashboardUserRecord record)
    {
        Id = record.Id;
        Username = record.Username;
        IsAdmin = record.IsAdmin;
        AllowedProgramIds = record.AllowedProgramIds;
    }

    public string Id { get; }

    public string Username { get; }

    public bool IsAdmin { get; }

    public IReadOnlyList<string> AllowedProgramIds { get; }

    public string RoleDisplay => IsAdmin
        ? "Administrador"
        : AllowedProgramIds.Count switch
        {
            0 => "Produtor (nenhum Programa liberado ainda)",
            1 => "Produtor (1 Programa liberado)",
            _ => $"Produtor ({AllowedProgramIds.Count} Programas liberados)"
        };
}

/// <summary>
/// ViewModel da seção "Playlist" da janela de Configurações — Sprint Playlist-Dashboard
/// (2026-11-14, ver arquitetura/playlist-modulo-roteiro.md). Só dois campos editáveis (pasta raiz e
/// porta), mesmo padrão simples de <see cref="NdiNetworkSettingsViewModel"/>/
/// <see cref="BackgroundArtSettingsViewModel"/> — nenhuma lista compartilhada com outra seção.
///
/// <see cref="DashboardUrlDisplay"/> é só pra conveniência do operador (copiar e passar pro
/// produtor) — mostra o primeiro IPv4 local não-loopback encontrado; numa máquina com mais de uma
/// placa de rede, pode não ser a placa certa pra alcançar os produtores (mesma limitação já aceita
/// pelo seletor de "Rede NDI"/"Saídas Livewire" — aqui é só um texto informativo, não grava nada).
///
/// **Usuários da Dashboard — Sprint 5 (2026-11-16, pedido do usuário "precisamos de uma barra de
/// login... criar usuários... dando permissão")**: diferente de <see cref="RootFolder"/>/
/// <see cref="DashboardPort"/> (ficam só em memória até o "Salvar" geral do SettingsWindow, ver
/// <see cref="ApplyTo"/>), <see cref="DashboardUsers"/> grava DIRETO no disco a cada ação
/// (criar/editar/remover) — mesmo modelo já usado pelas Laudas/Programas/Edições da própria
/// dashboard (<c>Infrastructure.Playlist.UserStore</c>/<c>PlaylistFileStore</c>, ambos "nunca
/// lança", escrita imediata) — não existe "cancelar" depois de criar um usuário aqui, só remover.
/// Opera sobre <see cref="_persistedRootFolder"/> (a pasta raiz JÁ SALVA, lida uma vez na
/// construção) deliberadamente, não sobre o valor que está sendo digitado em
/// <see cref="RootFolder"/> — gerenciar usuário de uma pasta que ainda nem foi salva não faz
/// sentido.
/// </summary>
public sealed class PlaylistSettingsViewModel : ViewModelBase
{
    private readonly string? _persistedRootFolder;
    private string _rootFolder;
    private int _dashboardPort;
    private DashboardUserRowViewModel? _selectedDashboardUser;
    private string _dashboardUsersError = string.Empty;

    public PlaylistSettingsViewModel(AppSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _persistedRootFolder = snapshot.Playlist.RootFolder;
        _rootFolder = snapshot.Playlist.RootFolder ?? string.Empty;
        _dashboardPort = snapshot.Playlist.DashboardPort;

        BrowseRootFolderCommand = new RelayCommand(_ => BrowseFolder("Escolher pasta raiz da Playlist", path => RootFolder = path));

        AddDashboardUserCommand = new RelayCommand(_ => AddDashboardUser(), _ => HasPersistedRootFolder);
        EditDashboardUserCommand = new RelayCommand(_ => EditDashboardUser(), _ => SelectedDashboardUser is not null);
        RemoveDashboardUserCommand = new RelayCommand(_ => RemoveDashboardUser(), _ => SelectedDashboardUser is not null);

        RefreshDashboardUsers();
    }

    /// <summary>Pasta raiz onde cada Programa/Data ganha sua própria subpasta — vazia desliga o módulo (ver <see cref="PlaylistSettingsSnapshot.RootFolder"/>).</summary>
    public string RootFolder
    {
        get => _rootFolder;
        set
        {
            if (SetProperty(ref _rootFolder, value))
            {
                OnPropertyChanged(nameof(DashboardUrlDisplay));
            }
        }
    }

    /// <summary>Porta TCP onde a dashboard escuta — precisa ser alcançável por outros computadores da rede local.</summary>
    public int DashboardPort
    {
        get => _dashboardPort;
        set
        {
            if (SetProperty(ref _dashboardPort, Math.Clamp(value, 1, 65535)))
            {
                OnPropertyChanged(nameof(DashboardUrlDisplay));
            }
        }
    }

    /// <summary>URL pra passar aos produtores — só exibição, calculada a partir do primeiro IPv4 local não-loopback; vazia (campo escondido na UI) enquanto <see cref="RootFolder"/> não estiver preenchida.</summary>
    public string DashboardUrlDisplay => string.IsNullOrWhiteSpace(RootFolder)
        ? string.Empty
        : $"http://{FindFirstLocalIPv4() ?? "<ip-desta-maquina>"}:{DashboardPort}/";

    public ICommand BrowseRootFolderCommand { get; }

    /// <summary>Falso quando a pasta raiz ainda não foi salva nenhuma vez — "Usuários da Dashboard" fica desabilitado até isso acontecer (ver comentário de classe).</summary>
    public bool HasPersistedRootFolder => !string.IsNullOrWhiteSpace(_persistedRootFolder);

    public ObservableCollection<DashboardUserRowViewModel> DashboardUsers { get; } = new();

    /// <summary>Verdadeiro quando a pasta raiz já foi salva mas ainda não existe nenhum Admin — lembrete pro operador criar o primeiro usuário antes de passar a URL pro produtor (decisão confirmada: o primeiro Admin é criado por aqui, não por uma tela de "primeiro acesso" na própria dashboard).</summary>
    public bool ShowNoAdminYetHint => HasPersistedRootFolder && !DashboardUsers.Any(u => u.IsAdmin);

    public DashboardUserRowViewModel? SelectedDashboardUser
    {
        get => _selectedDashboardUser;
        set
        {
            if (SetProperty(ref _selectedDashboardUser, value))
            {
                ((RelayCommand)EditDashboardUserCommand).RaiseCanExecuteChanged();
                ((RelayCommand)RemoveDashboardUserCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Erro da última ação de usuário (criar/editar/remover) — mensagens honestas da própria UserStore (ex.: "já existe um usuário chamado...", "este é o único Administrador...").</summary>
    public string DashboardUsersError
    {
        get => _dashboardUsersError;
        private set => SetProperty(ref _dashboardUsersError, value);
    }

    public ICommand AddDashboardUserCommand { get; }

    public ICommand EditDashboardUserCommand { get; }

    public ICommand RemoveDashboardUserCommand { get; }

    public void ApplyTo(AppSettingsSnapshot snapshot)
    {
        snapshot.Playlist.RootFolder = string.IsNullOrWhiteSpace(RootFolder) ? null : RootFolder.Trim();
        snapshot.Playlist.DashboardPort = DashboardPort;
    }

    private void RefreshDashboardUsers()
    {
        DashboardUsers.Clear();
        try
        {
            if (!HasPersistedRootFolder)
            {
                return;
            }

            var (ok, error, users) = UserStore.ListUsers(_persistedRootFolder!);
            if (!ok)
            {
                DashboardUsersError = error ?? "erro desconhecido ao listar usuários.";
                return;
            }

            foreach (var user in users)
            {
                DashboardUsers.Add(new DashboardUserRowViewModel(user));
            }
        }
        finally
        {
            // DashboardUsers (ObservableCollection) só notifica mudança de ITENS — ShowNoAdminYetHint
            // é uma propriedade derivada dela, precisa de aviso manual pra UI reavaliar o Binding.
            OnPropertyChanged(nameof(ShowNoAdminYetHint));
        }
    }

    /// <summary>Lê os Programas já cadastrados na pasta raiz persistida — pro checklist de permissão do diálogo de criar/editar usuário (ver <see cref="Views.DashboardUserWindow"/>).</summary>
    private List<(string Id, string Name)> ListAvailablePrograms()
    {
        var (ok, _, programs) = PlaylistFileStore.ListPrograms(_persistedRootFolder ?? string.Empty);
        return ok ? programs.Select(p => (p.Id, p.Name)).ToList() : new List<(string Id, string Name)>();
    }

    private void AddDashboardUser()
    {
        DashboardUsersError = string.Empty;
        var editViewModel = new DashboardUserEditViewModel(isNewUser: true, username: string.Empty, isAdmin: false, allowedProgramIds: Array.Empty<string>(), allPrograms: ListAvailablePrograms());
        var window = new DashboardUserWindow(editViewModel) { Owner = Application.Current?.MainWindow };
        if (window.ShowDialog() != true)
        {
            return;
        }

        var (ok, error, _) = UserStore.CreateUser(_persistedRootFolder!, editViewModel.Username, window.EnteredPassword, editViewModel.IsAdmin, editViewModel.SelectedProgramIds);
        if (!ok)
        {
            DashboardUsersError = error ?? "erro desconhecido ao criar usuário.";
            return;
        }

        RefreshDashboardUsers();
    }

    private void EditDashboardUser()
    {
        var target = SelectedDashboardUser;
        if (target is null)
        {
            return;
        }

        DashboardUsersError = string.Empty;
        var editViewModel = new DashboardUserEditViewModel(isNewUser: false, username: target.Username, isAdmin: target.IsAdmin, allowedProgramIds: target.AllowedProgramIds, allPrograms: ListAvailablePrograms());
        var window = new DashboardUserWindow(editViewModel) { Owner = Application.Current?.MainWindow };
        if (window.ShowDialog() != true)
        {
            return;
        }

        var (permOk, permError, _) = UserStore.UpdateUserPermissions(_persistedRootFolder!, target.Id, editViewModel.IsAdmin, editViewModel.SelectedProgramIds);
        if (!permOk)
        {
            DashboardUsersError = permError ?? "erro desconhecido ao atualizar permissões.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(window.EnteredPassword))
        {
            var passwordError = UserStore.ResetPassword(_persistedRootFolder!, target.Id, window.EnteredPassword);
            if (passwordError is not null)
            {
                DashboardUsersError = passwordError;
                return;
            }
        }

        RefreshDashboardUsers();
    }

    private void RemoveDashboardUser()
    {
        var target = SelectedDashboardUser;
        if (target is null)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"Remover o usuário \"{target.Username}\"? Ele não vai mais conseguir entrar na dashboard.",
            "Cartucheira Vídeo",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        DashboardUsersError = string.Empty;
        var error = UserStore.DeleteUser(_persistedRootFolder!, target.Id);
        if (error is not null)
        {
            DashboardUsersError = error;
            return;
        }

        RefreshDashboardUsers();
    }

    /// <summary>OpenFolderDialog (Microsoft.Win32, novo no .NET 8) — mesmo padrão já usado por <c>CartridgeSettingsViewModel.BrowseFolder</c>.</summary>
    private static void BrowseFolder(string title, Action<string> onSelected)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (dialog.ShowDialog() == true)
        {
            onSelected(dialog.FolderName);
        }
    }

    private static string? FindFirstLocalIPv4()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                    && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Where(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(addr => addr.Address.ToString())
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
