using System.Windows;

namespace Snag;

/// <summary>How a dialog looks: an everyday question, something that deletes or stops things, or a plain notice.</summary>
public enum DialogTone { Question, Danger, Notice }

/// <summary>What a dialog says. <see cref="CancelText"/> null means a single button.</summary>
public sealed record DialogRequest(string Title, string Message, string ConfirmText, string? CancelText, DialogTone Tone, string Glyph);

/// <summary>
/// Snag's own confirmation dialogs, drawn in the window like the download options pop-up (blurred app behind,
/// theme colours, Snag's buttons) instead of Windows' grey message boxes. Synchronous, like MessageBox.
/// </summary>
public static class Dialogs
{
    public const string TrashGlyph = "";
    public const string DownloadGlyph = "";
    public const string HistoryGlyph = "";
    public const string UpdateGlyph = "";
    public const string WarningGlyph = "";
    public const string QuestionGlyph = "";

    /// <summary>True if the person chose <paramref name="confirmText"/>; Cancel, Esc or a click outside is false.</summary>
    public static bool Confirm(string title, string message, string confirmText, string cancelText = "Cancel",
        DialogTone tone = DialogTone.Question, string glyph = QuestionGlyph)
    {
        if (Host is { } window) return window.ShowSnagDialog(new DialogRequest(title, message, confirmText, cancelText, tone, glyph));
        // Before the window is up (or after it's gone), Windows' own box will have to do.
        return MessageBox.Show(message, title, MessageBoxButton.YesNo,
            tone == DialogTone.Danger ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    /// <summary>A message with just an OK button.</summary>
    public static void Notice(string title, string message, string glyph = WarningGlyph)
    {
        if (Host is { } window) window.ShowSnagDialog(new DialogRequest(title, message, "OK", null, DialogTone.Notice, glyph));
        else MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static MainWindow? Host =>
        Application.Current?.MainWindow is MainWindow { IsLoaded: true, IsVisible: true } window && window.CanShowSnagDialog ? window : null;
}
