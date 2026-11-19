using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OnePlusBuds4;

/// <summary>Russian / English UI. Auto-detects the system language; can be toggled from the tray menu.</summary>
public static class Loc
{
    public static bool Ru { get; private set; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase);

    static readonly string CfgDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnePlusBuds");
    static readonly string CfgFile = Path.Combine(CfgDir, "lang.txt");

    static Loc()
    {
        try { if (File.Exists(CfgFile)) Ru = File.ReadAllText(CfgFile).Trim() == "ru"; } catch { }
    }

    public static void Set(bool ru)
    {
        Ru = ru;
        try { Directory.CreateDirectory(CfgDir); File.WriteAllText(CfgFile, ru ? "ru" : "en"); } catch { }
    }

    static readonly Dictionary<string, string> RuMap = new()
    {
        ["ANC"] = "ANC", ["Adaptive"] = "Адаптивный", ["Auto"] = "Авто", ["Auto-scroll"] = "Автопрокрутка",
        ["Balanced"] = "Сбалансированный", ["Bass"] = "Бас", ["Bold"] = "Насыщенный", ["Clear Vocals"] = "Чистый вокал",
        ["Connect"] = "Подключить", ["Create / Apply Custom EQ"] = "Создать / применить свой EQ", ["Delete"] = "Удалить",
        ["DynAudio"] = "DynAudio", ["High"] = "Высокий", ["Left"] = "Левый", ["Low"] = "Низкий", ["Mid"] = "Средний",
        ["Moderate"] = "Умеренный", ["Off"] = "Выкл", ["Right"] = "Правый", ["Select"] = "Выбрать", ["Send Hex"] = "Отправить HEX",
        ["Serenade"] = "Serenade", ["Start minimized with Windows"] = "Запускать свёрнутым вместе с Windows",
        ["Transparency"] = "Прозрачность", ["📋 Copy All"] = "📋 Копировать всё", ["📟 Log"] = "📟 Журнал",
        ["🔍 Probe Features"] = "🔍 Поиск функций", ["🗑 Clear"] = "🗑 Очистить",
        ["(with AI assistance)"] = "(при помощи ИИ)", ["About"] = "О программе", ["Battery: —"] = "Батарея: —",
        ["CUSTOM EQ"] = "СВОЙ ЭКВАЛАЙЗЕР", ["Connecting…"] = "Подключение…", ["Developed by Thomaspatrick14"] = "Автор: Thomaspatrick14",
        ["Device"] = "Устройство", ["Double-tap"] = "Двойное касание", ["EARBUD CONTROLS"] = "УПРАВЛЕНИЕ НАУШНИКАМИ",
        ["EQ — SOUND MASTER"] = "EQ — SOUND MASTER", ["Live Bluetooth RFCOMM Packet Monitor"] = "Монитор пакетов Bluetooth RFCOMM",
        ["Long touch & hold"] = "Долгое касание", ["My EQ"] = "Мой EQ", ["NOISE CONTROL"] = "ШУМОПОДАВЛЕНИЕ",
        ["Noise Cancellation"] = "Шумоподавление", ["OFF"] = "ВЫКЛ", ["OnePlus Buds Controller"] = "OnePlus Buds — управление",
        ["Packets: 0"] = "Пакетов: 0", ["Single-tap"] = "Одно касание", ["Slide"] = "Свайп", ["Slot"] = "Слот",
        ["Touch & hold"] = "Касание и удержание", ["Triple-tap"] = "Тройное касание", ["WHEN NOT ON A CALL"] = "ВНЕ ЗВОНКА",
        ["WHEN ON A CALL"] = "ВО ВРЕМЯ ЗВОНКА", ["— OnePlus Buds"] = "— OnePlus Buds",
        ["Close"] = "Закрыть", ["Minimize"] = "Свернуть",
        ["Launch automatically into system tray when you log in to Windows"] = "Запускать в трее при входе в Windows",
        ["Open Live Bluetooth Packet Monitor"] = "Открыть монитор пакетов Bluetooth",
        ["Pick a paired device, or type/paste a MAC"] = "Выберите сопряжённое устройство или введите MAC",
        ["Scan function codes 0x01..0x35 and feature switches to discover all supported earbud features"] = "Перебрать коды функций 0x01..0x35 и переключатели, чтобы найти все функции наушников",
        ["Type hex bytes to send (e.g. AA 07 00 00 01 01 00 00 00)"] = "Введите байты в HEX (например AA 07 00 00 01 01 00 00 00)",
        // dynamic strings
        ["Battery"] = "Батарея", ["Worn"] = "В ушах", ["Not worn"] = "Не в ушах", ["Show"] = "Показать", ["Exit"] = "Выход",
        ["Start with Windows"] = "Запускать с Windows", ["Not connected"] = "Нет подключения",
        ["Select a device and press Connect"] = "Выберите устройство и нажмите «Подключить»",
        ["Pick a device or type a MAC, then Connect"] = "Выберите устройство или введите MAC, затем «Подключить»",
        ["No paired OnePlus Buds found — pair in Windows Bluetooth settings or paste MAC"] = "Сопряжённые наушники не найдены — выполните сопряжение в настройках Bluetooth или введите MAC",
        ["Pick a device or type a MAC first"] = "Сначала выберите устройство или введите MAC",
        ["Waiting for earbuds to connect…"] = "Ожидание подключения наушников…",
        ["Waiting for earbuds to connect to Windows…"] = "Ожидание подключения наушников к Windows…",
        ["Earbuds unavailable — press Connect to retry."] = "Наушники недоступны — нажмите «Подключить».",
        ["Connected"] = "Подключено", ["Language"] = "Язык", ["Charging"] = "Зарядка", ["Case"] = "Кейс",
        ["Noise cancellation"] = "Шумоподавление", ["Transparency mode"] = "Режим прозрачности", ["Noise control off"] = "Шумоподавление выключено",
        ["Adaptive mode"] = "Адаптивный режим",
    };

    public static string T(string s) => Ru && RuMap.TryGetValue(s, out var r) ? r : s;

    /// <summary>Translate status-line sentences that embed a MAC / label.</summary>
    public static string Status(string s)
    {
        if (!Ru) return s;
        if (RuMap.TryGetValue(s, out var r)) return r;
        if (s.StartsWith("Connecting to ")) return "Подключение к " + s[14..];
        if (s.StartsWith("Connected to ")) return "Подключено к " + s[13..];
        if (s.StartsWith("Reconnecting to ")) return "Переподключение к " + s[16..];
        if (s.EndsWith(" ✓")) return s;
        int i = s.IndexOf(" failed (error ", StringComparison.Ordinal);
        if (i > 0) return s[..i] + " — ошибка (" + s[(i + 15)..].TrimEnd(')') + ")";
        return s;
    }

    // ── Tree translation (keeps the English original so the language can be toggled back) ──
    static readonly ConditionalWeakTable<DependencyObject, Dictionary<string, string>> Orig = new();

    static void Swap(DependencyObject o, string key, string? cur, Action<string> set)
    {
        if (string.IsNullOrEmpty(cur) && !(Orig.TryGetValue(o, out var d0) && d0.ContainsKey(key))) return;
        var d = Orig.GetOrCreateValue(o);
        if (!d.TryGetValue(key, out var en))
        {
            en = cur!;
            // only remember strings we know; anything else (live data) is left alone
            if (!RuMap.ContainsKey(en)) return;
            d[key] = en;
        }
        set(T(en));
    }

    public static void Apply(DependencyObject root)
    {
        if (root is TextBlock tb && tb.Inlines.Count <= 1) Swap(tb, "T", tb.Text, v => tb.Text = v);
        if (root is ContentControl cc && cc.Content is string s) Swap(cc, "C", s, v => cc.Content = v);
        if (root is FrameworkElement fe && fe.ToolTip is string tt) Swap(fe, "TT", tt, v => fe.ToolTip = v);
        if (root is Window w) Swap(w, "W", w.Title, v => w.Title = v);

        if (root is Visual || root is System.Windows.Media.Media3D.Visual3D)
            for (int i = 0, n = VisualTreeHelper.GetChildrenCount(root); i < n; i++)
                Apply(VisualTreeHelper.GetChild(root, i));
        foreach (var c in LogicalTreeHelper.GetChildren(root))
            if (c is DependencyObject dc && !(dc is Visual)) Apply(dc);
    }
}
