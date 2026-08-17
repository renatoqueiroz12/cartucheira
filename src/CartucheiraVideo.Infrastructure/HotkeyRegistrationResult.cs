namespace CartucheiraVideo.Infrastructure;

/// <summary>
/// Resultado de <see cref="GlobalHotkeyService.Register"/> — honesto de propósito: um atalho global
/// pode falhar em runtime por um motivo fora do controle deste programa (outro programa já registrou
/// a mesma tecla no sistema inteiro, ex.: <c>ERROR_HOTKEY_ALREADY_REGISTERED</c>) — quem chama
/// precisa saber disso pra avisar o operador em vez de fingir que o atalho está ativo (regra do
/// projeto: nenhuma opção de Configurações finge que funciona quando não funciona).
/// </summary>
public sealed class HotkeyRegistrationResult
{
    private HotkeyRegistrationResult(bool succeeded, int? id, string? errorMessage)
    {
        Succeeded = succeeded;
        Id = id;
        ErrorMessage = errorMessage;
    }

    /// <summary>Verdadeiro se o Win32 confirmou o registro do atalho (RegisterHotKey retornou sucesso).</summary>
    public bool Succeeded { get; }

    /// <summary>Identificador do atalho registrado — só presente quando <see cref="Succeeded"/> é verdadeiro. Necessário pra desregistrar depois (<see cref="GlobalHotkeyService.Unregister"/>).</summary>
    public int? Id { get; }

    /// <summary>Mensagem legível do motivo da falha — só presente quando <see cref="Succeeded"/> é falso.</summary>
    public string? ErrorMessage { get; }

    public static HotkeyRegistrationResult Success(int id) => new(true, id, null);

    public static HotkeyRegistrationResult Failure(string errorMessage) => new(false, null, errorMessage);
}
