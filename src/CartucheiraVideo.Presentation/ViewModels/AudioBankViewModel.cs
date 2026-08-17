using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Core;
using CartucheiraVideo.Engine;
using CartucheiraVideo.Presentation.Mvvm;
using CartucheiraVideo.Presentation.Views;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// Um banco de botoes da Botoneira (ex.: "Vinhetas", "Trilhas").
///
/// Ate a conversao visual pro mockup v8 (2026-08-08), a Botoneira era um unico painel com VARIOS
/// bancos, um selecionado por vez via dropdown — Renomear/Novo/Editar viviam no MainViewModel,
/// operando sobre "o banco selecionado". O mockup v8 (nota de design #15) fixa a Botoneira em DOIS
/// bancos sempre visiveis lado a lado (Vinhetas | Trilhas), sem conceito de "selecionado" — por
/// isso essas tres acoes migraram pra CA, cada metade opera sobre o proprio banco diretamente.
///
/// Sprint Cartucheira (2026-08-09): motor de áudio real. Cada banco tem seu próprio
/// <see cref="Engine.AudioEngine"/> — instância dedicada e totalmente independente por banco
/// (desde a Sprint Crossfade v2, 2026-08-11, sobre NAudio; ver <see cref="Engine.AudioEngine"/> pro
/// porquê), inclusive em dispositivos de saída diferentes, ver <see cref="SetOutputDevice"/>. Dentro de UM banco só um
/// cartucho toca por vez — clicar num cartucho novo corta o anterior (mesma exclusividade que já
/// existe no Deck); clicar de novo no cartucho que já está tocando para ele (alternância
/// "dispara/corta", convenção comum de cart wall).
///
/// Correção pós-entrega (2026-08-10): enquanto nenhuma pasta raiz é configurada, o banco mostra
/// programas de exemplo do mockup v8 (<see cref="LoadSamplePrograms"/>) em vez de ficar vazio —
/// decisão do usuário ("mantenha os botões da cartucheira, assim como o mockup").
///
/// Sprint Cartucheira 2, parte 1 (2026-08-10): reestruturado pra navegação por subpasta — pedido
/// direto do usuário ("trabalhar com subpasta... seleção de vinhetas de cada programa sem precisar
/// de um banco de cadastro"). A pasta configurada em Configurações → Cartucheira agora é uma
/// RAIZ: só mostra as subpastas diretas (cada uma um "programa", ver <see cref="Programs"/>/
/// <see cref="CartucheiraProgramViewModel"/>) — nenhum cartucho solto direto na raiz. Abrir um
/// programa (<see cref="OpenProgram"/>) mostra os áudios daquela subpasta como cartuchos
/// (<see cref="Buttons"/>, igual antes); <see cref="Back"/> volta pra lista de programas. Cada
/// cartucho real também ganha, nesta sprint, a duração real do arquivo (lida em segundo plano via
/// <see cref="AudioEngine.GetDurationAsync"/>, sem travar o carregamento dos outros) e uma
/// contagem regressiva viva enquanto toca (<see cref="VtButtonViewModel.UpdateElapsed"/>, vindo de
/// <see cref="AudioEngine.PositionChanged"/>).
///
/// Sprint Cartucheira 2, parte 2 (2026-08-10): <see cref="NowPlaying"/> substitui o antigo campo
/// privado "_currentlyPlaying" — agora exposto publicamente pra alimentar a barra de execução (nome
/// + progresso + regressiva) no topo de cada metade da Botoneira (MainWindow.xaml). Também ganha a
/// persistência de customização por cartucho (<see cref="LoadOverrides"/>/<see cref="_overridesByPath"/>,
/// chave: caminho do arquivo) — cor e loop escolhidos via Ctrl+clique direito num cartucho
/// (VtButtonViewModel) chegam aqui por <see cref="OnCartridgeOverrideChanged"/> e são repassados pro
/// <c>persistOverride</c> recebido do MainViewModel (que sabe em qual lista salvar,
/// Cartridges.VinhetasOverrides ou TrilhasOverrides). Loop também passa a ser aplicado de verdade no
/// Engine a cada Play (<see cref="PlayPad"/>).
///
/// Sprint Cartucheira 2, parte 2 - correção (2026-08-10): seleção múltipla e ordenada de cartuchos
/// pra comando externo — pedido explícito do usuário ("selecionar uma ou mais cartucho... ai
/// selecionei uma cartucho ele fica com o numero 1, seleccionei mais um ai o outro fica com 2").
/// <see cref="_selectedPads"/> é a fila (ordem de seleção = ordem de disparo futura, arquitetura
/// §14.2); <see cref="ToggleSelection"/> é o único lugar que adiciona/remove dela e renumera
/// (<see cref="RenumberSelection"/>) os cartuchos que sobraram — chamado pelo próprio
/// VtButtonViewModel (clique direito simples). A fila é esvaziada (<c>_selectedPads.Clear()</c>) em
/// todo lugar que já zera <see cref="Buttons"/> (<see cref="Back"/>,
/// <see cref="LoadCartridgesFromRealFolder"/>, <see cref="LoadSampleCartridges"/>) — os
/// VtButtonViewModel antigos são descartados a cada scan/troca de programa, então uma referência
/// antiga na fila seria só lixo (e um Tab externo nunca deveria mirar num cartucho que nem existe
/// mais na tela).
///
/// Sprint Cartucheira 2, parte 2 - correção #2 (2026-08-10): o clique esquerdo num cartucho JÁ
/// selecionado tinha virado "só desmarca, não toca" — o usuário reportou que isso é pior (queria
/// tocar mesmo estando selecionado) e pediu o oposto: <see cref="VtButtonViewModel.PlayAudioCommand"/>
/// volta a SEMPRE tocar/parar, e a seleção passa a sair sozinha quando o cartucho termina de tocar
/// (<see cref="ClearNowPlaying"/> chama <see cref="ToggleSelection"/> se o cartucho que parou estava
/// selecionado) — cobre tanto o fim natural do áudio (<see cref="OnPlaybackEnded"/>) quanto parada
/// manual/troca de cartucho (<see cref="Stop"/>), já que os dois passam por
/// <see cref="ClearNowPlaying"/> agora.
///
/// Sprint Crossfade v1 (2026-08-11) — REVERTIDA no mesmo dia: chegou a existir um caminho em
/// <see cref="PlayPad"/> que usava dois MediaPlayer do LibVLCSharp simultâneos pra misturar a troca
/// entre cartuchos. Testado de verdade pelo usuário e confirmou, na prática, um risco técnico real
/// do LibVLCSharp (volume de um MediaPlayer vazando pros outros) — depois de rodar, o áudio das DUAS
/// Cartucheiras ficou mudo, até fechar e reabrir o programa.
///
/// Sprint Crossfade v2 (2026-08-11, mesmo dia) — reintroduzida depois de pesquisa confirmar a causa
/// raiz real (bug aberto e sem correção do LibVLCSharp com o módulo "mmdevice", ver o comentário
/// completo em <see cref="Engine.AudioEngine"/>) e trocar o motor desta classe pra NAudio, onde
/// volume por cartucho é só um float isolado em memória gerenciada — sem estado nativo
/// compartilhado, sem o risco que derrubou a v1. <see cref="PlayPad"/> agora chama
/// <see cref="AudioEngine.Play"/> uma única vez por transição (sem <see cref="Stop"/> antes) — o
/// próprio Engine decide crossfade-ou-corte-seco internamente, baseado em
/// <see cref="SetCrossfadeDuration"/>. <see cref="TriggerExternalCommand"/> também ganha de volta a
/// correção pós-entrega da v1 (F2/teclado passa pelo mesmo caminho de crossfade que o clique manual,
/// em vez de cortar seco direto).
/// </summary>
public sealed class AudioBankViewModel : ViewModelBase, IDisposable
{
    private static readonly string[] AudioExtensions = { ".mp3", ".wav", ".wma", ".aac", ".m4a", ".flac", ".ogg" };

    private readonly Action<string> _onStatus;
    private readonly Action<string, string?, bool> _persistOverride;
    private readonly AudioEngine _audioEngine;
    private readonly Dictionary<string, CartridgeOverride> _overridesByPath = new();
    private readonly List<VtButtonViewModel> _selectedPads = new();
    private string _name;
    private string? _rootFolderPath;
    private string? _currentProgramPath;
    private string _currentProgramName = string.Empty;
    private VtButtonViewModel? _nowPlaying;
    private bool _isInsideProgram;
    private bool _hasButtons;
    private bool _hasPrograms;

    public AudioBankViewModel(string name, Action<string> onStatus, Action<string, string?, bool> persistOverride)
    {
        _name = name;
        _onStatus = onStatus ?? throw new ArgumentNullException(nameof(onStatus));
        _persistOverride = persistOverride ?? throw new ArgumentNullException(nameof(persistOverride));
        _audioEngine = new AudioEngine();

        _audioEngine.MediaEnded += (_, _) => Application.Current?.Dispatcher.Invoke(OnPlaybackEnded);
        _audioEngine.ErrorOccurred += (_, message) => Application.Current?.Dispatcher.Invoke(() =>
        {
            var failedPadName = NowPlaying?.Name;
            OnPlaybackEnded();
            _onStatus($"{Name}: erro ao tocar \"{failedPadName}\" ({message}).");
        });
        // Sprint Cartucheira 2, parte 1 (2026-08-10): alimenta a contagem regressiva do cartucho
        // tocando agora — sem efeito se nada estiver tocando ou se a duração dele for desconhecida
        // (VtButtonViewModel.UpdateElapsed já trata os dois casos).
        _audioEngine.PositionChanged += (_, elapsed) => Application.Current?.Dispatcher.Invoke(() => NowPlaying?.UpdateElapsed(elapsed));

        Programs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CountLabel));
        Buttons.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CountLabel));

        RenameCommand = new RelayCommand(_ => Rename());
        ReloadCommand = new RelayCommand(_ => Reload());
        BackCommand = new RelayCommand(_ => Back());
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(HeaderTitle));
            }
        }
    }

    /// <summary>Programas (subpastas diretas da raiz) desta Cartucheira — visível quando <see cref="IsInsideProgram"/> é falso.</summary>
    public ObservableCollection<CartucheiraProgramViewModel> Programs { get; } = new();

    /// <summary>Cartuchos do programa aberto — visível quando <see cref="IsInsideProgram"/> é verdadeiro. Mesma coleção/papel de antes da navegação por subpasta.</summary>
    public ObservableCollection<VtButtonViewModel> Buttons { get; } = new();

    /// <summary>Verdadeiro dentro de um programa aberto (mostrando cartuchos); falso na lista de programas.</summary>
    public bool IsInsideProgram
    {
        get => _isInsideProgram;
        private set
        {
            if (SetProperty(ref _isInsideProgram, value))
            {
                OnPropertyChanged(nameof(HeaderTitle));
                OnPropertyChanged(nameof(CountLabel));
            }
        }
    }

    /// <summary>Nome do programa aberto — vazio na lista de programas.</summary>
    public string CurrentProgramName
    {
        get => _currentProgramName;
        private set
        {
            if (SetProperty(ref _currentProgramName, value))
            {
                OnPropertyChanged(nameof(HeaderTitle));
            }
        }
    }

    /// <summary>Título do cabeçalho — só o nome do banco na lista de programas; "Banco › Programa" dentro de um programa aberto.</summary>
    public string HeaderTitle => IsInsideProgram ? $"{Name} › {CurrentProgramName}" : Name;

    /// <summary>Contagem exibida no cabeçalho — programas na lista de programas, cartuchos dentro de um programa aberto.</summary>
    public string CountLabel => IsInsideProgram
        ? $"{Buttons.Count} cartucho(s)"
        : $"{Programs.Count} programa(s)";

    /// <summary>Verdadeiro quando o programa aberto tem pelo menos um cartucho — controla a mensagem honesta de "pasta vazia" dentro de um programa.</summary>
    public bool HasButtons
    {
        get => _hasButtons;
        private set => SetProperty(ref _hasButtons, value);
    }

    /// <summary>Verdadeiro quando há pelo menos um programa (subpasta) — controla a mensagem honesta de "nenhum programa encontrado" na raiz.</summary>
    public bool HasPrograms
    {
        get => _hasPrograms;
        private set => SetProperty(ref _hasPrograms, value);
    }

    /// <summary>
    /// Cartucho tocando agora neste banco (só um por vez) — nulo se nada está tocando. Sprint
    /// Cartucheira 2, parte 2 (2026-08-10): alimenta a barra de execução (nome + progresso +
    /// regressiva) no topo da metade correspondente da Botoneira em MainWindow.xaml.
    /// </summary>
    public VtButtonViewModel? NowPlaying
    {
        get => _nowPlaying;
        private set
        {
            if (SetProperty(ref _nowPlaying, value))
            {
                OnPropertyChanged(nameof(HasNowPlaying));
            }
        }
    }

    /// <summary>Verdadeiro quando algo está tocando neste banco — controla a visibilidade da barra de execução do topo.</summary>
    public bool HasNowPlaying => NowPlaying is not null;

    public ICommand RenameCommand { get; }

    public ICommand ReloadCommand { get; }

    /// <summary>Volta da lista de cartuchos de um programa pra lista de programas.</summary>
    public ICommand BackCommand { get; }

    /// <summary>Dispositivo de saída de áudio usado por este banco a partir do próximo cartucho tocado (Configurações → Cartucheira). Nulo = padrão do sistema.</summary>
    public void SetOutputDevice(string? deviceId) => _audioEngine.SetOutputDevice(deviceId);

    /// <summary>Duração (ms) do crossfade aplicado na próxima troca de cartucho deste banco — Sprint Crossfade v2 (2026-08-11). Zero = corte seco (padrão).</summary>
    public void SetCrossfadeDuration(int milliseconds) => _audioEngine.SetCrossfadeDuration(milliseconds);

    /// <summary>
    /// Sprint Deck-pausa-Cartucheira (2026-08-11): pausa o cartucho tocando agora neste banco SEM
    /// soltar <see cref="NowPlaying"/>/a seleção — a barra de execução continua mostrando o mesmo
    /// cartucho, só congela (mesmo racional de <see cref="Engine.AudioEngine.Pause"/>: o cartucho
    /// não é descartado, só sai da mistura de áudio). Chamado pelo MainViewModel quando um Deck com
    /// uma ação "Ao dar Play → Cartucheira" configurada começa a tocar um vídeo.
    /// <paramref name="fadeMs"/> (Sprint fade ajustável, 2026-08-11): zero (padrão) = corte seco;
    /// maior que zero rampeia o volume até silêncio ao longo desse tempo antes de sair da mistura —
    /// ver <see cref="Engine.AudioEngine.Pause"/>. Sem efeito se nada estiver tocando neste banco.
    /// </summary>
    public void Pause(int fadeMs = 0) => _audioEngine.Pause(fadeMs);

    /// <summary>
    /// Retoma o cartucho pausado por <see cref="Pause"/> exatamente de onde parou — chamado pelo
    /// MainViewModel quando o(s) Deck(s) que pediram a pausa devolvem o controle (vídeo termina
    /// sozinho ou é parado manualmente). <paramref name="fadeMs"/>: zero (padrão) = corte seco;
    /// maior que zero rampeia o volume de volta ao longo desse tempo — ver
    /// <see cref="Engine.AudioEngine.Resume"/>. Sem efeito se nada estiver pausado.
    /// </summary>
    public void Resume(int fadeMs = 0) => _audioEngine.Resume(fadeMs);

    /// <summary>
    /// Carrega as customizações de cor/loop persistidas (Configuration.CartridgeOverride) — chamado
    /// pelo MainViewModel na construção e sempre que a janela de Configurações fecha (mesmo motivo
    /// de <see cref="SetRootFolder"/>: a lista pode ter sido salva de novo). Reaplica nos cartuchos
    /// visíveis agora (se algum programa estiver aberto) e fica guardado pra aplicar nos próximos
    /// scans de pasta também (<see cref="LoadCartridgesFromRealFolder"/>), já que os
    /// <see cref="VtButtonViewModel"/> são recriados do zero a cada um.
    /// </summary>
    public void LoadOverrides(IReadOnlyList<CartridgeOverride> overrides)
    {
        _overridesByPath.Clear();
        foreach (var o in overrides)
        {
            _overridesByPath[o.FilePath] = o;
        }

        foreach (var button in Buttons)
        {
            ApplyOverrideIfAny(button);
        }
    }

    private void ApplyOverrideIfAny(VtButtonViewModel button)
    {
        if (_overridesByPath.TryGetValue(button.FilePath, out var o))
        {
            button.ApplyOverride(o.ColorOverride, o.LoopEnabled);
        }
    }

    /// <summary>
    /// Chamado por um VtButtonViewModel deste banco quando o operador muda a cor (Ctrl+clique
    /// direito → Cor) ou o loop (Ctrl+clique direito → Loop) dele — repassa pro callback
    /// <c>persistOverride</c> recebido do MainViewModel salvar no disco. Cartuchos de exemplo (sem
    /// arquivo real, <see cref="VtButtonViewModel.FilePath"/> vazio) nunca são persistidos — não há
    /// chave estável pra eles, e várias amostras diferentes compartilhariam a mesma chave vazia.
    /// </summary>
    private void OnCartridgeOverrideChanged(VtButtonViewModel button)
    {
        if (string.IsNullOrWhiteSpace(button.FilePath))
        {
            return;
        }

        _persistOverride(button.FilePath, button.ColorOverride, button.IsLoopEnabled);
    }

    /// <summary>
    /// Adiciona/remove um cartucho da fila de seleção deste banco e renumera quem sobrou —
    /// Sprint Cartucheira 2, parte 2 - correção (2026-08-10). Chamado (1) pelo próprio
    /// VtButtonViewModel no clique direito simples nele (<c>ToggleSelected</c>, via
    /// MainWindow.xaml.cs), pra adicionar/remover normalmente; e (2) por <see cref="ClearNowPlaying"/>
    /// quando um cartucho selecionado termina de tocar, pra tirá-lo da fila sozinho (correção #2,
    /// 2026-08-10 — antes isso saía pelo clique esquerdo, ver <see cref="ClearNowPlaying"/>). Nos dois
    /// casos o cartucho não sabe (nem precisa saber) a fila inteira, só pede pro dono cuidar disso.
    /// </summary>
    private void ToggleSelection(VtButtonViewModel pad)
    {
        if (_selectedPads.Remove(pad))
        {
            pad.SetSelectionState(false, null);
        }
        else
        {
            _selectedPads.Add(pad);
        }

        RenumberSelection();
    }

    /// <summary>Reaplica a numeração sequencial (1, 2, 3...) na ordem atual da fila — chamado sempre que <see cref="ToggleSelection"/> muda a fila.</summary>
    private void RenumberSelection()
    {
        for (var i = 0; i < _selectedPads.Count; i++)
        {
            _selectedPads[i].SetSelectionState(true, i + 1);
        }
    }

    /// <summary>
    /// Define a pasta RAIZ desta Cartucheira e (re)carrega a lista de programas a partir dela —
    /// chamado no startup (com o valor persistido) e de novo sempre que a Configuração é salva.
    /// Raiz nula/vazia mostra os programas de exemplo do mockup (ver <see cref="LoadSamplePrograms"/>);
    /// raiz configurada mas inexistente/sem subpastas fica honestamente sem programa nenhum
    /// (<see cref="HasPrograms"/> falso) — sem inventar programa nenhum.
    /// </summary>
    public void SetRootFolder(string? rootFolderPath)
    {
        _rootFolderPath = rootFolderPath;
        Back();
        LoadPrograms();
    }

    /// <summary>Volta pra lista de programas, parando qualquer cartucho tocando — chamado ao trocar de raiz e pelo comando "Voltar".</summary>
    public void Back()
    {
        Stop();
        _currentProgramPath = null;
        CurrentProgramName = string.Empty;
        IsInsideProgram = false;
        Buttons.Clear();
        _selectedPads.Clear();
    }

    /// <summary>Recarrega o nível atual: a lista de programas na raiz, ou os cartuchos do programa aberto.</summary>
    private void Reload()
    {
        if (IsInsideProgram)
        {
            if (_currentProgramPath is null)
            {
                _onStatus($"\"{CurrentProgramName}\" é um programa de exemplo (sem pasta raiz configurada ainda) — nada pra recarregar do disco.");
                return;
            }

            LoadCartridgesFromRealFolder(_currentProgramPath);
            _onStatus($"Programa \"{CurrentProgramName}\" recarregado — {Buttons.Count} cartucho(s).");
        }
        else
        {
            LoadPrograms();
            _onStatus(string.IsNullOrWhiteSpace(_rootFolderPath)
                ? $"Nenhuma pasta raiz configurada para \"{Name}\" — defina em Configurações → Cartucheira."
                : $"Lista de programas de \"{Name}\" recarregada — {Programs.Count} programa(s).");
        }
    }

    private void LoadPrograms()
    {
        Programs.Clear();

        if (string.IsNullOrWhiteSpace(_rootFolderPath))
        {
            LoadSamplePrograms();
            HasPrograms = Programs.Count > 0;
            return;
        }

        if (!Directory.Exists(_rootFolderPath))
        {
            HasPrograms = false;
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(_rootFolderPath).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            Programs.Add(new CartucheiraProgramViewModel(Path.GetFileName(dir), dir, OpenProgram));
        }

        HasPrograms = Programs.Count > 0;
    }

    private void OpenProgram(CartucheiraProgramViewModel program)
    {
        Stop();
        _currentProgramPath = program.Path;
        CurrentProgramName = program.Name;
        IsInsideProgram = true;

        if (program.Path is null)
        {
            LoadSampleCartridges(program.Name);
        }
        else
        {
            LoadCartridgesFromRealFolder(program.Path);
        }
    }

    private void LoadCartridgesFromRealFolder(string folderPath)
    {
        Buttons.Clear();
        _selectedPads.Clear();

        if (!Directory.Exists(folderPath))
        {
            HasButtons = false;
            return;
        }

        var files = Directory.EnumerateFiles(folderPath)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var number = 1;
        foreach (var file in files)
        {
            var mediaItem = new MediaItem
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Path = file,
                MediaType = MediaType.Audio,
                FileSize = SafeFileSize(file)
            };

            var pad = new AudioPad
            {
                Name = mediaItem.Name,
                Media = mediaItem,
                Color = PadColorFor(Name),
                Category = CurrentProgramName
            };

            var padViewModel = new VtButtonViewModel(number, pad, PlayPad, OnCartridgeOverrideChanged, ToggleSelection);
            ApplyOverrideIfAny(padViewModel);
            Buttons.Add(padViewModel);
            number++;

            _ = LoadDurationAsync(padViewModel, file);
        }

        HasButtons = Buttons.Count > 0;
    }

    /// <summary>
    /// Lê a duração real do arquivo em segundo plano (ver Engine.AudioEngine.GetDurationAsync) e
    /// aplica no cartucho quando terminar — cada cartucho é independente, então um arquivo
    /// corrompido/lento não atrasa a duração dos outros. Captura qualquer exceção de propósito: o
    /// tipo exato de falha varia com o motivo (arquivo corrompido, formato não suportado, etc.), e
    /// nenhuma delas pode derrubar o carregamento do banco inteiro.
    /// </summary>
    private async Task LoadDurationAsync(VtButtonViewModel pad, string filePath)
    {
        TimeSpan? duration;
        try
        {
            duration = await AudioEngine.GetDurationAsync(filePath);
        }
        catch (Exception)
        {
            duration = null;
        }

        Application.Current?.Dispatcher.Invoke(() => pad.SetDuration(duration));
    }

    private void LoadSampleCartridges(string programName)
    {
        Buttons.Clear();
        _selectedPads.Clear();

        var cartridges = SampleProgramsFor(Name).FirstOrDefault(p => p.Program == programName).Cartridges
            ?? Array.Empty<(string Name, string Color)>();

        var number = 1;
        foreach (var (label, color) in cartridges)
        {
            var mediaItem = new MediaItem { Name = label, Path = string.Empty, MediaType = MediaType.Audio };
            var pad = new AudioPad { Name = label, Media = mediaItem, Color = color, Category = programName };
            Buttons.Add(new VtButtonViewModel(number, pad, PlayPad, OnCartridgeOverrideChanged, ToggleSelection));
            number++;
        }

        HasButtons = Buttons.Count > 0;
    }

    private void LoadSamplePrograms()
    {
        foreach (var (programName, _) in SampleProgramsFor(Name))
        {
            Programs.Add(new CartucheiraProgramViewModel(programName, path: null, OpenProgram));
        }
    }

    /// <summary>
    /// Sprint Crossfade v2 (2026-08-11): diferente da versão original (Stop + Load + Play, corte
    /// seco sempre), aqui NÃO se chama <see cref="Stop"/> antes de trocar de cartucho — isso mataria
    /// o cartucho anterior na hora, impossibilitando qualquer mistura. <see cref="AudioEngine.Play"/>
    /// é chamado uma vez só, com o cartucho anterior (se houver) ainda tocando; é o próprio Engine
    /// que decide, por conta própria, se mistura os dois (crossfade configurado,
    /// <see cref="SetCrossfadeDuration"/>) ou corta seco (0ms, padrão) — mesmo resultado de sempre
    /// quando não há crossfade configurado.
    /// </summary>
    private void PlayPad(VtButtonViewModel pad)
    {
        if (ReferenceEquals(NowPlaying, pad))
        {
            Stop();
            _onStatus($"\"{pad.Name}\" parado.");
            return;
        }

        if (string.IsNullOrWhiteSpace(pad.FilePath))
        {
            _onStatus($"\"{pad.Name}\" não tem arquivo associado.");
            return;
        }

        var previousPad = NowPlaying;
        _audioEngine.Play(pad.FilePath, pad.IsLoopEnabled);
        NowPlaying = pad;
        pad.IsPlaying = true;
        ClearPadUiOnly(previousPad);
        _onStatus($"\"{pad.Name}\" tocando ({Name}).");
    }

    /// <summary>
    /// Implementa a arquitetura §14.2 ("disparo por comando externo") de verdade — Sprint Comando
    /// Externo (2026-08-10). Chamado pelo <c>MainViewModel</c> tanto no atalho de teclado global
    /// quanto no <c>POST</c> HTTP daquela Cartucheira (os dois canais convergem aqui, mesmo efeito
    /// pros dois — ver <c>CartucheiraVideo.Infrastructure.GlobalHotkeyService</c>/
    /// <c>CartucheiraVideo.Api.LocalTriggerApiHost</c>). Regras confirmadas explicitamente pelo
    /// usuário antes desta sprint:
    /// <list type="bullet">
    /// <item>Nada selecionado → comando é ignorado, sem erro nenhum (§14.2: "o comando da mesa é
    /// ignorado").</item>
    /// <item>O cartucho "1" da fila de seleção (<see cref="VtButtonViewModel.SelectionNumber"/>) já
    /// está tocando quando chega um comando novo → interrompe na hora e toca o próximo da fila
    /// (decisão explícita do usuário pro ponto em aberto do §14.6 — "todo aperto faz algo").</item>
    /// <item>Senão (nada tocando, ou tocando algo que não é o "1" da fila) → toca o "1" da fila
    /// direto, interrompendo qualquer outra coisa que estivesse tocando (mesmo <see cref="PlayPad"/>
    /// que um clique manual já usa).</item>
    /// </list>
    /// Reaproveita <see cref="Stop"/>/<see cref="PlayPad"/>/<see cref="ClearNowPlaying"/> por inteiro
    /// — nenhuma gambiarra nova de fila: interromper o "1" atual já o remove da seleção e renumera
    /// quem sobrou (via <see cref="ClearNowPlaying"/>, igual a quando um cartucho termina sozinho),
    /// então "tocar o próximo" já é, literalmente, "tocar quem virou o novo '1' depois disso".
    /// </summary>
    public void TriggerExternalCommand()
    {
        if (_selectedPads.Count == 0)
        {
            _onStatus($"Comando externo recebido em \"{Name}\" — nenhum cartucho selecionado, ignorado.");
            return;
        }

        var target = _selectedPads[0];

        if (ReferenceEquals(NowPlaying, target))
        {
            // Correção pós-entrega, reintroduzida na Sprint Crossfade v2 (2026-08-11): antes
            // chamava Stop() direto aqui, o que sempre cortava seco e pulava o crossfade — F2/
            // teclado nunca misturava, só clique manual (bug real reportado pelo usuário).
            // ToggleSelection só tira da fila (sem tocar no motor de áudio); quem decide
            // crossfade-ou-corte-seco pro próximo da fila é sempre o PlayPad logo abaixo, o mesmo
            // caminho que um clique manual já usa.
            ToggleSelection(target);

            if (_selectedPads.Count == 0)
            {
                Stop();
                _onStatus($"Comando externo recebido em \"{Name}\" — \"{target.Name}\" interrompido, nada mais selecionado.");
                return;
            }

            target = _selectedPads[0];
        }

        PlayPad(target);
    }

    /// <summary>
    /// Comando externo de "DESLIGAR" — Sprint Comando Externo, correção pós-entrega (2026-08-10):
    /// canal novo (mesa de áudio via UDP, protocolo confirmado pelo usuário — colunas "Trigger
    /// ON"/"Trigger OFF" da tela da mesa) que precisa de um par Ligar/Desligar, diferente do
    /// teclado/Stream Deck (só um botão, sempre "avança a fila" via <see cref="TriggerExternalCommand"/>).
    /// Só para o que estiver tocando agora neste banco (reaproveita <see cref="Stop"/> por inteiro,
    /// que já desmarca da seleção e renumera quem sobrou via <see cref="ClearNowPlaying"/>) — nunca
    /// toca nada novo. Sem efeito, honesto, se nada estiver tocando.
    /// </summary>
    public void StopExternalCommand()
    {
        if (NowPlaying is null)
        {
            _onStatus($"Comando externo de \"desligar\" recebido em \"{Name}\" — nada tocando, ignorado.");
            return;
        }

        var name = NowPlaying.Name;
        Stop();
        _onStatus($"Comando externo de \"desligar\" recebido em \"{Name}\" — \"{name}\" interrompido.");
    }

    private void Stop()
    {
        if (NowPlaying is null)
        {
            return;
        }

        _audioEngine.Stop();
        ClearNowPlaying();
    }

    private void OnPlaybackEnded() => ClearNowPlaying();

    /// <summary>
    /// Desliga o cartucho tocando agora (<see cref="VtButtonViewModel.IsPlaying"/> = falso,
    /// <see cref="NowPlaying"/> = nulo) — chamado tanto por <see cref="Stop"/> (parada manual, clique
    /// esquerdo de novo no que já toca, ou troca pra outro cartucho) quanto por
    /// <see cref="OnPlaybackEnded"/> (fim natural do áudio). Sem efeito se nada estiver tocando.
    ///
    /// Sprint Cartucheira 2, parte 2 - correção #2 (2026-08-10): também tira o cartucho da fila de
    /// seleção (se estiver nela) e renumera quem sobrou — pedido explícito do usuário ("a seleção
    /// retira qd o cartucho acaba de executar"; exemplo dado: 3 selecionados, 1 termina de tocar, o
    /// que era 2 vira 1 e o que era 3 vira 2). Antes disso a seleção só saía pelo clique esquerdo no
    /// próprio cartucho já selecionado — removido junto com a correção #2 do
    /// <see cref="VtButtonViewModel.PlayAudioCommand"/> (agora sempre toca/para, nunca desmarca).
    /// </summary>
    private void ClearNowPlaying()
    {
        var pad = NowPlaying;
        if (pad is null)
        {
            return;
        }

        NowPlaying = null;
        ClearPadUiOnly(pad);
    }

    /// <summary>
    /// Sprint Crossfade v2 (2026-08-11): parte visual/seleção de <see cref="ClearNowPlaying"/>,
    /// isolada pra ser reaproveitada por <see cref="PlayPad"/> ao trocar de cartucho — nesse caso
    /// NÃO se pode chamar <see cref="ClearNowPlaying"/> inteiro (ele mexeria em <see cref="NowPlaying"/>,
    /// que o PlayPad já está prestes a sobrescrever com o cartucho novo). Não toca no motor de áudio
    /// de propósito — quem manda no áudio nesse caso é sempre o <see cref="AudioEngine.Play"/> que o
    /// PlayPad já chamou antes.
    /// </summary>
    private void ClearPadUiOnly(VtButtonViewModel? pad)
    {
        if (pad is null)
        {
            return;
        }

        pad.IsPlaying = false;

        if (_selectedPads.Contains(pad))
        {
            ToggleSelection(pad);
        }
    }

    /// <summary>Cor por banco (não por categoria de cartucho individual, diferente dos dados fictícios antigos — sem metadata de categoria vinda de um scan de pasta) — reaproveita as cores já aprovadas em Theme.xaml (Brush.Pad.*), nenhuma cor nova.</summary>
    private static string PadColorFor(string bankName) => bankName == "Vinhetas" ? "#FF2D7DFF" : "#FF2DBE60";

    private static long SafeFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private void Rename()
    {
        var dialog = new RenameBankWindow(Name)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResultName))
        {
            Name = dialog.ResultName;
            _onStatus($"Banco renomeado para \"{Name}\".");
        }
    }

    /// <summary>
    /// Programas de exemplo do mockup v8 — os mesmos 16+14 cartuchos fictícios de sempre
    /// (nomes/cores idênticos ao antigo <c>MainViewModel.CreateSampleButtons1/2</c> da Sprint UI),
    /// só reorganizados em "programas" (por categoria de cor, que já era como eles eram agrupados
    /// visualmente) pra ilustrar a navegação por subpasta antes de uma pasta raiz real existir.
    /// </summary>
    private static IReadOnlyList<(string Program, (string Name, string Color)[] Cartridges)> SampleProgramsFor(string bankName) =>
        bankName == "Vinhetas" ? SampleProgramsVinhetas : SampleProgramsTrilhas;

    private static readonly (string Program, (string Name, string Color)[] Cartridges)[] SampleProgramsVinhetas =
    {
        ("Vinhetas", new[]
        {
            ("Vinheta Abertura", "#FF2D7DFF"), ("Vinheta Encerramento", "#FF2D7DFF"), ("Vinheta Pop", "#FF2D7DFF"),
            ("Vinheta Rock", "#FF2D7DFF"), ("Vinheta Flash", "#FF2D7DFF"), ("Vinheta Institucional", "#FF2D7DFF")
        }),
        ("Jingles", new[]
        {
            ("Jingle Entrada", "#FF8B5CF6"), ("Jingle Saída", "#FF8B5CF6"), ("Jingle Bloco 1", "#FF8B5CF6"), ("Jingle Bloco 2", "#FF8B5CF6")
        }),
        ("Transições", new[]
        {
            ("Transição Corte", "#FFE3B341"), ("Transição Suave", "#FFE3B341"), ("Transição Rápida", "#FFE3B341")
        }),
        ("Chamadas", new[]
        {
            ("Chamada Comercial", "#FFEC4899"), ("Chamada Próximo Bloco", "#FFEC4899"), ("Escalada Final", "#FFEC4899")
        })
    };

    private static readonly (string Program, (string Name, string Color)[] Cartridges)[] SampleProgramsTrilhas =
    {
        ("Impactos", new[]
        {
            ("Impacto", "#FFD84545"), ("Boom", "#FFD84545"), ("Aplauso", "#FFD84545"), ("Risada", "#FFD84545"), ("Alerta", "#FFD84545")
        }),
        ("Fundos Musicais", new[]
        {
            ("BG Piano", "#FF2DBE60"), ("BG Suspense", "#FF2DBE60"), ("BG Tensão", "#FF2DBE60"), ("Tique-taque", "#FF2DBE60")
        }),
        ("Efeitos", new[]
        {
            ("Sweep", "#FF06B6D4"), ("Whoosh", "#FF06B6D4"), ("Entrada", "#FF06B6D4"), ("Saída", "#FF06B6D4"), ("Clique", "#FF06B6D4")
        })
    };

    public void Dispose() => _audioEngine.Dispose();
}
