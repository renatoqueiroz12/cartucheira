using CartucheiraVideo.Configuration;
using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>Modo do diálogo de autenticação — decide qual campo aparece e o que <see cref="AdminAuthViewModel.TryConfirm"/> faz.</summary>
public enum AdminAuthMode
{
    /// <summary>Primeiro acesso — <c>AppSettingsSnapshot.AdminPasswordHash</c> ainda está vazio. Pede a senha nova + confirmação, nenhum hash existente pra comparar.</summary>
    SetPassword,

    /// <summary>Acessos seguintes — já existe hash salvo, precisa bater com a senha digitada.</summary>
    Login
}

/// <summary>
/// ViewModel do <see cref="Views.AdminAuthWindow"/> — Sprint Config Geral, parte 1 (2026-08-09).
/// Cobre os dois momentos do fluxo de admin (arquitetura: "Login e senha de admin... para acessar
/// o config"): definir a senha na primeira vez que "Configurações" é clicado, e pedir login nas
/// vezes seguintes. Não existe recuperação de senha nesta sprint — decisão deliberada de
/// simplicidade (documentada em status-implementacao.md): esquecer a senha exige apagar o
/// settings.json manualmente. Também não há troca de senha dentro do Settings ainda (só o
/// "Definir senha" de primeiro acesso) — limitação conhecida, sprint futura.
/// </summary>
public sealed class AdminAuthViewModel : ViewModelBase
{
    private readonly string? _expectedHash;
    private readonly string? _expectedSalt;

    private string _password = string.Empty;
    private string _confirmPassword = string.Empty;
    private string _errorMessage = string.Empty;

    public AdminAuthViewModel(AdminAuthMode mode, string? expectedHash = null, string? expectedSalt = null)
    {
        Mode = mode;
        _expectedHash = expectedHash;
        _expectedSalt = expectedSalt;
    }

    public AdminAuthMode Mode { get; }

    public bool IsSetPasswordMode => Mode == AdminAuthMode.SetPassword;

    public bool IsLoginMode => Mode == AdminAuthMode.Login;

    public string Title => IsSetPasswordMode ? "Definir senha de admin" : "Login de admin";

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public string ConfirmPassword
    {
        get => _confirmPassword;
        set => SetProperty(ref _confirmPassword, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>Preenchido só em <see cref="AdminAuthMode.SetPassword"/>, só depois de <see cref="TryConfirm"/> devolver true — o hash novo, pronto pra ser persistido pelo chamador.</summary>
    public string? HashResult { get; private set; }

    /// <summary>Sal novo correspondente a <see cref="HashResult"/> — ver comentário acima.</summary>
    public string? SaltResult { get; private set; }

    /// <summary>
    /// Valida e, se tudo certo, prepara o resultado (SetPassword) ou confirma a senha (Login).
    /// Devolve <see langword="false"/> e preenche <see cref="ErrorMessage"/> em caso de falha — o
    /// code-behind da janela mantém a janela aberta pra nova tentativa nesse caso, só fecha
    /// (DialogResult=true) quando isto devolver true.
    /// </summary>
    public bool TryConfirm()
    {
        ErrorMessage = string.Empty;

        if (IsSetPasswordMode)
        {
            if (string.IsNullOrWhiteSpace(Password) || Password.Length < 4)
            {
                ErrorMessage = "A senha precisa ter pelo menos 4 caracteres.";
                return false;
            }

            if (Password != ConfirmPassword)
            {
                ErrorMessage = "As senhas não coincidem.";
                ConfirmPassword = string.Empty;
                return false;
            }

            var (hash, salt) = PasswordHasher.Hash(Password);
            HashResult = hash;
            SaltResult = salt;
            return true;
        }

        // Login: hash/salt esperados sempre deveriam existir aqui (MainViewModel só entra em modo
        // Login quando AdminPasswordHash já está preenchido) — string vazia cai no branch "senha
        // incorreta" do PasswordHasher.Verify, nunca lança.
        if (PasswordHasher.Verify(Password, _expectedHash ?? string.Empty, _expectedSalt ?? string.Empty))
        {
            return true;
        }

        ErrorMessage = "Senha incorreta.";
        Password = string.Empty;
        return false;
    }
}
