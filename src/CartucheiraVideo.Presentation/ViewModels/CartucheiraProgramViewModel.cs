using System.Windows.Input;
using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// Representa UM programa dentro de uma Cartucheira (Vinhetas ou Trilhas) — Sprint Cartucheira 2,
/// parte 1 (2026-08-10): cada subpasta direta da pasta raiz configurada em Configurações →
/// Cartucheira vira um "programa" aqui (ex.: pasta raiz "D:\Audios\Vinhetas" com subpastas
/// "Jornal 18h"/"Esporte Show" — cada uma um programa, com seus próprios cartuchos de áudio
/// dentro). Substitui o conceito antigo de "Novo Banco" (cadastro manual) — pedido direto do
/// usuário: "seleção de vinhetas de cada programa sem precisar de um banco de cadastro".
///
/// Deliberadamente simples (2 níveis fixos: lista de programas → cartuchos do programa aberto →
/// Voltar), diferente da árvore genérica e recursiva do Explorer (<see cref="FolderNodeViewModel"/>)
/// — a Cartucheira não precisa de subpasta dentro de subpasta (arquitetura §14 nunca descreveu
/// isso), então não há necessidade de reaproveitar aquele lazy-loading recursivo aqui.
///
/// Sprint Cartucheira-Dashboard (2026-11-23): <see cref="Id"/> é novo — não-nulo quando este
/// Programa foi criado pela dashboard web (<c>Infrastructure.Cartucheira.CartucheiraFileStore</c>),
/// nulo pra Programas lidos do jeito antigo (subpasta criada manualmente no Explorer, sem
/// <c>programs.json</c>) ou de exemplo (mockup). É esse campo que o <see cref="AudioBankViewModel"/>
/// usa em <c>OpenProgram</c> pra decidir entre ler os cartuchos via <c>CartucheiraFileStore</c>
/// (dashboard-managed) ou escanear a pasta crua do jeito antigo.
/// </summary>
public sealed class CartucheiraProgramViewModel : ViewModelBase
{
    public CartucheiraProgramViewModel(string name, string? path, string? id, Action<CartucheiraProgramViewModel> onOpen)
    {
        Name = name;
        Path = path;
        Id = id;

        ArgumentNullException.ThrowIfNull(onOpen);
        OpenCommand = new RelayCommand(_ => onOpen(this));
    }

    public string Name { get; }

    /// <summary>Caminho completo da subpasta no disco — nulo pros programas de exemplo do mockup (sem pasta raiz configurada ainda).</summary>
    public string? Path { get; }

    /// <summary>Id deste Programa no <c>programs.json</c> da dashboard — ver comentário de classe.</summary>
    public string? Id { get; }

    public ICommand OpenCommand { get; }
}
