using System;

namespace CartucheiraVideo.Infrastructure.Livewire;

/// <summary>
/// Sprint Livewire-1 (2026-09-13): matemática de framing RTP/L24 do Livewire — porta das partes SEM
/// SOCKET de <c>LivewireSender.h</c>/<c>LivewireReceiver.h</c> (projeto "Mesa Console", referência
/// C++/JUCE). Deliberadamente sem nenhum <c>Socket</c>/<c>UdpClient</c> aqui: quem transmite/recebe de
/// verdade (sprints Livewire-2/Livewire-6, ver <c>arquitetura/livewire-plano.md</c>) usa estes
/// helpers, mas eles próprios são só matemática pura — testável sem rede, sem hardware Livewire por
/// perto.
///
/// Um stream Livewire padrão carrega <see cref="FramesPerPacket"/> quadros por pacote (5 ms de
/// estéreo a 48 kHz), amostras de 24 bits big-endian intercaladas (L/R/L/R...), num pacote RTP de
/// payload type ajustável (96 por padrão — a faixa dinâmica reservada do RTP; a Axia pode recusar um
/// valor que não reconheça).
/// </summary>
public static class LivewireRtpFraming
{
    /// <summary>5 ms de áudio a 48 kHz — tamanho de pacote padrão Livewire (pacotes "live" usam 12, 0,25ms; não suportado por este helper ainda).</summary>
    public const int FramesPerPacket = 240;

    /// <summary>Cabeçalho RTP fixo (sem extensão, sem CSRC) — versão 2, conforme <see cref="WriteHeader"/>.</summary>
    public const int RtpHeaderSize = 12;

    /// <summary>2 canais x 3 bytes (24 bits) por quadro.</summary>
    public const int BytesPerFrame = 6;

    /// <summary>Porta UDP fixa de todo stream de áudio Livewire.</summary>
    public const int MulticastPort = 5004;

    /// <summary>Payload type RTP padrão anunciado pela Axia — ajustável em runtime por quem transmite (ver comentário de classe).</summary>
    public const byte DefaultPayloadType = 96;

    /// <summary>Endereço multicast a partir do número do canal Livewire: canal = y*256 + z, grupo 239.192.y.z. Ex.: canal 3101 = 239.192.12.29.</summary>
    public static string GetMulticastAddress(int channel)
    {
        var y = (channel >> 8) & 0xff;
        var z = channel & 0xff;
        return $"239.192.{y}.{z}";
    }

    /// <summary>Escreve os 12 bytes do cabeçalho RTP em <paramref name="destination"/> (precisa de pelo menos <see cref="RtpHeaderSize"/> bytes de espaço).</summary>
    public static void WriteHeader(Span<byte> destination, byte payloadType, ushort sequence, uint timestamp, uint ssrc)
    {
        destination[0] = 0x80; // versão 2, sem padding nem CSRC
        destination[1] = (byte)(payloadType & 0x7f);
        destination[2] = (byte)(sequence >> 8);
        destination[3] = (byte)(sequence & 0xff);
        WriteUInt32BigEndian(destination.Slice(4, 4), timestamp);
        WriteUInt32BigEndian(destination.Slice(8, 4), ssrc);
    }

    /// <summary>Codifica uma amostra float (-1..1) em 3 bytes big-endian com sinal. Estourar o formato produz um limite duro (clipping), nunca um valor incorreto/estalo maior.</summary>
    public static void WriteSample24(Span<byte> destination, float sample)
    {
        var clamped = sample > 1.0f ? 1.0f : (sample < -1.0f ? -1.0f : sample);
        var v = (int)(clamped * 8388607.0f);
        destination[0] = (byte)((v >> 16) & 0xff);
        destination[1] = (byte)((v >> 8) & 0xff);
        destination[2] = (byte)(v & 0xff);
    }

    /// <summary>Decodifica 3 bytes big-endian com sinal (L24) de volta pra float (-1..1).</summary>
    public static float ReadSample24(ReadOnlySpan<byte> source)
    {
        var v = (source[0] << 16) | (source[1] << 8) | source[2];
        if ((v & 0x800000) != 0)
        {
            v -= 0x1000000; // extensão de sinal
        }

        return v * (1.0f / 8388608.0f);
    }

    private static void WriteUInt32BigEndian(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }
}
