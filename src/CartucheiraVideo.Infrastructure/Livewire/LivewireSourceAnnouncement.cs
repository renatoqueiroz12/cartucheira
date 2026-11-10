namespace CartucheiraVideo.Infrastructure.Livewire;

/// <summary>
/// Sprint Livewire-1 (2026-09-13): uma fonte (canal) dentro de um anúncio Livewire — porta 1:1 de
/// <c>mesa::lw::FonteAnuncio</c> (projeto "Mesa Console", referência C++/JUCE cedida pelo usuário).
/// Usada tanto pra MONTAR um anúncio nosso (<see cref="LivewireAnnouncementCodec.BuildSourceList"/>/
/// <see cref="LivewireAnnouncementCodec.BuildUsage"/>) quanto pra representar uma fonte já decodificada
/// de um anúncio recebido (<see cref="LivewireAnnouncement.Sources"/>).
/// </summary>
public sealed class LivewireSourceAnnouncement
{
    public LivewireSourceAnnouncement()
    {
    }

    public LivewireSourceAnnouncement(int channel, string name, ulong usage = 0)
    {
        Channel = channel;
        Name = name;
        Usage = usage;
    }

    /// <summary>Número do canal Livewire (1..32767) — também determina o endereço multicast, ver <see cref="LivewireRtpFraming.GetMulticastAddress"/>.</summary>
    public int Channel { get; set; }

    /// <summary>Nome da fonte exibido no console (campo PSNM, corta em 15 caracteres + zero — ver <see cref="LivewireAnnouncementCodec"/>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Estado de uso (BUSY) da fonte — 8 bytes opacos. Zero = livre. Não inventamos o significado dos
    /// bytes: só ecoamos de volta EXATAMENTE o valor que o console escreveu via WRIN (ver comentário de
    /// classe de <see cref="LivewireAnnouncementCodec"/> sobre o achado "Used EW" da Mesa Console).
    /// </summary>
    public ulong Usage { get; set; }
}
