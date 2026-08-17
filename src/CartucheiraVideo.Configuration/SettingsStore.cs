using System.Text.Json;

namespace CartucheiraVideo.Configuration;

/// <summary>
/// Persistência real em disco (<c>%AppData%\CartucheiraVideo\settings.json</c>) — Sprint Config
/// Geral, parte 1 (2026-08-09), primeira vez que qualquer configuração do programa sobrevive a
/// fechar e abrir de novo. <see cref="Load"/> nunca lança exceção: arquivo ausente ou corrompido
/// cai num <see cref="AppSettingsSnapshot"/> vazio (equivalente a "primeira execução"), em vez de
/// travar o programa. <see cref="Save"/> também não lança — devolve <see langword="false"/> se não
/// conseguir escrever (disco cheio, sem permissão, etc.), pro chamador avisar o operador sem
/// derrubar o programa no meio de uma operação ao vivo.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;

    /// <summary>Caminho padrão — usado pelo composition root (App.xaml.cs/MainViewModel) em produção.</summary>
    public SettingsStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CartucheiraVideo", "settings.json"))
    {
    }

    /// <summary>Construtor com caminho explícito — existe pra permitir apontar pra um arquivo diferente (ex.: diagnóstico), sem depender só do padrão do %AppData%.</summary>
    public SettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public AppSettingsSnapshot Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new AppSettingsSnapshot();
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<AppSettingsSnapshot>(json) ?? new AppSettingsSnapshot();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Arquivo corrompido, sem permissao de leitura, etc. — cai num padrao vazio honesto em
            // vez de travar o programa; o pior caso e perder a configuracao salva, nunca um crash.
            return new AppSettingsSnapshot();
        }
    }

    /// <summary>Grava o snapshot no disco. Devolve <see langword="false"/> (sem lançar) se não conseguir — chamador decide como avisar o operador.</summary>
    public bool Save(AppSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(_filePath, json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
