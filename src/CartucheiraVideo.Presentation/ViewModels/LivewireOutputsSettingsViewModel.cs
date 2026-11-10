using System.Collections.ObjectModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Input;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// Sprint Livewire-2 (2026-09-13): ViewModel da nova seção "Saídas Livewire" na janela de
/// Configurações — quarta entrada na sidebar, ao lado de Deck 1/Deck 2/Cartucheira. Aqui o
/// operador cria/renomeia/remove as "Saídas Livewire" (nome + número de canal) que passam a
/// aparecer, prefixadas (<c>"livewire:{Id}"</c>), no MESMO seletor de dispositivo de saída de
/// áudio já usado por Vinhetas/Trilhas (<see cref="CartridgeSettingsViewModel.AudioDevices"/>) —
/// pedido literal do usuário: "crio o output e aí posso selecionar esses criados nos decks e nas
/// cartucheira" (Deck fica de fora desta sprint, ver <c>arquitetura/livewire-plano.md</c> no
/// projeto).
///
/// <see cref="Outputs"/> é a MESMA instância (por referência) passada pro construtor de
/// <see cref="CartridgeSettingsViewModel"/> (ver <see cref="SettingsViewModel"/>) — assim, criar ou
/// remover uma Saída aqui já atualiza a lista de dispositivos da aba Cartucheira na hora, sem
/// precisar fechar/reabrir a janela de Configurações. Limitação honesta, documentada também no
/// plano do projeto: RENOMEAR uma Saída já existente (mudar só o texto de <see cref="LivewireOutputItemViewModel.Name"/>)
/// não dispara esse recálculo — o rótulo já mostrado na aba Cartucheira só atualiza depois de
/// fechar e abrir a janela de Configurações de novo — porque isso exigiria observar
/// PropertyChanged de cada item individualmente, complexidade que não pareceu valer a pena pra
/// esta sprint (renomear é raro comparado a criar/remover).
///
/// Mesmo padrão de "editar em memória, só grava quando o Salvar global do SettingsWindow for
/// clicado" já usado pelo resto da tela — ver <see cref="ApplyTo"/>.
/// </summary>
public sealed class LivewireOutputsSettingsViewModel : ViewModelBase
{
    private LivewireOutputItemViewModel? _selectedOutput;
    private string? _networkInterfaceIp;

    public LivewireOutputsSettingsViewModel(AppSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        foreach (var output in snapshot.LivewireOutputs)
        {
            Outputs.Add(new LivewireOutputItemViewModel(output));
        }

        _networkInterfaceIp = snapshot.LivewireNetworkInterfaceIp;

        var interfaces = new List<ComboOption<string?>>
        {
            new() { Value = null, Label = "Deixar o sistema escolher (não recomendado com mais de uma placa de rede)" }
        };
        interfaces.AddRange(EnumerateLocalIPv4Addresses()
            .Select(ip => new ComboOption<string?> { Value = ip, Label = ip }));
        NetworkInterfaces = interfaces;

        AddCommand = new RelayCommand(_ => Add());
        RemoveCommand = new RelayCommand(_ => Remove(), _ => SelectedOutput is not null);
    }

    /// <summary>
    /// Saídas Livewire configuradas — a MESMA instância (por referência) é passada também pro
    /// <see cref="CartridgeSettingsViewModel"/> construído ao lado (ver <see cref="SettingsViewModel"/>),
    /// pra Add/Remove aqui já refletirem na lista de dispositivos de Vinhetas/Trilhas. Ver
    /// comentário de classe pra limitação honesta sobre renomear.
    /// </summary>
    public ObservableCollection<LivewireOutputItemViewModel> Outputs { get; } = new();

    /// <summary>Linha selecionada na lista — controla se <see cref="RemoveCommand"/> pode rodar.</summary>
    public LivewireOutputItemViewModel? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (SetProperty(ref _selectedOutput, value))
            {
                ((RelayCommand)RemoveCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Endereços IPv4 locais (uma opção por placa de rede ativa) + "deixar o sistema escolher" (Value nulo) no topo.</summary>
    public IReadOnlyList<ComboOption<string?>> NetworkInterfaces { get; }

    /// <summary>
    /// IP local (IPv4) da placa de rede usada pra transmitir Livewire — nulo deixa o Windows
    /// escolher, o que NÃO é recomendado numa máquina com mais de uma placa (lição confirmada por
    /// teste real na Mesa Console, referência C++/JUCE do usuário — ver
    /// <c>Infrastructure.Livewire.LivewireSender</c>): sem essa escolha explícita, o Windows pode
    /// escolher uma interface que não chega na rede Livewire, sem erro nenhum — o pacote "sai" e
    /// ninguém recebe.
    /// </summary>
    public string? NetworkInterfaceIp
    {
        get => _networkInterfaceIp;
        set => SetProperty(ref _networkInterfaceIp, value);
    }

    /// <summary>Cria uma nova Saída Livewire com um nome e canal sugeridos (editáveis na hora) — nunca reaproveita um canal já usado por outra Saída desta lista.</summary>
    public ICommand AddCommand { get; }

    /// <summary>Remove <see cref="SelectedOutput"/> da lista — desabilitado (ver <see cref="RemoveCommand"/> acima) se nada estiver selecionado.</summary>
    public ICommand RemoveCommand { get; }

    private void Add()
    {
        var item = new LivewireOutputItemViewModel(new LivewireOutputSettings
        {
            Name = $"Saída Livewire {Outputs.Count + 1}",
            Channel = NextFreeChannel(),
        });

        Outputs.Add(item);
        SelectedOutput = item;
    }

    private void Remove()
    {
        var target = SelectedOutput;
        if (target is null)
        {
            return;
        }

        Outputs.Remove(target);
        SelectedOutput = null;
    }

    /// <summary>Primeiro canal (1..32767) ainda não usado por nenhuma Saída já criada — só uma sugestão inicial, o operador pode trocar livremente depois (dois canais iguais em Saídas diferentes não é impedido aqui, mas transmitiriam pro MESMO grupo multicast se as duas fossem selecionadas ao mesmo tempo — limitação conhecida, documentada na sprint).</summary>
    private int NextFreeChannel()
    {
        var used = new HashSet<int>(Outputs.Select(o => o.Channel));

        for (var channel = 1; channel <= 32767; channel++)
        {
            if (!used.Contains(channel))
            {
                return channel;
            }
        }

        return 1;
    }

    /// <summary>Escreve os valores editados de volta no snapshot real — chamado só quando o Salvar global do SettingsWindow é clicado (mesmo padrão de <see cref="CartridgeSettingsViewModel.ApplyTo"/>).</summary>
    public void ApplyTo(AppSettingsSnapshot snapshot)
    {
        snapshot.LivewireOutputs = Outputs
            .Select(o => new LivewireOutputSettings
            {
                Id = o.Id,
                Name = string.IsNullOrWhiteSpace(o.Name) ? "Saída Livewire" : o.Name.Trim(),
                Channel = o.Channel,
            })
            .ToList();

        snapshot.LivewireNetworkInterfaceIp = NetworkInterfaceIp;
    }

    /// <summary>
    /// Endereços IPv4 das placas de rede ativas (<see cref="OperationalStatus.Up"/>) da máquina —
    /// mesma ideia de <c>AudioEngine.EnumerateOutputDevices</c>, só que pra rede em vez de áudio.
    /// Loopback (127.0.0.1) excluído de propósito: nunca é uma escolha válida pra sair numa rede
    /// Livewire de verdade.
    /// </summary>
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
            // Falha honesta ao enumerar placas de rede (raríssimo, mas não deveria travar a tela de
            // Configurações) — fica só com a opção "deixar o sistema escolher".
            result = new List<string>();
        }

        return result;
    }
}
