using System;
using System.Collections.Generic;
using System.Text;

namespace CartucheiraVideo.Infrastructure.Livewire;

/// <summary>
/// Sprint Livewire-1 (2026-09-13), primeira etapa do suporte Livewire nativo (Axia/Telos) na
/// Cartucheira — pedido do usuário: "a ideia é que a cartucheira gere seus Livewire", usando como
/// base o conhecimento já validado contra hardware real no projeto "Mesa Console" (áudio virtual em
/// C++17/JUCE do próprio usuário). Ver <c>arquitetura/livewire-plano.md</c> no projeto pro roteiro
/// completo de sprints e o raciocínio de onde este código vive.
///
/// ESTA CLASSE É UM PORT, NÃO CÓDIGO NOVO — a lógica (formato de bytes, nomes de campo, valores
/// "mágicos" como 0x3f5/0x54/0x2d/0x0d) vem inteira de <c>AnuncioLw.h</c> (arquivo cedido pelo
/// usuário, escrito pela Mesa Console a partir de captura de pacote real contra um driver Axia — o
/// formato NÃO é publicado pelo fabricante). Deliberadamente NÃO reescrito/"melhorado": qualquer
/// desvio do byte exato arrisca o console não reconhecer o anúncio, e não há como testar isso de
/// verdade neste ambiente (sem hardware Livewire por perto) — só a fidelidade ao original já
/// comprovado dá alguma confiança antes do primeiro teste com um nó real.
///
/// Por que existe puro, sem nenhum socket: mesmo racional documentado no <c>AnuncioLw.h</c> original —
/// "enquanto os bytes do anúncio nasciam dentro do socket, nenhum teste os via". Aqui o pacote é só um
/// <c>byte[]</c> que uma bateria de testes confere (ver o harness portado de <c>testes-anuncio.cpp</c>,
/// os mesmos 24 casos, rodado neste sandbox antes da entrega — mas os arquivos de teste em si não
/// fazem parte do pacote entregue, mesmo padrão já usado neste projeto pro <c>GlobalHotkeyService</c>/
/// <c>UdpTriggerListener</c>: harness descartável, não uma 8ª pasta/projeto permanente).
///
/// Formato do datagrama (decifrado por captura, não publicado pelo fabricante):
/// <code>
///     03 00 02 07          início fixo
///     4 bytes              contador, sobe a cada mensagem
///     8 bytes              zeros
///     frases: [4 letras][tipo][valor]
/// </code>
/// Tipos: 0x00 e 0x07 levam 1 byte; 0x08 leva 2; 0x01 leva 4; 0x09 leva 8; 0x03 leva 2 bytes de
/// tamanho e depois o texto; 0x06 abre um bloco aninhado com 2 bytes de tamanho.
///
/// Regras descobertas (documentadas no <see cref="LivewireIdentity"/> e aqui de novo, porque
/// determinam campos concretos deste codec): NEST conta os itens do nível de cima depois dele
/// próprio; INDI conta os itens do bloco; FSID = 0xEFC00000 + canal; BSID = 0xEFC10000 + canal.
///
/// O achado mais caro da Mesa Console — o mecanismo BUSY — está todo neste arquivo: o console
/// escreve o estado de uso de uma fonte (comando "WRIN", decodificado por <see cref="Read"/> como um
/// <see cref="LivewireAnnouncement"/> com <see cref="LivewireAnnouncement.AdvertisementType"/> == 0)
/// na porta de controle do nó; se o nó não ecoar EXATAMENTE esse valor de volta no próximo ADVT3
/// (<see cref="BuildUsage"/>), o console mostra a fonte como "Used EW" (áudio funciona, rótulo
/// confuso no operador). A sprint que liga isto a sockets de verdade (Livewire-5, ver o roteiro,
/// reordenado em 2026-09-13 pra dar prioridade ao anúncio) só
/// precisa: decodificar o WRIN recebido com <see cref="Read"/>, guardar
/// <see cref="LivewireAnnouncement.Sources"/>[0].Usage por canal, e passar esse mesmo valor de volta
/// pro próximo <see cref="BuildUsage"/> daquele canal.
/// </summary>
public static class LivewireAnnouncementCodec
{
    private static readonly byte[] FixedHeader = { 0x03, 0x00, 0x02, 0x07 };

    // ------------------------------------------------------------------ escrita

    /// <summary>ADVT 1 — a lista completa das fontes, com nome e endereços de stream. Porta de <c>montaLista</c>.</summary>
    public static byte[] BuildSourceList(LivewireIdentity identity, IReadOnlyList<LivewireSourceAnnouncement> sources, uint sequence)
    {
        var v = new List<byte>();
        WriteHeader(v, sequence);

        WriteTag(v, "NEST"); WriteU8(v, 0x00); WriteU8(v, (byte)(3 + sources.Count));
        WriteTag(v, "PVER"); WriteU8(v, 0x08); WriteU16(v, 2);
        WriteTag(v, "ADVT"); WriteU8(v, 0x07); WriteU8(v, 0x01);
        WriteTag(v, "TERM"); WriteU8(v, 0x06); WriteU16(v, 0x54);
        WriteTag(v, "INDI"); WriteU8(v, 0x00); WriteU8(v, 0x06);
        WriteTag(v, "ADVV"); WriteU8(v, 0x01); WriteU32(v, 0x3f5);
        WriteTag(v, "HWID"); WriteU8(v, 0x08); WriteU16(v, identity.Hwid);
        WriteTag(v, "INIP"); WriteU8(v, 0x01); WriteU32(v, identity.Ip);
        WriteTag(v, "UDPC"); WriteU8(v, 0x08); WriteU16(v, identity.Udpc);
        WriteTag(v, "NUMS"); WriteU8(v, 0x08); WriteU16(v, (ushort)sources.Count);
        WriteTag(v, "ATRN"); WriteU8(v, 0x03); WriteText(v, identity.MachineName, 32);

        for (var i = 0; i < sources.Count; i++)
        {
            var canal = (uint)sources[i].Channel;
            WriteTag(v, SourceLabel(i)); WriteU8(v, 0x06); WriteU16(v, 0x65);
            WriteTag(v, "INDI"); WriteU8(v, 0x00); WriteU8(v, 0x0b);
            WriteTag(v, "PSID"); WriteU8(v, 0x01); WriteU32(v, canal);
            WriteTag(v, "SHAB"); WriteU8(v, 0x07); WriteU8(v, 0x00);
            WriteTag(v, "FSID"); WriteU8(v, 0x01); WriteU32(v, 0xefc00000u + canal);
            WriteTag(v, "FAST"); WriteU8(v, 0x07); WriteU8(v, 0x02);
            WriteTag(v, "FASM"); WriteU8(v, 0x07); WriteU8(v, 0x01);
            WriteTag(v, "BSID"); WriteU8(v, 0x01); WriteU32(v, 0xefc10000u + canal);
            WriteTag(v, "BAST"); WriteU8(v, 0x07); WriteU8(v, 0x01);
            WriteTag(v, "BASM"); WriteU8(v, 0x07); WriteU8(v, 0x00);
            WriteTag(v, "LPID"); WriteU8(v, 0x01); WriteU32(v, canal);
            WriteTag(v, "STPL"); WriteU8(v, 0x07); WriteU8(v, 0x00);
            WriteTag(v, "PSNM"); WriteU8(v, 0x03); WriteText(v, sources[i].Name, 16);
        }

        return v.ToArray();
    }

    /// <summary>ADVT 2 — sinal curto de vida: diz que o nó existe e quantas fontes tem. Porta de <c>montaVida</c>.</summary>
    public static byte[] BuildHeartbeat(LivewireIdentity identity, int sourceCount, uint sequence)
    {
        var v = new List<byte>();
        WriteHeader(v, sequence);

        WriteTag(v, "NEST"); WriteU8(v, 0x00); WriteU8(v, 0x03);
        WriteTag(v, "PVER"); WriteU8(v, 0x08); WriteU16(v, 2);
        WriteTag(v, "ADVT"); WriteU8(v, 0x07); WriteU8(v, 0x02);
        WriteTag(v, "TERM"); WriteU8(v, 0x06); WriteU16(v, 0x2d);
        WriteTag(v, "INDI"); WriteU8(v, 0x00); WriteU8(v, 0x05);
        WriteTag(v, "ADVV"); WriteU8(v, 0x01); WriteU32(v, 0x15);
        WriteTag(v, "HWID"); WriteU8(v, 0x08); WriteU16(v, identity.Hwid);
        WriteTag(v, "INIP"); WriteU8(v, 0x01); WriteU32(v, identity.Ip);
        WriteTag(v, "UDPC"); WriteU8(v, 0x08); WriteU16(v, identity.Udpc);
        WriteTag(v, "NUMS"); WriteU8(v, 0x08); WriteU16(v, (ushort)sourceCount);

        return v.ToArray();
    }

    /// <summary>ADVT 3 — estado de USO (BUSY) de cada fonte. Porta de <c>montaUso</c> — ver o mecanismo BUSY no comentário de classe.</summary>
    public static byte[] BuildUsage(LivewireIdentity identity, IReadOnlyList<LivewireSourceAnnouncement> sources, uint sequence)
    {
        var v = new List<byte>();
        WriteHeader(v, sequence);

        // 3 do nível de cima (PVER, ADVT, TERM) + as fontes + as duas marcas de fim
        WriteTag(v, "NEST"); WriteU8(v, 0x00); WriteU8(v, (byte)(5 + sources.Count));
        WriteTag(v, "PVER"); WriteU8(v, 0x08); WriteU16(v, 2);
        WriteTag(v, "ADVT"); WriteU8(v, 0x07); WriteU8(v, 0x03);
        WriteTag(v, "TERM"); WriteU8(v, 0x06); WriteU16(v, 0x0d);
        WriteTag(v, "INDI"); WriteU8(v, 0x00); WriteU8(v, 0x01);
        WriteTag(v, "HWID"); WriteU8(v, 0x08); WriteU16(v, identity.Hwid);

        for (var i = 0; i < sources.Count; i++)
        {
            WriteTag(v, SourceLabel(i)); WriteU8(v, 0x06); WriteU16(v, 0x1c);
            WriteTag(v, "INDI"); WriteU8(v, 0x00); WriteU8(v, 0x02);
            WriteTag(v, "PSID"); WriteU8(v, 0x01); WriteU32(v, (uint)sources[i].Channel);
            WriteTag(v, "BUSY"); WriteU8(v, 0x09);
            var usage = sources[i].Usage;
            for (var k = 7; k >= 0; k--)
            {
                v.Add((byte)((usage >> (k * 8)) & 0xff));
            }
        }

        // Marcas de fim capturadas do driver real — não são [tag ASCII][tipo][valor] legível, só
        // bytes fixos que o formato original também escreve; portados literalmente, sem tentar
        // reinterpretar o que significam (mesma decisão já tomada por quem escreveu o original).
        for (var k = 0; k < 4; k++) v.Add(0xff);
        WriteU8(v, 0x09); for (var k = 0; k < 8; k++) v.Add(0);
        v.Add(0xff); v.Add(0xff); v.Add(0xff); v.Add(0xfe);
        WriteU8(v, 0x09); for (var k = 0; k < 8; k++) v.Add(0);

        return v.ToArray();
    }

    /// <summary>Nome do bloco de fonte: S001, S002... três dígitos (o que o driver usa) — porta de <c>marcaDeFonte</c>.</summary>
    private static string SourceLabel(int index)
    {
        var n = (uint)((index + 1) % 1000);
        return "S" + (n / 100 % 10) + (n / 10 % 10) + (n % 10);
    }

    private static void WriteHeader(List<byte> v, uint sequence)
    {
        v.AddRange(FixedHeader);
        WriteU32(v, sequence);
        for (var i = 0; i < 8; i++)
        {
            v.Add(0);
        }
    }

    private static void WriteTag(List<byte> v, string fourLetters)
    {
        for (var i = 0; i < 4; i++)
        {
            v.Add((byte)fourLetters[i]);
        }
    }

    private static void WriteU8(List<byte> v, byte value) => v.Add(value);

    private static void WriteU16(List<byte> v, ushort value)
    {
        v.Add((byte)(value >> 8));
        v.Add((byte)(value & 0xff));
    }

    private static void WriteU32(List<byte> v, uint value)
    {
        v.Add((byte)(value >> 24));
        v.Add((byte)(value >> 16));
        v.Add((byte)(value >> 8));
        v.Add((byte)value);
    }

    /// <summary>
    /// Texto de tamanho fixo — porta de <c>poeTexto</c>. Guarda sempre o último byte pro zero: um nome
    /// que enche o campo inteiro deixaria o leitor do outro lado sem onde parar.
    /// </summary>
    private static void WriteText(List<byte> v, string text, int size)
    {
        WriteU16(v, (ushort)size);
        var fits = size - 1;
        for (var i = 0; i < size; i++)
        {
            byte b = 0;
            if (i < fits && i < text.Length)
            {
                b = (byte)text[i];
            }

            v.Add(b);
        }
    }

    // ------------------------------------------------------------------ leitura

    /// <summary>Lê um datagrama de anúncio/controle inteiro. Nunca lança por pacote malformado — devolve <see cref="LivewireAnnouncement.Ok"/> falso com o motivo em <see cref="LivewireAnnouncement.Error"/>.</summary>
    public static LivewireAnnouncement Read(byte[] data) => Read(data, data.Length);

    /// <summary>Sobrecarga usada pelos testes de pacote truncado — decodifica só os primeiros <paramref name="length"/> bytes de <paramref name="data"/>, mesmo o array tendo mais.</summary>
    public static LivewireAnnouncement Read(byte[] data, int length)
    {
        var result = new LivewireAnnouncement();

        if (data.Length == 0 || length < 16)
        {
            result.Error = "pacote curto demais";
            return result;
        }

        if (!(data[0] == 0x03 && data[1] == 0x00 && data[2] == 0x02 && data[3] == 0x07))
        {
            result.Error = "cabecalho inesperado";
            return result;
        }

        result.Counter = (uint)ReadBigEndian(data, 4, 4);

        var state = new ReadState { Data = data, Pos = 16 };
        var ctx = new ReadContext(result);

        Walk(state, length, 0, ctx);
        ctx.Commit();

        result.Sources = ctx.Sources;
        result.BytesConsumed = state.Pos;

        if (state.Failed)
        {
            result.Error = state.Error;
            result.Ok = false;
            return result;
        }

        result.Ok = true;
        return result;
    }

    /// <summary>Estado mutável do cursor de leitura — porta de <c>detalhe::Cursor</c>.</summary>
    private sealed class ReadState
    {
        public byte[] Data = Array.Empty<byte>();
        public int Pos;
        public bool Failed;
        public string? Error;

        public void Fail(string error)
        {
            if (!Failed)
            {
                Failed = true;
                Error = error;
            }
        }
    }

    /// <summary>Acumula o resultado enquanto <see cref="Walk"/> percorre o pacote — porta do par (lambda "guarda" + variáveis locais) do <c>le()</c> original.</summary>
    private sealed class ReadContext
    {
        public readonly LivewireAnnouncement Result;
        public readonly List<LivewireSourceAnnouncement> Sources = new();
        public LivewireSourceAnnouncement Current = new();
        public bool InsideSource;

        public ReadContext(LivewireAnnouncement result)
        {
            Result = result;
        }

        /// <summary>Fecha a fonte em andamento (se houver e tiver canal válido) — porta da lambda <c>guarda</c>.</summary>
        public void Commit()
        {
            if (InsideSource && Current.Channel > 0)
            {
                Sources.Add(Current);
            }

            Current = new LivewireSourceAnnouncement();
            InsideSource = false;
        }
    }

    /// <summary>Percorre um nível de frases [4 letras][tipo][valor], recursivo pra blocos aninhados (tipo 0x06) — porta de <c>detalhe::percorre</c>.</summary>
    private static void Walk(ReadState state, int end, int depth, ReadContext ctx)
    {
        if (depth > 4)
        {
            state.Fail("aninhamento fundo demais");
            return;
        }

        var data = state.Data;

        while (state.Pos < end && !state.Failed)
        {
            if (state.Pos + 5 > end)
            {
                state.Fail("frase cortada");
                return;
            }

            var name = Encoding.ASCII.GetString(data, state.Pos, 4);
            var type = data[state.Pos + 4];
            state.Pos += 5;

            int size = type switch
            {
                0x00 or 0x07 => 1,
                0x08 => 2,
                0x01 => 4,
                0x09 => 8,
                _ => -1,
            };

            if (size >= 0)
            {
                if (state.Pos + size > end)
                {
                    state.Fail($"valor cortado em {name}");
                    return;
                }

                HandleField(ctx, name, type, data, state.Pos, size, depth);
                state.Pos += size;
            }
            else if (type == 0x03 || type == 0x06)
            {
                if (state.Pos + 2 > end)
                {
                    state.Fail($"tamanho cortado em {name}");
                    return;
                }

                var n = (int)ReadBigEndian(data, state.Pos, 2);
                state.Pos += 2;

                if (state.Pos + n > end)
                {
                    state.Fail($"bloco maior que o pacote em {name}");
                    return;
                }

                if (type == 0x03)
                {
                    HandleField(ctx, name, type, data, state.Pos, n, depth);
                    state.Pos += n;
                }
                else
                {
                    HandleField(ctx, name, type, data, state.Pos, n, depth);
                    var upTo = state.Pos + n;
                    Walk(state, upTo, depth + 1, ctx);
                    if (state.Failed)
                    {
                        return;
                    }

                    state.Pos = upTo; // blindagem: o bloco manda no tamanho
                }
            }
            else
            {
                state.Fail($"tipo desconhecido 0x{type:x2} em {name}");
                return;
            }
        }
    }

    /// <summary>Interpreta um campo já delimitado por <see cref="Walk"/> — porta do corpo da lambda <c>aoAchar</c> dentro de <c>le()</c>.</summary>
    private static void HandleField(ReadContext ctx, string name, byte type, byte[] data, int offset, int length, int depth)
    {
        // Um bloco S### no nível de cima abre uma fonte; fechamos no seguinte (ou no fim do pacote,
        // via ReadContext.Commit() chamado depois de Walk retornar).
        if (type == 0x06 && depth == 0 && name.Length > 0 && name[0] == 'S')
        {
            ctx.Commit();
            ctx.InsideSource = true;
            return;
        }

        if (type == 0x06)
        {
            return;
        }

        switch (name)
        {
            case "ADVT":
                ctx.Result.AdvertisementType = (int)ReadBigEndian(data, offset, length);
                return;
            case "HWID":
                ctx.Result.Hwid = (ushort)ReadBigEndian(data, offset, length);
                return;
            case "INIP":
                ctx.Result.Ip = (uint)ReadBigEndian(data, offset, length);
                return;
            case "UDPC":
                ctx.Result.Udpc = (ushort)ReadBigEndian(data, offset, length);
                return;
            case "NUMS":
                ctx.Result.SourceCount = (int)ReadBigEndian(data, offset, length);
                return;
            case "ATRN" when type == 0x03:
                ctx.Result.MachineName = TrimAtNul(data, offset, length);
                return;
        }

        if (ctx.InsideSource && name == "PSID")
        {
            ctx.Current.Channel = (int)ReadBigEndian(data, offset, length);
        }
        else if (ctx.InsideSource && name == "BUSY" && length == 8)
        {
            ctx.Current.Usage = ReadBigEndian(data, offset, length);
        }
        else if (ctx.InsideSource && name == "PSNM" && type == 0x03)
        {
            ctx.Current.Name = TrimAtNul(data, offset, length);
        }
    }

    private static ulong ReadBigEndian(byte[] data, int offset, int count)
    {
        ulong x = 0;
        for (var k = 0; k < count; k++)
        {
            x = (x << 8) | data[offset + k];
        }

        return x;
    }

    private static string TrimAtNul(byte[] data, int offset, int length)
    {
        var len = 0;
        while (len < length && data[offset + len] != 0)
        {
            len++;
        }

        return Encoding.ASCII.GetString(data, offset, len);
    }

    // -------------------------------------------------------------- identidade

    /// <summary>
    /// HWID estável a partir de um texto (ex.: nome da máquina) — porta de <c>hwidDeTexto</c>. FNV-1a
    /// de 32 bits, dobrado pra 16. Precisa de três coisas (ver comentário de <see cref="LivewireIdentity"/>):
    /// não mudar entre reinícios, não sair do IP, e evitar 0x0000/0xffff (parecem reservados).
    /// </summary>
    public static ushort ComputeHwidFromText(string text)
    {
        var h = 2166136261u;
        foreach (var ch in text)
        {
            h ^= (byte)ch;
            h *= 16777619u;
        }

        var curto = (ushort)((h ^ (h >> 16)) & 0xffffu);
        if (curto == 0x0000)
        {
            curto = 0x4d45; // "ME"
        }

        if (curto == 0xffff)
        {
            curto = 0x5341; // "SA"
        }

        return curto;
    }

    /// <summary>Sobe de um em um até sair da lista de ocupados — porta de <c>hwidLivre</c>. HWIDs vistos na rede vêm do ouvinte de anúncios (sprint futura); escolher um livre é melhor que sortear.</summary>
    public static ushort FindFreeHwid(ushort desired, IReadOnlyCollection<ushort> occupied)
    {
        var x = desired;
        for (var attempt = 0; attempt < 4096; attempt++)
        {
            if (x != 0x0000 && x != 0xffff && !occupied.Contains(x))
            {
                return x;
            }

            x = (ushort)((x + 1) & 0xffff);
        }

        return desired;
    }
}
