using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace MacKeys;

/// <summary>Which language MacKeys speaks: the system's (Brazilian Portuguese if Windows is in any Portuguese, else English) or a fixed one. "Portuguese" always means Brazilian Portuguese (PT-BR).</summary>
internal enum AppLanguage { Auto, Portuguese, English }

/// <summary>
/// Brazilian Portuguese (PT-BR) is MacKeys' source language: every text is written in Portuguese and doubles as the key into the
/// English table (<see cref="En"/>). Windows are translated right after they're built (<see cref="Translate"/>
/// walks their texts, titles, buttons and tooltips); texts made in code go through <see cref="T"/> and <see cref="F"/>.
/// </summary>
internal static partial class L
{
    public static bool English { get; private set; }

    public static bool SystemIsPortuguese =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("pt", StringComparison.OrdinalIgnoreCase);

    public static void Apply(AppLanguage choice) => English = choice switch
    {
        AppLanguage.English => true,
        AppLanguage.Portuguese => false,
        _ => !SystemIsPortuguese,
    };

    /// <summary>The text in the current language (the Portuguese original when there's no translation).</summary>
    public static string T(string portuguese)
    {
        if (!English || string.IsNullOrEmpty(portuguese)) return portuguese;
        if (En.TryGetValue(portuguese, out var english)) return english;
        NoteMissing(portuguese);
        return portuguese;
    }

    /// <summary>Like <see cref="T"/>, for text that is usually not words (key combinations): never reported missing.</summary>
    public static string Keys(string text) => English && En.TryGetValue(text, out var english) ? english : text;

    /// <summary>A format string in the current language, filled in: F("Versão {0}", v).</summary>
    public static string F(string portugueseFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(portugueseFormat), args);

    /// <summary>Translates a window or control built from XAML: texts, runs, button contents, headers, tooltips.</summary>
    public static void Translate(DependencyObject root)
    {
        if (!English) return;
        Walk(root);
    }

    private static void Walk(DependencyObject node)
    {
        switch (node)
        {
            case Window window:
                window.Title = T(window.Title);
                break;
            case TextBlock text:
                if (text.Inlines.Count <= 1 && text.Inlines.FirstInline is null or Run)
                    text.Text = T(text.Text);
                else
                    foreach (var inline in text.Inlines.ToList()) TranslateInline(inline);
                break;
            case HeaderedContentControl { Header: string header } headered:
                headered.Header = T(header);
                break;
        }
        if (node is ContentControl { Content: string content } control) control.Content = T(content);
        if (node is FrameworkElement { ToolTip: string tip } element) element.ToolTip = T(tip);

        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().ToList())
            Walk(child);
    }

    private static void TranslateInline(Inline inline)
    {
        switch (inline)
        {
            case Run run:
                // Runs keep their spacing: translate the words, put the spaces back.
                string trimmed = run.Text.Trim();
                if (trimmed.Length > 0)
                    run.Text = run.Text.Replace(trimmed, T(trimmed));
                break;
            case Span span:
                foreach (var child in span.Inlines.ToList()) TranslateInline(child);
                break;
        }
    }

    // ───────────── missing translations (a development aid) ─────────────

    private static readonly HashSet<string> MissingSet = new();
    private static readonly Lazy<HashSet<string>> EnglishValues = new(() => En.Values.ToHashSet());

    /// <summary>Portuguese texts shown in English mode without a translation; --screenshots writes them out.</summary>
    public static IReadOnlyCollection<string> Missing => MissingSet;

    private static void NoteMissing(string text)
    {
        // Names, keys and symbols read the same in both languages; already-English texts aren't missing either.
        if (text.Count(char.IsLetter) < 3 || Same.Contains(text) || EnglishValues.Value.Contains(text)) return;
        lock (MissingSet) MissingSet.Add(text);
    }
}
