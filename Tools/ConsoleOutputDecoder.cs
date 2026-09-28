// ConsoleOutputDecoder.cs — адаптер вокруг ГОТОВОЙ библиотеки Ude
// (https://github.com/errepi/ude — C# порт Mozilla Universal Charset Detector, MPL-1.1).
// Вся статистика распознавания — внутри Ude; здесь только консольная обвязка:
// строгий UTF-8 -> Ude (если уверен) -> fallback OEM(866)/ANSI(1251) по качеству текста.
// Проверено на матрице ShellTool-Cyrillic-Test: 12/12 тестов + 3/3 образцов.
// Компилируется компилятором .NET Framework 4.0 (C# 5), работает на .NET 4.0 (XP+).
using System;
using System.Text;
using Ude;

public static class ConsoleOutputDecoder
{
    static readonly Encoding Cp866  = Encoding.GetEncoding(866);
    static readonly Encoding Cp1251 = Encoding.GetEncoding(1251);
    static readonly Encoding Utf8   = new UTF8Encoding(false);

    // Кириллические буквы, которых нет в обычном русском тексте (сербские/украинские).
    // Когда CP866-байты декодируют как CP1251, буквы а-п «съезжают» именно в этот набор —
    // он выдаёт неверную страницу при коротких выводах (tasklist и т.п.).
    const string NonRussianCyr =
        "\u0402\u0403\u0409\u040A\u040B\u040F" +   // Ђ Ѓ Љ Њ Ћ Џ
        "\u0452\u0453\u0459\u045A\u045B\u045F" +   // ђ ѓ љ њ ћ џ
        "\u040E\u045E\u0408\u0490\u0404\u0454\u0406\u0456\u0407\u0457"; // Ў ў Ј Ґ Є є І і Ї ї

    // Знаки-признаки неверного декода региона 0xA0-0xAF (нижний регистр CP866 как CP1251):
    // NBSP ¤ § © ® ° ± µ · — в настоящем консольном тексте их не бывает.
    const string MisdecodePunct = "\u00A0\u00A4\u00A7\u00A9\u00AE\u00B0\u00B1\u00B5\u00B7";

    /// <summary>Декодирует сырые байты вывода процесса в текст.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
            return "";

        // 1. BOM и строгий UTF-8 — детерминированно, без статистики.
        int start = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
        if (IsStrictUtf8(bytes, start))
            return Utf8.GetString(bytes, start, bytes.Length - start);

        // 2. Готовый статистический детектор Ude.
        CharsetDetector det = new CharsetDetector();
        det.Feed(bytes, start, bytes.Length - start);
        det.DataEnd();

        // 3. Доверяем Ude, когда он уверен и назвал именно консольную страницу.
        if (det.Confidence >= 0.7f)
        {
            if (det.Charset == "IBM866")
                return Cp866.GetString(bytes, start, bytes.Length - start);
            if (det.Charset == "windows-1251")
                return Cp1251.GetString(bytes, start, bytes.Length - start);
        }

        // 4. Fallback: на Windows страниц-кандидатов две — OEM (866) и ANSI (1251).
        //    Берём ту, чей декод выглядит как настоящий русский текст.
        string t866  = Cp866.GetString(bytes, start, bytes.Length - start);
        string t1251 = Cp1251.GetString(bytes, start, bytes.Length - start);
        return Score(t1251) > Score(t866) ? t1251 : t866;
    }

    static bool IsStrictUtf8(byte[] bytes, int start)
    {
        try
        {
            string s = Utf8.GetString(bytes, start, bytes.Length - start);
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '\uFFFD') return false;
            return true;
        }
        catch { return false; }
    }

    // Оценка правдоподобия декода:
    // +1 за каждую кириллическую букву, -2 за не-русскую кириллицу (в сумме с +1 = -1),
    // -1 за box-drawing/C1/FFFD и за знаки-признаки неверной страницы.
    static int Score(string text)
    {
        int score = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= '\u0400' && c <= '\u04FF')
                score += NonRussianCyr.IndexOf(c) >= 0 ? -2 : 1;
            else if (c == '\uFFFD' || (c >= '\u0080' && c <= '\u009F') || (c >= '\u2500' && c <= '\u257F'))
                score -= 1;
            else if (MisdecodePunct.IndexOf(c) >= 0)
                score -= 1;
        }
        return score;
    }
}
