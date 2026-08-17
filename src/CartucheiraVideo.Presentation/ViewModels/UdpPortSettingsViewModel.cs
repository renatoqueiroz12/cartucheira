using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// Correção do bug encontrado em teste real (2026-08-13): a porta UDP única (Cartucheira + Deck)
/// estava exposta via <c>RelativeSource AncestorType=Window</c> (mesma técnica usada por
/// <c>DeckSettingsViewModel.OnPlayDestinationOptions</c> dentro de <c>DataTemplate</c>s), mas nas
/// telas de Cartucheira e Deck ela simplesmente não resolvia em tempo de execução — o campo
/// aparecia vazio (mostrando só o placeholder) e a edição não chegava em lugar nenhum, então TODO
/// disparo externo parou de funcionar (a porta real ficava sempre no valor default, nunca no que o
/// operador digitava). Correção: em vez de "achar a Window e pegar a propriedade de lá", uma única
/// instância desta classe é criada em <see cref="SettingsViewModel"/> e passada por referência pra
/// <see cref="CartridgeSettingsViewModel"/> e pros dois <see cref="DeckSettingsViewModel"/> — os
/// três painéis (Cartucheira, Deck 1, Deck 2) apontam pro MESMO objeto, então a XAML de cada um
/// pode fazer um Binding local de dois níveis (<c>SharedUdpPort.Port</c>), sem precisar sair do seu
/// próprio DataContext — muito mais simples e sem depender de travessia da árvore visual.
/// </summary>
public sealed class UdpPortSettingsViewModel : ViewModelBase
{
    private int _port;

    public UdpPortSettingsViewModel(int initialPort)
    {
        _port = Math.Clamp(initialPort, 1, 65535);
    }

    /// <summary>Porta UDP (1–65535, clamp defensivo pra um valor digitado errado não virar um número sem sentido pro <c>UdpTriggerListener</c>).</summary>
    public int Port
    {
        get => _port;
        set
        {
            if (SetProperty(ref _port, Math.Clamp(value, 1, 65535)))
            {
                OnPropertyChanged(nameof(EndpointDisplay));
            }
        }
    }

    /// <summary>Endereço UDP (somente exibição, atualiza ao vivo com <see cref="Port"/>) que o operador configura na mesa — uma porta só, pra Cartucheira e Deck.</summary>
    public string EndpointDisplay => $"127.0.0.1:{Port}";
}
