namespace CartucheiraVideo.Configuration;

/// <summary>
/// Raiz do arquivo de configuração persistido em disco (<see cref="SettingsStore"/>,
/// <c>%AppData%\CartucheiraVideo\settings.json</c>) — Sprint Config Geral, parte 1 (2026-08-09),
/// primeira vez que qualquer configuração do programa sobrevive a fechar e abrir de novo.
/// </summary>
public sealed class AppSettingsSnapshot
{
    /// <summary>Hash PBKDF2 da senha de admin (Base64) — nulo se nenhuma senha foi definida ainda (primeiro acesso ao menu Configurações).</summary>
    public string? AdminPasswordHash { get; set; }

    /// <summary>Salt aleatório usado no hash acima (Base64) — nulo junto com <see cref="AdminPasswordHash"/>.</summary>
    public string? AdminPasswordSalt { get; set; }

    public DeckSettingsSnapshot DeckA { get; set; } = new();

    public DeckSettingsSnapshot DeckB { get; set; } = new();

    /// <summary>Configuração da Cartucheira (Vinhetas/Trilhas) — Sprint Cartucheira (2026-08-09).</summary>
    public CartridgeSettingsSnapshot Cartridges { get; set; } = new();

    /// <summary>
    /// Porta UDP (127.0.0.1) usada pelo comando externo da mesa de áudio (projeto
    /// <c>CartucheiraVideo.Api</c>, <c>UdpTriggerListener</c>) — UMA porta só, compartilhada por
    /// TUDO: as duas Cartucheiras (Vinhetas/Trilhas) e os dois Decks. Consolidada aqui (2026-08-13,
    /// extensão da Sprint Comando Externo Decks/Cartucheira) depois de existirem duas portas
    /// separadas (uma pra Cartucheira, fixa em 8888; outra pra Deck, editável, default 8889) terem
    /// causado confusão real de configuração (o operador trocou a porta de um lado sem perceber que
    /// o outro continuava noutra) — quem sempre diferenciou qual elemento (Vinhetas, Trilhas, Deck
    /// 1, Deck 2) e qual ação foi o TEXTO do pacote, nunca a porta, então consolidar numa porta só
    /// não perde nada. Default 8888 (o valor que já estava em uso/configurado na mesa antes desta
    /// consolidação). Editável — troca vale na hora do Salvar em Configurações, sem reiniciar o
    /// programa (<c>MainViewModel.ReapplyUdpListenerPort</c>).
    /// </summary>
    public int UdpTriggerPort { get; set; } = 8888;
}
