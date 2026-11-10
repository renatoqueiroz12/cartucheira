namespace CartucheiraVideo.Infrastructure.Livewire;

/// <summary>
/// Sprint Livewire-1 (2026-09-13): identidade do NÓ Livewire que a Cartucheira vai anunciar — porta
/// 1:1 de <c>mesa::lw::Identidade</c> (projeto "Mesa Console", referência C++/JUCE cedida pelo
/// usuário).
///
/// Duas regras herdadas da Mesa Console, confirmadas por teste real contra hardware Axia, que
/// PRECISAM sobreviver ao port (ver <c>arquitetura/livewire-plano.md</c> no projeto pra o histórico
/// completo):
/// <list type="bullet">
/// <item><see cref="Hwid"/> NUNCA pode ser derivado do <see cref="Ip"/> — numa máquina que também
/// roda o IP-Driver da Axia (mesmo IP), dois nós com HWID igual ao IP colidem no console: o anúncio
/// mais recente apaga as fontes do outro. Ver <see cref="LivewireAnnouncementCodec.ComputeHwidFromText"/>.</item>
/// <item><see cref="Udpc"/> (porta de controle) tem que ser diferente de 4000 — essa é a porta que o
/// IP-Driver da Axia usa; dividir a porta com ele na mesma máquina faz o console falar com o vizinho
/// errado. O valor padrão aqui (4002) só é um ponto de partida seguro — quem liga isto a sockets de
/// verdade (sprint futura) deve deixar configurável.</item>
/// </list>
/// </summary>
public sealed class LivewireIdentity
{
    /// <summary>Identificador do nó (16 bits) — ver <see cref="LivewireAnnouncementCodec.ComputeHwidFromText"/>/<see cref="LivewireAnnouncementCodec.FindFreeHwid"/>.</summary>
    public ushort Hwid { get; set; }

    /// <summary>Endereço IPv4 do nó, como inteiro de 32 bits big-endian (ex.: 192.168.2.116 = (192&lt;&lt;24)|(168&lt;&lt;16)|(2&lt;&lt;8)|116).</summary>
    public uint Ip { get; set; }

    /// <summary>Porta UDP de controle que este nó promete escutar (WRIN/BUSY) — nunca 4000 (IP-Driver da Axia), ver comentário de classe.</summary>
    public ushort Udpc { get; set; } = 4002;

    /// <summary>Nome da máquina/nó exibido no console (campo ATRN, corta em 31 caracteres + zero).</summary>
    public string MachineName { get; set; } = string.Empty;
}
