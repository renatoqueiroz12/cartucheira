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

    /// <summary>
    /// Saídas Livewire configuradas pelo usuário — Sprint Livewire-2 (2026-09-13). Cada uma pode ser
    /// selecionada no seletor de dispositivo de áudio de Vinhetas/Trilhas (valor prefixado
    /// <c>"livewire:{Id}"</c>) — ver <c>arquitetura/livewire-plano.md</c> no projeto e
    /// <see cref="LivewireOutputSettings"/>.
    /// </summary>
    public List<LivewireOutputSettings> LivewireOutputs { get; set; } = new();

    /// <summary>
    /// IP local (IPv4) da placa de rede usada pra transmitir/anunciar Livewire — Sprint Livewire-2.
    /// Nulo deixa o sistema escolher a interface de saída, o que NÃO é recomendado numa máquina com
    /// mais de uma placa de rede (lição confirmada por teste real na Mesa Console, ver
    /// <c>arquitetura/livewire-plano.md</c>): o Windows pode escolher uma interface que não chega na
    /// rede Livewire, sem erro nenhum — o pacote "sai" e ninguém recebe.
    /// </summary>
    public string? LivewireNetworkInterfaceIp { get; set; }

    /// <summary>
    /// IP local (IPv4) da placa de rede à qual o NDI desta máquina fica restrito — Sprint
    /// NDI-Placa-de-Rede (2026-09-13), pedida depois de dedicar uma placa só pro Livewire ("o NDI
    /// vai precisar ter o tratamento de qual placa de rede ele vai transmitir, pois agora ele está
    /// transmitindo pela placa do Livewire, e não pode"). Nulo deixa o NDI escolher sozinho (padrão
    /// de fábrica). Diferente de <see cref="LivewireNetworkInterfaceIp"/> em um jeito importante:
    /// este valor não é lido diretamente por nenhum socket da Cartucheira — ele só existe pra
    /// lembrar, entre uma sessão e outra, o que foi escrito em
    /// <c>CartucheiraVideo.Ndi.NdiNetworkAdapterConfig</c> (um arquivo do PRÓPRIO WINDOWS,
    /// <c>%ProgramData%\NDI\ndi-config.v1.json</c>, que vale pra QUALQUER programa NDI da máquina,
    /// não só a Cartucheira — ver comentário de classe lá pro motivo).
    /// </summary>
    public string? NdiNetworkInterfaceIp { get; set; }

    /// <summary>
    /// Sprint NDI-Firewall-Livewire (2026-09-13): último valor de <see cref="LivewireNetworkInterfaceIp"/>
    /// pro qual a regra de Firewall do Windows (<c>CartucheiraVideo.Ndi.NdiFirewallGuard</c>) foi
    /// aplicada COM SUCESSO — pedida depois que o usuário confirmou, com testes reais, que
    /// <see cref="NdiNetworkInterfaceIp"/> sozinho (a config "educada" do próprio NDI) não impediu o
    /// NDI de sair pela placa do Livewire nesta máquina.
    ///
    /// Existe só pra evitar pedir a janela de permissão de Administrador (UAC) toda vez que a
    /// Cartucheira abre: comparando este valor com <see cref="LivewireNetworkInterfaceIp"/> na hora,
    /// só reaplica a regra (e só pede UAC de novo) quando a placa do Livewire realmente mudou desde a
    /// última vez que funcionou. Nulo tanto no primeiro uso quanto sempre que nenhuma placa de
    /// Livewire estiver configurada (nesse caso não tem nada pra proteger — a regra é removida).
    /// </summary>
    public string? NdiFirewallGuardAppliedForLivewireIp { get; set; }

    /// <summary>
    /// Fix pós-entrega da v1.0.24 (v1.0.25, mesmo dia): versão do DESENHO das regras de Firewall
    /// (<see cref="CartucheiraVideo.Ndi.NdiFirewallGuard.RuleSchemaVersion"/>) pra qual
    /// <see cref="NdiFirewallGuardAppliedForLivewireIp"/> foi gravado. Existe porque a comparação
    /// original (só o IP) escondeu um bug real: a v1.0.24 corrigiu o desenho da regra (acrescentou
    /// bloqueio de ENTRADA, que faltava na v1.0.23), mas como o IP da placa do Livewire do usuário não
    /// tinha mudado entre uma versão e outra, o programa nunca chamou
    /// <c>NdiFirewallGuard.ApplyBlockRule</c> de novo — a correção da v1.0.24 nunca chegou a rodar de
    /// verdade na máquina dele, mesmo já instalada ("tudo igual, funcionando pela placa do
    /// livewire"). Agora <c>MainViewModel.ReapplyNdiFirewallGuard</c> só considera a regra "já
    /// aplicada" quando ESTE valor bate com <c>NdiFirewallGuard.RuleSchemaVersion</c> atual, além do
    /// IP baterem — qualquer mudança futura no desenho das regras (e o incremento correspondente da
    /// constante) força uma reaplicação de verdade (um novo UAC), mesmo que a placa não tenha mudado.
    /// Nulo até a primeira aplicação bem-sucedida com esta lógica (força reaplicar em qualquer
    /// instalação vindo de antes da v1.0.25, inclusive quem já tinha <see cref="NdiFirewallGuardAppliedForLivewireIp"/>
    /// preenchido de uma versão anterior).
    /// </summary>
    public int? NdiFirewallGuardAppliedRuleSchemaVersion { get; set; }
}
