using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// Sprint Telas-Saida-Direta (2026-11-10): uma linha da lista de monitores na aba "Telas" da tela de
/// Configuração do Deck — um checkbox por monitor do Windows detectado (via
/// <c>System.Windows.Forms.Screen.AllScreens</c>, chamada pelo <see cref="DeckSettingsViewModel"/>
/// que monta esta lista, nunca por esta classe — ela é só dado/estado de UI, mesmo padrão de
/// <see cref="BackgroundArtItemViewModel"/>).
///
/// <see cref="DeviceName"/> é o identificador estável que vai pro disco
/// (<c>Core.Deck.ScreenOutputDeviceNames</c>) — algo como <c>"\\.\DISPLAY2"</c>, que o Windows
/// reatribui ao mesmo conector físico entre reinicializações. <see cref="IsCurrentlyConnected"/>
/// existe pra "falha honesta": se o operador tinha escolhido um monitor que agora está desconectado
/// (placa desligada, cabo tirado), a escolha continua salva e marcada, mas a linha mostra um aviso em
/// vez de fingir que está tudo bem — ver <see cref="DeckSettingsViewModel"/> pra como a lista é
/// montada combinando os monitores detectados agora com a seleção persistida.
///
/// Fix v1.0.57 (2026-11-11): <see cref="IsOperatorScreen"/> novo — incidente real: o usuário achou
/// que o app estava "enxergando errado" os monitores porque marcar "Tela 1" não mandava imagem pra
/// lugar nenhum, sem perceber que "Tela 1" era justamente o monitor onde a própria Cartucheira
/// estava rodando (bloqueado de propósito, ver <c>DeckViewModel.RefreshScreenOutputs</c>). Agora a
/// própria linha da lista avisa isso, em vez do operador só descobrir ao tentar Salvar.
/// </summary>
public sealed class ScreenOptionViewModel : ViewModelBase
{
    private bool _isSelected;

    public ScreenOptionViewModel(string deviceName, string label, bool isSelected, bool isCurrentlyConnected, bool isOperatorScreen = false)
    {
        DeviceName = deviceName;
        Label = label;
        _isSelected = isSelected;
        IsCurrentlyConnected = isCurrentlyConnected;
        IsOperatorScreen = isOperatorScreen;
    }

    /// <summary>Identificador estável do monitor (ex.: <c>"\\.\DISPLAY2"</c>) — imutável, é o que persiste.</summary>
    public string DeviceName { get; }

    /// <summary>Texto amigável exibido ao lado do checkbox (ex.: "Tela 2 — 1920x1080 em (1920,0)").</summary>
    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Falso quando este monitor estava selecionado antes (persistido) mas não foi detectado agora
    /// (placa desconectada/desligada) — a aba mostra um aviso "não detectada agora" nessa linha em
    /// vez de remover a seleção, pra não perder a configuração do operador por uma desconexão
    /// temporária.
    /// </summary>
    public bool IsCurrentlyConnected { get; }

    /// <summary>Inverso de <see cref="IsCurrentlyConnected"/> — só pra facilitar o Binding de Visibility do aviso na XAML (evita precisar de um converter booleano-inverso só pra isto).</summary>
    public bool IsNotCurrentlyConnected => !IsCurrentlyConnected;

    /// <summary>
    /// Verdadeiro quando este monitor é o que hospeda AGORA alguma janela de verdade da própria
    /// Cartucheira (MainWindow, SettingsWindow etc.) — mesma checagem de
    /// <see cref="DeckViewModel.GetOperatorScreenDeviceNames"/>, usada aqui só pra AVISAR na lista
    /// (ver Fix v1.0.57 no comentário da classe); marcar esta linha continua permitido (a seleção
    /// salva nunca é bloqueada), só não vai abrir uma Tela aqui de verdade — a proteção real
    /// continua sendo feita em <see cref="DeckViewModel.RefreshScreenOutputs"/>.
    /// </summary>
    public bool IsOperatorScreen { get; }
}
