using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Input;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Presentation.Mvvm;
using Microsoft.Win32;

namespace CartucheiraVideo.Presentation.ViewModels;

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
/// </summary>
public sealed class PlaylistSettingsViewModel : ViewModelBase
{
    private string _rootFolder;
    private int _dashboardPort;

    public PlaylistSettingsViewModel(AppSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _rootFolder = snapshot.Playlist.RootFolder ?? string.Empty;
        _dashboardPort = snapshot.Playlist.DashboardPort;

        BrowseRootFolderCommand = new RelayCommand(_ => BrowseFolder("Escolher pasta raiz da Playlist", path => RootFolder = path));
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

    public void ApplyTo(AppSettingsSnapshot snapshot)
    {
        snapshot.Playlist.RootFolder = string.IsNullOrWhiteSpace(RootFolder) ? null : RootFolder.Trim();
        snapshot.Playlist.DashboardPort = DashboardPort;
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
