using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// Sprint NDI-Placa-de-Rede (2026-09-13): ViewModel da nova seção "Rede NDI" na janela de
/// Configurações — quinta entrada na sidebar, ao lado de Deck 1/Deck 2/Cartucheira/Saídas Livewire.
/// Pedido do usuário depois de dedicar uma placa de rede só pro Livewire: "o NDI vai precisar ter o
/// tratamento de qual placa de rede ele vai transmitir, pois agora ele está transmitindo pela placa
/// do Livewire, e não pode".
///
/// Igual ao <see cref="LivewireOutputsSettingsViewModel.NetworkInterfaceIp"/> na FORMA (mesmo
/// seletor de IPv4 local), mas bem diferente no que acontece quando o Salvar é clicado: a NDI SDK
/// usada aqui não tem parâmetro nenhum de placa/IP na criação do transmissor — o único mecanismo
/// real é um arquivo do PRÓPRIO WINDOWS que vale pra máquina inteira
/// (<c>CartucheiraVideo.Ndi.NdiNetworkAdapterConfig</c>, <c>%ProgramData%\NDI\ndi-config.v1.json</c>).
/// Por isso esta tela avisa em texto (ver <c>NdiNetworkSettingsPanel.xaml</c>) que a escolha vale
/// pra QUALQUER programa NDI da máquina, não só a Cartucheira, e que o mais seguro é reiniciar todo
/// programa NDI depois de salvar.
/// </summary>
public sealed class NdiNetworkSettingsViewModel : ViewModelBase
{
    private string? _networkInterfaceIp;

    public NdiNetworkSettingsViewModel(AppSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _networkInterfaceIp = snapshot.NdiNetworkInterfaceIp;

        var interfaces = new List<ComboOption<string?>>
        {
            new() { Value = null, Label = "Deixar o NDI escolher (padrão de fábrica)" }
        };
        interfaces.AddRange(EnumerateLocalIPv4Addresses()
            .Select(ip => new ComboOption<string?> { Value = ip, Label = ip }));
        NetworkInterfaces = interfaces;
    }

    /// <summary>Endereços IPv4 locais (uma opção por placa de rede ativa) + "deixar o NDI escolher" (Value nulo) no topo — mesma enumeração de <see cref="LivewireOutputsSettingsViewModel"/>, duplicada de propósito (isolamento entre seções, nenhuma referência cruzada nova).</summary>
    public IReadOnlyList<ComboOption<string?>> NetworkInterfaces { get; }

    /// <summary>
    /// IP local (IPv4) ao qual o NDI desta máquina fica restrito — nulo deixa o NDI escolher
    /// sozinho. Ver comentário de classe: isto NÃO é lido por nenhum socket da própria Cartucheira,
    /// só grava/remove a chave <c>ndi.adapters.allowed</c> no arquivo de configuração do Windows
    /// quando <see cref="SettingsViewModel.SaveCommand"/> roda.
    /// </summary>
    public string? NetworkInterfaceIp
    {
        get => _networkInterfaceIp;
        set => SetProperty(ref _networkInterfaceIp, value);
    }

    /// <summary>Escreve o valor editado de volta no snapshot — a aplicação de verdade (gravar o arquivo do Windows) acontece em <c>MainViewModel.ReapplyNdiNetworkAdapter</c>, não aqui, mesmo padrão de <see cref="LivewireOutputsSettingsViewModel.ApplyTo"/> separar "editar em memória" de "aplicar de verdade".</summary>
    public void ApplyTo(AppSettingsSnapshot snapshot)
    {
        snapshot.NdiNetworkInterfaceIp = NetworkInterfaceIp;
    }

    /// <summary>Endereços IPv4 das placas de rede ativas da máquina — cópia intencional de <see cref="LivewireOutputsSettingsViewModel.EnumerateLocalIPv4Addresses"/> (isolamento entre seções da tela de Configurações, mesmo raciocínio do resto do projeto entre os 7 projetos fixos).</summary>
    private static IEnumerable<string> EnumerateLocalIPv4Addresses()
    {
        List<string> result;
        try
        {
            result = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                    && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Where(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(addr => addr.Address.ToString())
                .Distinct()
                .OrderBy(ip => ip, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception)
        {
            result = new List<string>();
        }

        return result;
    }
}
