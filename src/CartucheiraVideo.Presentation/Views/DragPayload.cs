namespace CartucheiraVideo.Presentation.Views;

/// <summary>
/// Nome do formato usado no DataObject do drag & drop interno (Explorer -> Deck). Um formato
/// proprio evita colidir com o drag nativo de arquivo do Windows (que usaria "FileDrop").
/// </summary>
internal static class DragPayload
{
    public const string MediaPathFormat = "CartucheiraVideo.MediaFileName";
}
