using CartucheiraVideo.Core;
using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// Uma linha da aba Automacao do DeckSettingsPanel (On Play ou On End, arquitetura §10) — envolve
/// um <see cref="AutomationAction"/> real do Core.Deck.AutomationActions (nao um dado fictício:
/// some se removido, sobrevive no Deck se Salvar for clicado). O Trigger (OnPlay/OnEnd) é fixado
/// na criação — cada linha pertence a uma das duas listas (OnPlayActions/OnEndActions do
/// DeckSettingsViewModel), nunca muda de lista.
/// </summary>
public sealed class AutomationActionViewModel : ViewModelBase
{
    private OutputRouteType _destination;
    private TransitionType _transition;
    private int _transitionDurationMs;

    public AutomationActionViewModel(AutomationAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Model = action;
        Trigger = action.Trigger;
        _destination = action.Destination;
        _transition = action.Transition;
        _transitionDurationMs = action.TransitionDurationMs;
    }

    /// <summary>Objeto de dominio real por trás desta linha — Salvar no SettingsWindow escreve os valores atuais de volta aqui antes de aplicar no Deck.</summary>
    public AutomationAction Model { get; }

    public AutomationTrigger Trigger { get; }

    public OutputRouteType Destination
    {
        get => _destination;
        set
        {
            if (SetProperty(ref _destination, value))
            {
                OnPropertyChanged(nameof(IsCartucheiraDestination));
                OnPropertyChanged(nameof(IsTransitionEditable));
                OnPropertyChanged(nameof(IsDurationEditable));
            }
        }
    }

    /// <summary>
    /// Sprint Deck-pausa-Cartucheira v2 (2026-08-11): verdadeiro quando <see cref="Destination"/> é
    /// <see cref="OutputRouteType.Cartucheira"/> — não existe "transição" nesse caso (Corte/Fade não
    /// fazem sentido pra pausar/retomar áudio, diferente de cortar/fundir entre destinos de vídeo),
    /// então <see cref="Transition"/> fica desabilitado na tela quando este destino é escolhido.
    /// <see cref="TransitionDurationMs"/> continua editável (ver <see cref="IsDurationEditable"/>),
    /// só que com um significado diferente aqui — Sprint fade ajustável (2026-08-11, mesmo dia):
    /// vira o tempo (ms) do fade da pausa/retomada em si, consumido de verdade por
    /// <c>MainViewModel.PauseCartucheiraFor</c>/<c>ResumeCartucheiraFor</c> → <see cref="Engine.AudioEngine.Pause"/>/
    /// <see cref="Engine.AudioEngine.Resume"/> (zero = corte seco, o padrão de sempre).
    /// </summary>
    public bool IsCartucheiraDestination => Destination == OutputRouteType.Cartucheira;

    /// <summary>Falso quando o Destino é a Cartucheira (ver <see cref="IsCartucheiraDestination"/>) — controla se o ComboBox de Transição fica habilitado na tela.</summary>
    public bool IsTransitionEditable => !IsCartucheiraDestination;

    public TransitionType Transition
    {
        get => _transition;
        set
        {
            if (SetProperty(ref _transition, value))
            {
                OnPropertyChanged(nameof(IsDurationEditable));
            }
        }
    }

    /// <summary>
    /// Controla se o campo Tempo (ms) fica habilitado na tela. Dois casos: (1) Destino Cartucheira —
    /// sempre editável, é o tempo do fade de pausa/retomada em si (ver
    /// <see cref="IsCartucheiraDestination"/>); (2) Destino vMix/Resolume — só com Transição=Fade,
    /// já que Corte é instantâneo por definição (arquitetura §13.3).
    /// </summary>
    public bool IsDurationEditable => IsCartucheiraDestination || Transition == TransitionType.Fade;

    public int TransitionDurationMs
    {
        get => _transitionDurationMs;
        set => SetProperty(ref _transitionDurationMs, value);
    }

    /// <summary>Copia os valores atuais (editados na tela) de volta pro objeto de dominio — chamado ao Salvar.</summary>
    public void ApplyToModel()
    {
        Model.Destination = Destination;
        Model.Transition = IsTransitionEditable ? Transition : TransitionType.Cut;
        Model.TransitionDurationMs = IsDurationEditable ? TransitionDurationMs : 0;
    }
}
