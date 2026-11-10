using System.Collections.Generic;

namespace CartucheiraVideo.Infrastructure.Livewire;

/// <summary>
/// Sprint Livewire-1 (2026-09-13): resultado de decodificar um datagrama de anúncio/controle Livewire
/// — porta 1:1 de <c>mesa::lw::Anuncio</c> (projeto "Mesa Console"). Ver
/// <see cref="LivewireAnnouncementCodec.Read"/>.
///
/// <see cref="AdvertisementType"/> 0 significa "não é um ADVT" — é o caso do comando de escrita WRIN
/// que o CONSOLE manda pela porta de controle do nó (não um anúncio publicado pelo nó): mesmo formato
/// de "frases" tipadas, mas sem o campo ADVT. Ver o teste "comando WRIN decodifica inteiro" portado em
/// <c>LivewireAnnouncementCodecTests</c> — foi capturado de um QOR real e é a origem do achado "Used
/// EW" documentado em <c>arquitetura/livewire-plano.md</c>.
/// </summary>
public sealed class LivewireAnnouncement
{
    public bool Ok { get; internal set; }

    /// <summary>Motivo da falha (nunca lança exceção por pacote malformado — decodificação honesta, ver comentário de classe de <see cref="LivewireAnnouncementCodec"/>).</summary>
    public string? Error { get; internal set; }

    public uint Counter { get; internal set; }

    /// <summary>0 = não é um anúncio (ex.: comando WRIN do console), 1 = lista de fontes (ADVT1), 2 = sinal de vida (ADVT2), 3 = estado de uso (ADVT3).</summary>
    public int AdvertisementType { get; internal set; }

    public ushort Hwid { get; internal set; }

    public uint Ip { get; internal set; }

    public ushort Udpc { get; internal set; }

    /// <summary>Quantidade de fontes que o campo NUMS anunciou — pode diferir de <see cref="Sources"/>.Count num pacote malformado (a decodificação nunca inventa/completa; ver o teste "NEST e os tamanhos de bloco tem de acompanhar o numero de fontes" na origem C++). -1 = campo NUMS ausente.</summary>
    public int SourceCount { get; internal set; } = -1;

    public string MachineName { get; internal set; } = string.Empty;

    /// <summary>Fontes efetivamente decodificadas (cada bloco S001, S002... com PSID &gt; 0).</summary>
    public IReadOnlyList<LivewireSourceAnnouncement> Sources { get; internal set; } = System.Array.Empty<LivewireSourceAnnouncement>();

    /// <summary>Quantos bytes do datagrama foram de fato interpretados — se sobrar byte antes do fim declarado dos blocos, algo não fechou certo (ver <see cref="LivewireAnnouncementCodec.Read"/>).</summary>
    public int BytesConsumed { get; internal set; }
}
