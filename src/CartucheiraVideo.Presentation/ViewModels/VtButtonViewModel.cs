using System.Windows.Input;
using CartucheiraVideo.Core;
using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// ViewModel de um cartucho da Botoneira (Vinhetas/Trilhas). Até a Sprint Cartucheira (2026-08-09)
/// envolvia um <c>VtButtonConfig</c> fictício (clicar só avisava "ainda não implementado") — agora
/// envolve um <see cref="Core.AudioPad"/> real, populado a partir de um arquivo de verdade lido de
/// uma pasta de programa aberta pelo operador (ver <see cref="AudioBankViewModel"/>). O cartucho em
/// si não sabe tocar sozinho — <see cref="PlayAudioCommand"/> só delega pro <see cref="AudioBankViewModel"/>
/// dono deste cartucho (via o <c>onPlayAudio</c> recebido no construtor), que é quem tem o
/// <c>Engine.AudioEngine</c> por trás.
///
/// Sprint Cartucheira 2, parte 1 (2026-08-10): <see cref="DurationDisplay"/> passa a mostrar a
/// duração real do arquivo (lida via <c>Engine.AudioEngine.GetDurationAsync</c>, ver
/// <see cref="SetDuration"/> — quem chama isso e trata a assincronia é o
/// <see cref="AudioBankViewModel"/> dono) quando o cartucho está parado, e uma contagem regressiva
/// viva (duração menos o tempo decorrido, ver <see cref="UpdateElapsed"/>) enquanto está tocando.
///
/// Sprint Cartucheira 2, parte 2 (2026-08-10): três acréscimos pedidos pelo usuário —
/// (1) <see cref="Color"/> passa a poder ser sobrescrita pelo operador (<see cref="ColorOverride"/>,
/// escolhida via Ctrl+clique direito → Cor no cartucho, ver MainWindow.xaml); (2)
/// <see cref="IsLoopEnabled"/>, também via Ctrl+clique direito → Loop, passado pro
/// <c>Engine.AudioEngine.Play</c> pelo <see cref="AudioBankViewModel"/> dono a cada Play; (3)
/// <see cref="IsSelected"/>, marcado por clique direito simples (sem Ctrl) — prepara o disparo por
/// comando externo (Tab do teclado) da próxima parte (arquitetura §14.2), só a seleção visual nesta
/// parte. Cor/loop mudados aqui disparam <c>onOverrideChanged</c> pro <see cref="AudioBankViewModel"/>
/// dono persistir via <c>Configuration.CartridgeOverride</c> (chave: <see cref="FilePath"/>) — sem
/// isso, a customização se perderia a cada recarregar a pasta/reiniciar o programa, já que este
/// ViewModel é recriado do zero em cada scan.
///
/// Sprint Cartucheira 2, parte 2 - correção (2026-08-10): a seleção deixa de ser um estado que o
/// próprio cartucho gerencia sozinho (antes, <c>ToggleSelected</c> só invertia <see cref="IsSelected"/>
/// localmente) — agora ela é coordenada pelo <see cref="AudioBankViewModel"/> dono (via o novo
/// callback <c>onSelectionToggle</c> recebido no construtor), porque a seleção passou a ser MÚLTIPLA
/// e ORDENADA (<see cref="SelectionNumber"/>: 1º cartucho selecionado vira 1, o 2º vira 2, e assim
/// por diante — pedido explícito do usuário, prepara a ordem de disparo por Tab da próxima parte,
/// arquitetura §14.2). Só o dono do banco sabe a ordem de todos os cartuchos selecionados dele; por
/// isso <see cref="IsSelected"/> e <see cref="SelectionNumber"/> ganharam setter privado — só mudam
/// via <see cref="SetSelectionState"/>, chamado pelo <c>AudioBankViewModel.ToggleSelection</c>.
///
/// Sprint Cartucheira 2, parte 2 - correção #2 (2026-08-10): a tentativa anterior de fazer o clique
/// esquerdo num cartucho JÁ selecionado só desmarcar (sem tocar/parar) criou um problema pior — o
/// usuário reportou que, com isso, um cartucho selecionado deixava de tocar no clique esquerdo, e
/// pediu de volta o oposto: <see cref="PlayAudioCommand"/> agora SEMPRE toca/para, esteja o cartucho
/// selecionado ou não — nenhuma ramificação por <see cref="IsSelected"/> aqui. A seleção some
/// sozinha quando o cartucho termina de tocar (ver <c>AudioBankViewModel.ClearNowPlaying</c>, que
/// tira o cartucho da fila e renumera os que sobraram — não é mais o clique que desmarca).
/// </summary>
public sealed class VtButtonViewModel : ViewModelBase
{
    private readonly AudioPad _pad;
    private readonly Action<VtButtonViewModel> _onOverrideChanged;
    private readonly Action<VtButtonViewModel> _onSelectionToggle;
    private bool _isPlaying;
    private bool _isSelected;
    private int? _selectionNumber;
    private bool _isLoopEnabled;
    private string? _colorOverride;
    private TimeSpan? _duration;
    private TimeSpan? _remaining;
    private double _progress;

    public VtButtonViewModel(int number, AudioPad pad, Action<VtButtonViewModel> onPlayAudio, Action<VtButtonViewModel> onOverrideChanged, Action<VtButtonViewModel> onSelectionToggle)
    {
        _pad = pad ?? throw new ArgumentNullException(nameof(pad));
        ArgumentNullException.ThrowIfNull(onPlayAudio);
        _onOverrideChanged = onOverrideChanged ?? throw new ArgumentNullException(nameof(onOverrideChanged));
        _onSelectionToggle = onSelectionToggle ?? throw new ArgumentNullException(nameof(onSelectionToggle));

        Number = number;

        // Sprint Cartucheira 2, parte 2 - correção #2 (2026-08-10): clique esquerdo sempre toca/para,
        // esteja o cartucho selecionado ou não — a tentativa anterior de desmarcar em vez de tocar
        // quando já selecionado foi revertida a pedido explícito do usuário ("preciso que o esquerdo
        // dê o play se selecionado ou não"). A seleção agora só sai sozinha quando o cartucho termina
        // de tocar (AudioBankViewModel.ClearNowPlaying), nunca pelo clique em si.
        PlayAudioCommand = new RelayCommand(_ => onPlayAudio(this));

        SetColorCommand = new RelayCommand(parameter =>
        {
            if (parameter is string hex && !string.IsNullOrWhiteSpace(hex))
            {
                _colorOverride = hex;
                OnPropertyChanged(nameof(Color));
                _onOverrideChanged(this);
            }
        });

        ToggleLoopCommand = new RelayCommand(_ =>
        {
            IsLoopEnabled = !IsLoopEnabled;
            _onOverrideChanged(this);
        });
    }

    public int Number { get; }

    public string Name => _pad.Name;

    /// <summary>Cor efetiva do pad — a escolhida pelo operador (<see cref="ColorOverride"/>), ou a cor padrão do banco (<c>AudioBankViewModel.PadColorFor</c>) se nenhuma foi escolhida ainda.</summary>
    public string Color => _colorOverride ?? _pad.Color;

    /// <summary>Cor customizada pelo operador — nulo = nenhuma. Usado pelo AudioBankViewModel dono pra persistir/restaurar via Configuration.CartridgeOverride.</summary>
    public string? ColorOverride => _colorOverride;

    /// <summary>Caminho completo do arquivo de áudio real por trás deste cartucho — usado pelo AudioBankViewModel pra carregar no Engine e como chave de persistência dos overrides. Vazio nos cartuchos de exemplo (mockup).</summary>
    public string FilePath => _pad.Media.Path;

    /// <summary>
    /// Verdadeiro quando este é o cartucho tocando agora no banco (só um por vez dentro do mesmo
    /// banco — controlado externamente pelo <see cref="AudioBankViewModel"/>, nunca por si mesmo).
    /// Usado pelo destaque visual do pad em <c>MainWindow.xaml</c> (Theme.PadButton — fundo vermelho
    /// enquanto toca, Sprint Cartucheira 2, parte 2). Voltar a falso restaura o mostrador pra
    /// duração cheia (ver <see cref="DurationDisplay"/>) e zera <see cref="Progress"/>.
    /// </summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (SetProperty(ref _isPlaying, value) && !value)
            {
                _remaining = _duration;
                Progress = 0;
                OnPropertyChanged(nameof(DurationDisplay));
            }
        }
    }

    /// <summary>
    /// Verdadeiro quando o operador marcou este cartucho pra receber o comando externo de disparo
    /// (clique direito simples, sem Ctrl) — Sprint Cartucheira 2, parte 2 (2026-08-10). Só a marcação
    /// visual nesta parte (destaque azul no pad); o disparo por Tab/console em si, com o round-robin
    /// entre os cartuchos selecionados, fica pra parte 3 (arquitetura §14.2). Setter privado desde a
    /// correção de 2026-08-10 — só o <see cref="AudioBankViewModel"/> dono muda isto (via <see cref="SetSelectionState"/>),
    /// porque ele é quem sabe a ordem de todos os cartuchos selecionados do banco.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        private set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Posição deste cartucho na fila de seleção do banco (1º selecionado = 1, 2º = 2...) — nulo
    /// quando não está selecionado. Sprint Cartucheira 2, parte 2 - correção (2026-08-10): pedido
    /// explícito do usuário ("selecionei uma cartucho ele fica com o numero 1, seleccionei mais um ai
    /// o outro fica com 2") — prepara a ordem de disparo por Tab da próxima parte (arquitetura §14.2).
    /// Exibido como selo numerado no pad (MainWindow.xaml). Setter privado pelo mesmo motivo de
    /// <see cref="IsSelected"/> — só o <see cref="AudioBankViewModel"/> dono sabe renumerar a fila
    /// inteira quando um cartucho é removido do meio dela.
    /// </summary>
    public int? SelectionNumber
    {
        get => _selectionNumber;
        private set => SetProperty(ref _selectionNumber, value);
    }

    /// <summary>Verdadeiro = este cartucho reinicia sozinho ao terminar (passado pro Engine.AudioEngine.Play a cada Play pelo AudioBankViewModel dono) — escolhido via Ctrl+clique direito → Loop.</summary>
    public bool IsLoopEnabled
    {
        get => _isLoopEnabled;
        set => SetProperty(ref _isLoopEnabled, value);
    }

    /// <summary>Progresso de 0 a 100 enquanto toca — alimenta a barra de execução no topo da Cartucheira (AudioBankViewModel.NowPlaying). Fica em 0 quando parado ou quando a duração é desconhecida (nenhum valor inventado).</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    /// <summary>
    /// Parado: duração total do cartucho ("--:--" se ainda não lida, ou se for um cartucho de
    /// exemplo sem arquivo real). Tocando: contagem regressiva até "00:00" (ver <see cref="UpdateElapsed"/>).
    /// </summary>
    public string DurationDisplay => FormatTime(IsPlaying ? _remaining : _duration);

    public ICommand PlayAudioCommand { get; }

    /// <summary>Define a cor deste cartucho — CommandParameter é o hex (ex.: "#FF2D7DFF"), vindo do submenu Cor do menu de contexto (Ctrl+clique direito no pad).</summary>
    public ICommand SetColorCommand { get; }

    /// <summary>Liga/desliga o loop deste cartucho — item de menu "Loop" do menu de contexto (Ctrl+clique direito no pad).</summary>
    public ICommand ToggleLoopCommand { get; }

    /// <summary>Chamado pelo AudioBankViewModel quando a duração real do arquivo termina de ser lida (assíncrono, ver Engine.AudioEngine.GetDurationAsync) — nulo se a leitura falhou/não se aplica (cartucho de exemplo).</summary>
    public void SetDuration(TimeSpan? duration)
    {
        _duration = duration;
        _remaining = duration;
        OnPropertyChanged(nameof(DurationDisplay));
    }

    /// <summary>Chamado pelo AudioBankViewModel a cada tick de posição do Engine (só enquanto ESTE cartucho é o que está tocando) — recalcula a contagem regressiva e o progresso (0-100). Sem efeito se a duração é desconhecida (não há "zero" pra contar até, nem percentual pra calcular).</summary>
    public void UpdateElapsed(TimeSpan elapsed)
    {
        if (_duration is not { } duration || duration <= TimeSpan.Zero)
        {
            return;
        }

        var remaining = duration - elapsed;
        _remaining = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        OnPropertyChanged(nameof(DurationDisplay));

        var ratio = elapsed.TotalMilliseconds / duration.TotalMilliseconds;
        Progress = ratio switch
        {
            < 0 => 0,
            > 1 => 100,
            _ => ratio * 100.0
        };
    }

    /// <summary>Chamado pelo AudioBankViewModel dono ao carregar overrides persistidos (Configuration.CartridgeOverride, ver LoadOverrides) — não dispara onOverrideChanged de volta (isto é aplicar o que já está salvo, não uma mudança nova do operador pra persistir).</summary>
    public void ApplyOverride(string? colorOverride, bool loopEnabled)
    {
        _colorOverride = colorOverride;
        _isLoopEnabled = loopEnabled;
        OnPropertyChanged(nameof(Color));
        OnPropertyChanged(nameof(IsLoopEnabled));
    }

    /// <summary>
    /// Pede pro AudioBankViewModel dono alternar a seleção deste cartucho — chamado pelo handler de
    /// clique direito simples (sem Ctrl) em MainWindow.xaml.cs. Sprint Cartucheira 2, parte 2 -
    /// correção (2026-08-10): antes invertia <see cref="IsSelected"/> localmente; agora só delega pro
    /// dono (<c>onSelectionToggle</c>) — só ele sabe adicionar/remover este cartucho da fila ordenada
    /// de seleção do banco e renumerar o resto (ver <see cref="SelectionNumber"/>).
    /// </summary>
    public void ToggleSelected() => _onSelectionToggle(this);

    /// <summary>
    /// Chamado exclusivamente pelo AudioBankViewModel dono (nunca pelo próprio cartucho) pra aplicar
    /// o resultado de uma mudança na fila de seleção do banco — Sprint Cartucheira 2, parte 2 -
    /// correção (2026-08-10). <paramref name="selectionNumber"/> deve ser não-nulo quando
    /// <paramref name="isSelected"/> é verdadeiro, e nulo quando é falso — quem garante essa
    /// consistência é o dono (AudioBankViewModel.RenumberSelection), não este método.
    /// </summary>
    public void SetSelectionState(bool isSelected, int? selectionNumber)
    {
        IsSelected = isSelected;
        SelectionNumber = selectionNumber;
    }

    private static string FormatTime(TimeSpan? time) =>
        time is { } t && t >= TimeSpan.Zero ? t.ToString(@"mm\:ss") : "--:--";
}
