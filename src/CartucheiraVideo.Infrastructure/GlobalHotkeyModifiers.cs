using System;

namespace CartucheiraVideo.Infrastructure;

/// <summary>
/// Modificadores opcionais pra <see cref="GlobalHotkeyService.Register"/> — Fix v1.0.56
/// (2026-11-11), criado pro atalho de emergência "fechar todas as Telas"
/// (<c>MainViewModel.RegisterEmergencyCloseScreensHotkey</c>), que precisa de uma combinação rara
/// (Ctrl+Alt+Shift) pra nunca colidir com nada do Windows ou de outro programa já rodando — bem
/// diferente dos atalhos de Vinhetas/Trilhas (F13-F24 puras, ou Ctrl+Tab), que nunca precisaram
/// disso. Valores numéricos são as flags reais do Win32 (<c>MOD_ALT</c>/<c>MOD_CONTROL</c>/
/// <c>MOD_SHIFT</c>, <c>user32.dll</c>/<c>winuser.h</c>) — <see cref="GlobalHotkeyService"/>
/// combina (OR) o que for passado aqui direto no parâmetro <c>fsModifiers</c> de
/// <c>RegisterHotKey</c>, sem nenhuma tabela de tradução.
/// </summary>
[Flags]
public enum GlobalHotkeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004
}
