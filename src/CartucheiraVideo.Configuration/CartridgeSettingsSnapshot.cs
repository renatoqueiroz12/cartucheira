using System.Collections.Generic;

namespace CartucheiraVideo.Configuration;

/// <summary>
/// Subconjunto persistível das configurações da Cartucheira (Vinhetas/Trilhas) — Sprint Cartucheira
/// (2026-08-09, parte 2 do item 2 do roteiro). Cada Cartucheira tem seu próprio dispositivo de
/// saída de áudio (decisão do usuário: "dispositivo separado") e sua própria pasta de origem dos
/// cartuchos, lidos em ordem alfabética (mesmo padrão de numeração "01 - nome", "02 - nome"... que
/// a Playlist real vai usar quando for proposta — roteiro item 5).
///
/// Sprint Comando Externo (2026-08-10): <see cref="VinhetasExternalTriggerEnabled"/>/
/// <see cref="TrilhasExternalTriggerEnabled"/> ligam TRÊS canais juntos por Cartucheira — teclado
/// (atalho global Win32, <see cref="VinhetasKeyboardShortcut"/>/<see cref="TrilhasKeyboardShortcut"/>),
/// Stream Deck/Companion (HTTP local) e mesa de áudio (UDP local,
/// <see cref="VinhetasUdpTriggerOnCommand"/>/<see cref="VinhetasUdpTriggerOffCommand"/>/
/// <see cref="TrilhasUdpTriggerOnCommand"/>/<see cref="TrilhasUdpTriggerOffCommand"/>) — os três
/// controlados de ponta a ponta por este projeto, sem bloqueio externo, por isso já entram com
/// disparo de verdade (arquitetura §14.2), não como placeholder. Um único "habilitado" por
/// Cartucheira liga os TRÊS canais juntos — o pedido original do usuário foi "a opção... para
/// receber comando externo por teclado e por stream deck", no singular; o canal UDP da mesa (pedido
/// depois, com o protocolo real confirmado por captura de tela — texto curto tipo "ON"/"OFF" por
/// UDP, arquitetura §13.4) entrou no mesmo toggle por ser exatamente a mesma ideia — mais um jeito
/// externo de disparar a mesma Cartucheira. Porta UDP consolidada em
/// <see cref="AppSettingsSnapshot.UdpTriggerPort"/> (2026-08-13) — uma porta só, compartilhada por
/// Cartucheira E Deck, não mais um campo aqui.
///
/// **Substituído nesta correção:** o campo único antigo <c>ExternalTriggerCommand</c> (honesto/
/// placeholder desde a Sprint Cartucheira, nunca teve efeito nenhum porque o protocolo da mesa
/// ainda não era conhecido) saiu — não havia nada funcional pra migrar, já que nunca disparava
/// nada. No lugar dele, os quatro campos de UDP abaixo, um par Ligar/Desligar por Cartucheira,
/// espelhando as colunas "Trigger ON"/"Trigger OFF" da própria tela de configuração da mesa.
///
/// Sprint Crossfade v1 (2026-08-11) — REVERTIDA no mesmo dia: <c>VinhetasCrossfadeMs</c>/
/// <c>TrilhasCrossfadeMs</c> (duração da mistura entre cartuchos) chegaram a ser removidos daqui
/// depois de um teste real confirmar instabilidade num primeiro desenho (LibVLCSharp, dois
/// MediaPlayer simultâneos). Reintroduzidos na Sprint Crossfade v2 (2026-08-11, mesmo dia) depois de
/// pesquisa confirmar a causa raiz real e trocar o motor de áudio das Cartucheiras pra NAudio — ver
/// o comentário completo em <c>CartucheiraVideo.Engine.AudioEngine</c>.
/// </summary>
public sealed class CartridgeSettingsSnapshot
{
    /// <summary>Identificador do dispositivo de saída de áudio do Windows (mmdevice) usado pela Cartucheira Vinhetas. Nulo = dispositivo padrão do sistema.</summary>
    public string? VinhetasAudioDeviceId { get; set; }

    /// <summary>Pasta de onde os cartuchos de Vinhetas são lidos. Nulo/vazio = banco fica vazio, sem inventar cartucho nenhum.</summary>
    public string? VinhetasFolderPath { get; set; }

    /// <summary>Identificador do dispositivo de saída de áudio do Windows (mmdevice) usado pela Cartucheira Trilhas. Nulo = dispositivo padrão do sistema.</summary>
    public string? TrilhasAudioDeviceId { get; set; }

    /// <summary>Pasta de onde os cartuchos de Trilhas são lidos.</summary>
    public string? TrilhasFolderPath { get; set; }

    /// <summary>Customizações de cor/loop por cartucho da Cartucheira Vinhetas — Sprint Cartucheira 2, parte 2 (2026-08-10), ver <see cref="CartridgeOverride"/>.</summary>
    public List<CartridgeOverride> VinhetasOverrides { get; set; } = new();

    /// <summary>Customizações de cor/loop por cartucho da Cartucheira Trilhas.</summary>
    public List<CartridgeOverride> TrilhasOverrides { get; set; } = new();

    /// <summary>Verdadeiro = Vinhetas recebe comando externo de verdade por teclado (atalho global), Stream Deck/Companion (HTTP local) e mesa de áudio (UDP local) — Sprint Comando Externo (2026-08-10).</summary>
    public bool VinhetasExternalTriggerEnabled { get; set; }

    /// <summary>Tecla do atalho global de Vinhetas — nome de <c>Infrastructure.GlobalHotkeyKey</c> (ex.: "F1", "Tab"). Nulo = nenhuma escolhida ainda (atalho fica desativado mesmo com o toggle ligado).</summary>
    public string? VinhetasKeyboardShortcut { get; set; }

    /// <summary>Verdadeiro = Trilhas recebe comando externo de verdade por teclado, Stream Deck/Companion e mesa de áudio.</summary>
    public bool TrilhasExternalTriggerEnabled { get; set; }

    /// <summary>Tecla do atalho global de Trilhas — mesma regra de <see cref="VinhetasKeyboardShortcut"/>.</summary>
    public string? TrilhasKeyboardShortcut { get; set; }

    /// <summary>Texto que a mesa manda por UDP (porta em <see cref="AppSettingsSnapshot.UdpTriggerPort"/>) pra DISPARAR Vinhetas (dispara o "1" da fila selecionada, interrompendo se já estiver tocando — arquitetura §14.2/§14.6). Vazio = nenhum comando configurado ainda, canal UDP de Vinhetas fica sem efeito mesmo com o toggle ligado.</summary>
    public string VinhetasUdpTriggerOnCommand { get; set; } = string.Empty;

    /// <summary>Texto que a mesa manda por UDP pra PARAR o que estiver tocando em Vinhetas. Vazio = nenhum comando configurado ainda.</summary>
    public string VinhetasUdpTriggerOffCommand { get; set; } = string.Empty;

    /// <summary>Mesma ideia de <see cref="VinhetasUdpTriggerOnCommand"/>, pra Trilhas.</summary>
    public string TrilhasUdpTriggerOnCommand { get; set; } = string.Empty;

    /// <summary>Mesma ideia de <see cref="VinhetasUdpTriggerOffCommand"/>, pra Trilhas.</summary>
    public string TrilhasUdpTriggerOffCommand { get; set; } = string.Empty;

    /// <summary>Duração (ms) do crossfade entre um cartucho de Vinhetas terminando e o próximo começando — Sprint Crossfade v2 (2026-08-11). Zero (padrão) = corte seco de sempre.</summary>
    public int VinhetasCrossfadeMs { get; set; }

    /// <summary>Mesma ideia de <see cref="VinhetasCrossfadeMs"/>, pra Trilhas.</summary>
    public int TrilhasCrossfadeMs { get; set; }
}
