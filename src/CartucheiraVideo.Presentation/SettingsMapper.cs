using System.Linq;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Core;
using CartucheiraVideo.Presentation.ViewModels;

namespace CartucheiraVideo.Presentation;

/// <summary>
/// Ponte entre o vocabulário decotado da <c>Configuration</c> (DTOs de persistência, enums do Core
/// guardados como string) e os objetos de domínio/ViewModel de verdade da Presentation — Sprint
/// Config Geral, parte 1 (2026-08-09). Só a Presentation conhece os dois lados (referencia Core E
/// Configuration), por isso este mapeamento mora aqui, não em nenhum dos dois projetos — mantém a
/// decisão de desacoplamento entre eles (mesmo padrão já usado por Engine/Ndi).
/// </summary>
internal static class SettingsMapper
{
    /// <summary>Aplica um snapshot persistido de volta num Deck real — chamado uma vez por Deck, na inicialização do MainViewModel, antes de qualquer outra coisa tocar nos Decks.</summary>
    public static void ApplyToDeck(DeckSettingsSnapshot snapshot, DeckViewModel deck)
    {
        deck.DomainDeck.NdiChannelName = string.IsNullOrWhiteSpace(snapshot.NdiChannelName) ? null : snapshot.NdiChannelName;

        // Sprint Áudio-Unificado (2026-09-15): AudioDeviceId agora vale pro Play E pro Preview (o
        // campo separado PreviewAudioDeviceId foi removido, ver Core.Deck.AudioDeviceId) — uma única
        // linha aplica os dois.
        deck.DomainDeck.AudioDeviceId = snapshot.AudioDeviceId;
        deck.SetAudioOutputDevice(snapshot.AudioDeviceId);

        deck.SendToVMix = snapshot.VMixEnabled;
        var vmix = deck.DomainDeck.OutputRoutes.First(r => r.OutputType == OutputRouteType.VMix);
        vmix.Configuration["Host"] = snapshot.VMixHost;
        vmix.Configuration["Port"] = snapshot.VMixPort;
        vmix.Configuration["Input"] = snapshot.VMixInput;

        deck.SendToResolume = snapshot.ResolumeEnabled;
        var resolume = deck.DomainDeck.OutputRoutes.First(r => r.OutputType == OutputRouteType.Resolume);
        resolume.Configuration["Host"] = snapshot.ResolumeHost;
        resolume.Configuration["Port"] = snapshot.ResolumePort;
        resolume.Configuration["Layer"] = snapshot.ResolumeLayer;

        deck.DomainDeck.UseBackgroundArt = snapshot.UseBackgroundArt;

        deck.DomainDeck.SendToScreens = snapshot.SendToScreens;
        deck.DomainDeck.ScreenOutputDeviceNames.Clear();
        foreach (var deviceName in snapshot.ScreenOutputDeviceNames)
        {
            deck.DomainDeck.ScreenOutputDeviceNames.Add(deviceName);
        }

        deck.DomainDeck.AutomationActions.Clear();
        foreach (var actionSnapshot in snapshot.OnPlayActions)
        {
            deck.DomainDeck.AutomationActions.Add(ToAction(actionSnapshot, AutomationTrigger.OnPlay));
        }

        foreach (var actionSnapshot in snapshot.OnEndActions)
        {
            deck.DomainDeck.AutomationActions.Add(ToAction(actionSnapshot, AutomationTrigger.OnEnd));
        }
    }

    /// <summary>Lê os valores atuais (já editados na tela) de um <see cref="DeckSettingsViewModel"/> e monta o DTO que vai pro disco — chamado ao Salvar.</summary>
    public static DeckSettingsSnapshot ToSnapshot(DeckSettingsViewModel vm) => new()
    {
        NdiChannelName = vm.NdiChannelName,
        AudioDeviceId = vm.AudioDeviceId,
        VMixEnabled = vm.VMixEnabled,
        VMixHost = vm.VMixHost,
        VMixPort = vm.VMixPort,
        VMixInput = vm.VMixInput,
        ResolumeEnabled = vm.ResolumeEnabled,
        ResolumeHost = vm.ResolumeHost,
        ResolumePort = vm.ResolumePort,
        ResolumeLayer = vm.ResolumeLayer,
        UseBackgroundArt = vm.UseBackgroundArt,
        SendToScreens = vm.SendToScreensEnabled,
        ScreenOutputDeviceNames = vm.AvailableScreens.Where(s => s.IsSelected).Select(s => s.DeviceName).ToList(),
        OnPlayActions = vm.OnPlayActions.Select(ToActionSnapshot).ToList(),
        OnEndActions = vm.OnEndActions.Select(ToActionSnapshot).ToList(),
        ExternalTriggerEnabled = vm.ExternalTriggerEnabled,
        UdpTriggerPlayCommand = vm.UdpTriggerPlayCommand?.Trim() ?? string.Empty,
        UdpTriggerPauseCommand = vm.UdpTriggerPauseCommand?.Trim() ?? string.Empty,
        OutboundUdpEnabled = vm.OutboundUdpEnabled,
        OutboundUdpHost = vm.OutboundUdpHost?.Trim() ?? string.Empty,
        OutboundUdpPort = vm.OutboundUdpPort,
        OutboundUdpOnPlayCommand = vm.OutboundUdpOnPlayCommand?.Trim() ?? string.Empty,
        OutboundUdpOnEndCommand = vm.OutboundUdpOnEndCommand?.Trim() ?? string.Empty,
        OutboundUdpOnPreviewCommand = vm.OutboundUdpOnPreviewCommand?.Trim() ?? string.Empty,
        OutboundUdpOnPreviewEndCommand = vm.OutboundUdpOnPreviewEndCommand?.Trim() ?? string.Empty
    };

    private static AutomationActionSnapshot ToActionSnapshot(AutomationActionViewModel action) => new()
    {
        Destination = action.Destination.ToString(),
        Transition = action.Transition.ToString(),
        TransitionDurationMs = action.TransitionDurationMs
    };

    /// <summary>
    /// Reconstrói um <see cref="AutomationAction"/> real a partir do DTO — <see cref="Enum.TryParse"/>
    /// com fallback honesto (Unknown/Cut) se o texto salvo não bater com nenhum valor do enum atual
    /// (ex.: settings.json de uma versão futura/antiga com nomes diferentes), em vez de lançar e
    /// travar a inicialização do programa por causa de um arquivo de configuração desatualizado.
    /// </summary>
    private static AutomationAction ToAction(AutomationActionSnapshot snapshot, AutomationTrigger trigger)
    {
        var destination = Enum.TryParse<OutputRouteType>(snapshot.Destination, out var parsedDestination)
            ? parsedDestination
            : OutputRouteType.Unknown;

        var transition = Enum.TryParse<TransitionType>(snapshot.Transition, out var parsedTransition)
            ? parsedTransition
            : TransitionType.Cut;

        return new AutomationAction
        {
            Trigger = trigger,
            Destination = destination,
            Transition = transition,
            TransitionDurationMs = snapshot.TransitionDurationMs
        };
    }
}
