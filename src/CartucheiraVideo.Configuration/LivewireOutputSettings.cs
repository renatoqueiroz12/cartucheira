namespace CartucheiraVideo.Configuration;

/// <summary>
/// Sprint Livewire-2 (2026-09-13): uma "Saída Livewire" criada pelo usuário — nome + número de canal.
/// Uma vez criada, passa a aparecer no MESMO seletor de dispositivo de saída de áudio já usado pelas
/// Cartucheiras (<see cref="CartridgeSettingsSnapshot.VinhetasAudioDeviceId"/>/
/// <see cref="CartridgeSettingsSnapshot.TrilhasAudioDeviceId"/>) — pedido literal do usuário: "crio o
/// output e aí posso selecionar esses criados nos decks e nas cartucheira" (Deck fica de fora desta
/// sprint, ver <c>arquitetura/livewire-plano.md</c> no projeto).
///
/// Identidade do NÓ Livewire (HWID/nome de máquina/porta de controle) é única por instalação, não por
/// Saída — entra na Sprint Livewire-3 (anúncio, ver <c>CartucheiraVideo.Infrastructure.Livewire.LivewireAdvertiser</c>);
/// esta classe cobre só o que a transmissão de verdade (Sprint Livewire-2) precisa: canal e nome de
/// exibição — ambos reaproveitados pelo anúncio via <c>LivewireSourceAnnouncement</c>.
/// </summary>
public sealed class LivewireOutputSettings
{
    /// <summary>
    /// Identificador estável desta Saída — usado no valor prefixado (<c>"livewire:{Id}"</c>, ver
    /// <c>CartucheiraVideo.Engine.AudioEngine.ExternalOutputPrefix</c>) gravado em campos como
    /// <see cref="CartridgeSettingsSnapshot.VinhetasAudioDeviceId"/>. Gerado uma vez na criação
    /// (<c>Guid.NewGuid</c>), nunca reaproveitado — mesmo se a Saída for renomeada depois.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Nome exibido no seletor de dispositivo de áudio, e desde a Sprint Livewire-3 também no campo PSNM do anúncio Livewire (<c>CartucheiraVideo.Infrastructure.Livewire.LivewireAdvertiser</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Número do canal Livewire (1..32767) — determina o endereço multicast (239.192.y.z), ver <c>CartucheiraVideo.Infrastructure.Livewire.LivewireRtpFraming.GetMulticastAddress</c>.</summary>
    public int Channel { get; set; }
}
