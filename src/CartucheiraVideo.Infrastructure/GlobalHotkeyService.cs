using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace CartucheiraVideo.Infrastructure;

/// <summary>
/// Atalho de teclado GLOBAL (funciona mesmo com a janela da Cartucheira sem foco — o operador pode
/// estar com o vMix/Resolume/navegador em primeiro plano) — Sprint Comando Externo (2026-08-10),
/// arquitetura §14.2/§13 ("Infrastructure: Hotkey global (Win32)", previsto desde o início, nunca
/// implementado até esta sprint). Um único <see cref="GlobalHotkeyService"/> serve QUALQUER
/// quantidade de atalhos (<see cref="Register"/> retorna um id novo a cada chamada) — a Presentation
/// (MainViewModel) usa uma instância só pros dois atalhos (Vinhetas e Trilhas).
///
/// Implementação — janela "de mensagem apenas" (<c>HWND_MESSAGE</c>) criada por P/Invoke puro numa
/// thread dedicada, rodando seu próprio loop de mensagens Win32 (<c>GetMessage</c>/
/// <c>DispatchMessage</c>): é o requisito real do Win32 pra <c>RegisterHotKey</c> funcionar — a API
/// SÓ entrega <c>WM_HOTKEY</c> pra uma fila de mensagens de verdade, e não existe outro jeito de ter
/// uma fila de mensagens sem uma janela (ou "hWnd nulo", que amarra o hotkey à THREAD chamadora —
/// rejeitado aqui porque a thread chamadora seria a UI da WPF, e bloquear/acoplar o loop de mensagens
/// da UI a isto teria mais risco do que uma thread dedicada, isolada, só pra isto). Sem nenhuma
/// dependência de WPF/WinForms — só <c>user32.dll</c>/<c>kernel32.dll</c> (mesmo padrão do
/// CartucheiraVideo.Ndi: P/Invoke direto, sem wrapper de terceiro).
///
/// **Requisito documentado do Win32** (confirmado contra a documentação oficial da função antes de
/// escrever este código — mesmo rigor já aplicado nas pesquisas de NDI/vMix/Resolume):
/// <c>RegisterHotKey</c>/<c>UnregisterHotKey</c> só funcionam chamados pela MESMA thread que criou a
/// janela (hWnd) associada — "This function fails if you try to associate a hot key with a window
/// created by another thread." Por isso <see cref="Register"/>/<see cref="Unregister"/>, que podem
/// ser chamados da thread de UI da WPF, marcham a chamada real pra dentro da thread dona da janela
/// via <see cref="RunOnMessageThread"/> (uma fila de callbacks pendentes + uma mensagem Win32 própria,
/// <c>WM_APP_INVOKE</c>, que acorda o loop de mensagens pra executá-los) — nunca chamam
/// RegisterHotKey/UnregisterHotKey direto da thread chamadora.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_APP_INVOKE = 0x8000 + 1; // WM_APP + 1 — mensagem própria, sem significado nativo, só usada pra "há callbacks pendentes na fila, execute-os".
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint MOD_CONTROL = 0x0002;
    private const int CW_USEDEFAULT = unchecked((int)0x80000000);

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly WndProcDelegate _wndProcDelegate;
    private readonly string _className = $"CartucheiraVideoHotkeyWnd_{Guid.NewGuid():N}";
    private readonly ConcurrentQueue<Action> _pendingActions = new();
    private readonly Dictionary<int, Action> _callbacks = new();
    private readonly ManualResetEventSlim _windowReady = new(false);
    private readonly Thread _thread;
    private readonly object _registerLock = new();

    private IntPtr _hwnd = IntPtr.Zero;
    private int _nextId = 1;
    private volatile bool _disposed;

    public GlobalHotkeyService()
    {
        _wndProcDelegate = WndProc;
        _thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "CartucheiraVideo.GlobalHotkeyService"
        };
        _thread.Start();
        _windowReady.Wait();
    }

    /// <summary>
    /// Registra um atalho global novo — chamado da thread de UI (MainViewModel), mas a chamada real
    /// de <c>RegisterHotKey</c> acontece dentro da thread dona da janela (ver comentário da classe).
    /// Nunca lança em caso de falha (ex.: tecla já usada por outro programa) — devolve
    /// <see cref="HotkeyRegistrationResult.Failure"/> com uma mensagem legível, pra quem chamou
    /// avisar o operador honestamente em vez de fingir que o atalho está ativo.
    /// </summary>
    public HotkeyRegistrationResult Register(GlobalHotkeyKey key, Action onPressed)
    {
        ArgumentNullException.ThrowIfNull(onPressed);

        if (_disposed)
        {
            return HotkeyRegistrationResult.Failure("o serviço de atalho global já foi encerrado.");
        }

        lock (_registerLock)
        {
            var id = _nextId++;
            var succeeded = false;
            var win32Error = 0;

            // Correção pós-entrega (2026-08-10): Tab sem nenhum modificador REGISTRAVA com sucesso
            // (RegisterHotKey devolvia true, nenhum erro) mas o WM_HOTKEY nunca chegava de verdade —
            // confirmado pelo usuário testando no Windows real (nenhuma mensagem de erro, e mesmo
            // assim a tecla não disparava o comando). Pesquisa técnica sobre o assunto encontrou a
            // causa mais provável: capturar uma tecla de navegação comum (Tab, Enter, Espaço...) via
            // RegisterHotKey SEM nenhum modificador é a assinatura exata de uma técnica conhecida de
            // keylogger ("hotkeyz"/Grzegorz Tworek, documentada publicamente pela Elastic Security
            // Labs) — softwares de segurança/EDR modernos têm mitigação específica contra esse
            // padrão, deixando o registro "ter sucesso" mas descartando a entrega do WM_HOTKEY em
            // silêncio, exatamente o sintoma relatado. F13-F24 não sofrem disso (não fazem parte da
            // assinatura conhecida — ninguém digita com elas). Correção: Tab passa a exigir Ctrl
            // (MOD_CONTROL) junto — sai do padrão "tecla de navegação comum sem modificador" que a
            // mitigação de segurança provavelmente filtra, e também evita colidir com qualquer outro
            // programa que já tenha reivindicado o Tab puro. F1-F24 continuam sem modificador nenhum
            // (nenhum relato de problema com elas). Ver também o rótulo "Ctrl+Tab" no ComboBox de
            // Configurações (CartridgeSettingsViewModel.KeyboardShortcuts) — o operador precisa saber
            // que agora precisa segurar Ctrl.
            var modifiers = MOD_NOREPEAT | (key == GlobalHotkeyKey.Tab ? MOD_CONTROL : 0);

            RunOnMessageThread(() =>
            {
                succeeded = RegisterHotKey(_hwnd, id, modifiers, (uint)key);
                if (!succeeded)
                {
                    win32Error = Marshal.GetLastWin32Error();
                }
            });

            if (!succeeded)
            {
                return HotkeyRegistrationResult.Failure(DescribeWin32Error(win32Error));
            }

            _callbacks[id] = onPressed;
            return HotkeyRegistrationResult.Success(id);
        }
    }

    /// <summary>Remove um atalho registrado por <see cref="Register"/> — sem efeito se o id já não estiver registrado (idempotente, seguro de chamar de novo).</summary>
    public void Unregister(int id)
    {
        if (_disposed)
        {
            return;
        }

        lock (_registerLock)
        {
            if (!_callbacks.Remove(id))
            {
                return;
            }

            RunOnMessageThread(() => UnregisterHotKey(_hwnd, id));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_registerLock)
        {
            foreach (var id in _callbacks.Keys)
            {
                var capturedId = id;
                RunOnMessageThread(() => UnregisterHotKey(_hwnd, capturedId));
            }

            _callbacks.Clear();
        }

        RunOnMessageThread(() => DestroyWindow(_hwnd));
        _thread.Join(TimeSpan.FromSeconds(2));
        _windowReady.Dispose();
    }

    /// <summary>
    /// Marcha a execução de <paramref name="action"/> pra dentro da thread dona da janela e espera
    /// terminar — necessário porque RegisterHotKey/UnregisterHotKey (chamados de dentro de
    /// <paramref name="action"/>) só funcionam na thread que criou o hWnd (ver comentário da classe).
    /// Se já estivermos nessa thread (ex.: chamado de dentro do próprio WndProc), executa direto, sem
    /// enfileirar — evita deadlock (a thread nunca consegue processar sua própria fila enquanto
    /// espera ela mesma).
    /// </summary>
    private void RunOnMessageThread(Action action)
    {
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
        {
            action();
            return;
        }

        using var done = new ManualResetEventSlim(false);
        _pendingActions.Enqueue(() =>
        {
            try
            {
                action();
            }
            finally
            {
                done.Set();
            }
        });

        PostMessage(_hwnd, WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero);
        done.Wait(TimeSpan.FromSeconds(2));
    }

    private void RunMessageLoop()
    {
        var hInstance = GetModuleHandle(null);

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = hInstance,
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = _className,
            hIconSm = IntPtr.Zero
        };

        RegisterClassEx(ref wndClass);

        _hwnd = CreateWindowEx(0, _className, null, 0, CW_USEDEFAULT, CW_USEDEFAULT, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);

        _windowReady.Set();

        if (_hwnd == IntPtr.Zero)
        {
            // Sem janela, não há como registrar hotkey nenhum — sai do loop; Register() vai falhar
            // honestamente (RegisterHotKey com hWnd zero também falha) em vez de travar esperando.
            return;
        }

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_HOTKEY:
                var id = wParam.ToInt32();
                if (_callbacks.TryGetValue(id, out var callback))
                {
                    // Chamado direto (sem lock) — RunOnMessageThread já garante que Register/Unregister
                    // rodam nesta mesma thread, então não há corrida real com o Dictionary aqui (só um
                    // "escritor" possível por vez, a própria thread deste loop). O callback em si
                    // (MainViewModel) é responsável por marchar pra Dispatcher.Invoke se precisar tocar
                    // em binding da WPF — esta classe não sabe nada de WPF de propósito.
                    callback();
                }

                return IntPtr.Zero;

            case WM_APP_INVOKE:
                while (_pendingActions.TryDequeue(out var pending))
                {
                    pending();
                }

                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    private static string DescribeWin32Error(int win32Error) => win32Error switch
    {
        1409 => "essa tecla já está registrada por outro programa neste computador (ERROR_HOTKEY_ALREADY_REGISTERED).", // 0x581
        5 => "acesso negado pelo Windows ao registrar o atalho.",
        _ => $"falha do Windows ao registrar o atalho (código {win32Error})."
    };

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
