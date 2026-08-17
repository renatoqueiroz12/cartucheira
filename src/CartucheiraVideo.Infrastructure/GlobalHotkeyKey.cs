namespace CartucheiraVideo.Infrastructure;

/// <summary>
/// Teclas oferecidas pra atalho global de comando externo da Cartucheira (Configurações →
/// Cartucheira → Vinhetas/Trilhas — Sprint Comando Externo, 2026-08-10). Cobre F1-F24 (as 12 teclas
/// de função comuns de qualquer teclado + as 12 "estendidas" que só teclados 100%+/programáveis
/// têm) e <see cref="Tab"/> — pedido explícito do usuário (2026-08-10, correção pós-entrega),
/// substituindo a lista original que só ia de F13 a F24.
///
/// **Aviso registrado aqui, não é placeholder nem bug — é a troca que o usuário pediu por
/// escolha própria:** diferente das F13-F24 (que praticamente nenhum outro programa usa por
/// padrão), F1-F12 e principalmente <see cref="Tab"/> são teclas de uso comum em qualquer
/// aplicativo (F1 = Ajuda na maioria dos programas Windows; Tab = navegar entre campos/janelas,
/// inclusive dentro da própria janela desta Cartucheira). Como <see cref="GlobalHotkeyService"/>
/// registra F1-F24 via <c>RegisterHotKey</c> SEM nenhum modificador (Ctrl/Alt/Shift — só
/// <c>MOD_NOREPEAT</c>), a tecla escolhida vira exclusiva do comando externo em qualquer lugar do
/// Windows enquanto o toggle estiver ligado — se o operador escolher F1, perde a Ajuda em TODO o
/// sistema enquanto o atalho daquela Cartucheira estiver habilitado. Fica por conta do operador
/// escolher uma tecla que ele sabe que não vai fazer falta (o toggle "habilitado" em Configurações
/// continua desligando o atalho a qualquer momento).
///
/// **<see cref="Tab"/> é um caso à parte, corrigido depois da entrega inicial (2026-08-10):** sem
/// nenhum modificador, o registro "funcionava" (nenhum erro do Windows) mas o comando nunca
/// disparava de verdade — confirmado testando no Windows real. Pesquisa técnica apontou a causa
/// mais provável: capturar uma tecla de navegação comum (Tab, Enter, Espaço...) via
/// <c>RegisterHotKey</c> SEM modificador nenhum é a assinatura exata de uma técnica conhecida de
/// keylogger ("hotkeyz"), e softwares de segurança/EDR modernos parecem ter mitigação específica
/// contra esse padrão — deixam o registro "ter sucesso", mas descartam a entrega do evento em
/// silêncio. Por isso <see cref="GlobalHotkeyService.Register"/> agora registra <see cref="Tab"/>
/// sempre com Ctrl junto (sai do padrão que a mitigação provavelmente filtra, e também evita
/// colidir com qualquer outro programa que já tenha reivindicado o Tab puro) — o operador precisa
/// segurar Ctrl+Tab, não só Tab; o ComboBox de Configurações (<c>CartridgeSettingsViewModel.
/// KeyboardShortcuts</c>) já mostra o rótulo "Ctrl+Tab" pra deixar isso claro. F1-F24 não
/// mudaram — nenhum relato de problema com elas.
///
/// Os valores numéricos são os códigos de tecla virtual (VK_*) reais do Win32
/// (<c>user32.dll</c>/<c>winuser.h</c>) — <see cref="GlobalHotkeyService"/> passa o valor do enum
/// direto pro parâmetro <c>vk</c> de <c>RegisterHotKey</c>, sem nenhuma tabela de tradução.
/// </summary>
public enum GlobalHotkeyKey
{
    Tab = 0x09,
    F1 = 0x70,
    F2 = 0x71,
    F3 = 0x72,
    F4 = 0x73,
    F5 = 0x74,
    F6 = 0x75,
    F7 = 0x76,
    F8 = 0x77,
    F9 = 0x78,
    F10 = 0x79,
    F11 = 0x7A,
    F12 = 0x7B,
    F13 = 0x7C,
    F14 = 0x7D,
    F15 = 0x7E,
    F16 = 0x7F,
    F17 = 0x80,
    F18 = 0x81,
    F19 = 0x82,
    F20 = 0x83,
    F21 = 0x84,
    F22 = 0x85,
    F23 = 0x86,
    F24 = 0x87
}
