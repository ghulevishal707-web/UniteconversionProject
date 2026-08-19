using System.Globalization;
using System.Text;

namespace MusicTrackGen.Core;

public enum Vowel { A, E, I, O, U }

public enum OnsetClass { None, Plosive, Fricative, Nasal, Liquid }

/// <summary>
/// One sung syllable. The vocal synthesiser needs the vowel (which formant shape
/// to hold) and the onset class (what kind of consonant burst to precede it with).
/// </summary>
public sealed record Syllable(string Text, Vowel Vowel, OnsetClass Onset, bool LongVowel = false);

public sealed class LyricLine
{
    public required string Text { get; init; }
    public required IReadOnlyList<Syllable> Syllables { get; init; }
    public string Role { get; init; } = "verse";
    public int Count => Syllables.Count;
    public override string ToString() => Text;
}

/// <summary>
/// Per-language phrase banks and syllabifiers. The PoC assembles lyrics from these
/// original phrase pools; it is not a lyricist, and the user can paste their own text.
/// </summary>
public static class Lyrics
{
    // Short, original, mood-neutral phrases. Deliberately generic vocabulary so
    // nothing here reproduces an existing song.
    private static readonly Dictionary<Language, string[]> VersePool = new()
    {
        [Language.English] =
        [
            "the evening holds its light",
            "i walk the quiet road",
            "your name is in the rain",
            "slow water finds the sea",
            "morning opens like a hand",
            "we counted every star",
            "the window keeps the sun",
            "a long song in the wind",
            "nothing here is far away",
            "the city learns to sleep",
            "keep the lantern burning low",
            "summer leaves the door ajar",
        ],
        [Language.Hindi] =
        [
            "साँझ ढली है धीरे",
            "मन ये गाता जाए",
            "तेरी बातें याद हैं",
            "चाँदनी बिखरी राह में",
            "हवा में खुशबू घुली",
            "सपने जागे रात भर",
            "नदिया बहती जाए",
            "दिल की धड़कन सुन ले",
            "बादल छाए आँगन में",
            "रोशनी उतरी छत पे",
            "बीते पल लौट आए",
            "मौसम गाता राग है",
        ],
        [Language.Marathi] =
        [
            "सांज सरली हळू",
            "मन हे गाणे गाते",
            "तुझी आठवण साथ",
            "चांदणे अंगणात",
            "वारा गुणगुणतो",
            "स्वप्न जागले रात",
            "नदी वाहते संगे",
            "काळजाची साद ऐक",
            "ढग दाटले वरती",
            "उजेड उतरला घरी",
            "क्षण परत आले",
            "ऋतू गाणे म्हणतो",
        ],
        [Language.Punjabi] =
        [
            "ਸ਼ਾਮ ਢਲੀ ਹੌਲੀ",
            "ਮਨ ਇਹ ਗਾਉਂਦਾ ਜਾਏ",
            "ਤੇਰੀਆਂ ਗੱਲਾਂ ਯਾਦ",
            "ਚਾਨਣੀ ਰਾਹ ਉੱਤੇ",
            "ਹਵਾ ਵਿੱਚ ਖੁਸ਼ਬੂ",
            "ਸੁਪਨੇ ਜਾਗੇ ਰਾਤ",
            "ਨਦੀ ਵਗਦੀ ਜਾਏ",
            "ਦਿਲ ਦੀ ਧੜਕਣ ਸੁਣ",
            "ਬੱਦਲ ਛਾਏ ਵਿਹੜੇ",
            "ਰੋਸ਼ਨੀ ਉੱਤਰੀ ਛੱਤ",
            "ਬੀਤੇ ਪਲ ਮੁੜ ਆਏ",
            "ਮੌਸਮ ਗਾਉਂਦਾ ਰਾਗ",
        ],
    };

    private static readonly Dictionary<Language, string[]> HookPool = new()
    {
        [Language.English] =
        [
            "stay a little longer",
            "carry me along",
            "light the way tonight",
            "sing it one more time",
            "hold the summer close",
        ],
        [Language.Hindi] =
        [
            "थोड़ा ठहर जा",
            "साथ चला चल",
            "राह रोशन कर",
            "गा ले फिर एक बार",
            "मौसम थाम ले",
        ],
        [Language.Marathi] =
        [
            "जरा थांब ना",
            "सोबत चाल तू",
            "वाट उजळ दे",
            "गा ना पुन्हा एकदा",
            "ऋतू धरून ठेव",
        ],
        [Language.Punjabi] =
        [
            "ਥੋੜਾ ਠਹਿਰ ਜਾ",
            "ਨਾਲ ਤੁਰਦਾ ਚੱਲ",
            "ਰਾਹ ਰੋਸ਼ਨ ਕਰ",
            "ਗਾ ਲੈ ਫਿਰ ਇੱਕ ਵਾਰ",
            "ਮੌਸਮ ਫੜ ਲੈ",
        ],
    };

    public static string ScriptName(Language language) => language switch
    {
        Language.English => "Latin",
        Language.Punjabi => "Gurmukhi",
        _ => "Devanagari",
    };

    /// <summary>Builds a lyric sheet: hook plus verse lines, sized to the section plan.</summary>
    public static List<LyricLine> Assemble(Language language, Rng rng, int verseLines, int hookLines)
    {
        var lines = new List<LyricLine>();
        string[] hooks = HookPool[language];
        string[] verses = VersePool[language];

        string hook = hooks[rng.Next(0, hooks.Length)];
        var used = new HashSet<string>();

        for (int i = 0; i < hookLines; i++)
            lines.Add(Parse(hook, language, "hook"));

        for (int i = 0; i < verseLines; i++)
        {
            string pick = verses[rng.Next(0, verses.Length)];
            for (int attempt = 0; attempt < 6 && !used.Add(pick); attempt++)
                pick = verses[rng.Next(0, verses.Length)];
            lines.Add(Parse(pick, language, "verse"));
        }
        return lines;
    }

    /// <summary>Splits user-supplied text into lyric lines.</summary>
    public static List<LyricLine> FromUserText(string text, Language language)
    {
        var lines = new List<LyricLine>();
        foreach (string raw in text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            string t = raw.Trim();
            if (t.Length == 0) continue;
            var parsed = Parse(t, language, "verse");
            if (parsed.Count > 0) lines.Add(parsed);
        }
        return lines;
    }

    public static LyricLine Parse(string text, Language language, string role) => new()
    {
        Text = text,
        Syllables = Syllabify(text, language),
        Role = role,
    };

    public static IReadOnlyList<Syllable> Syllabify(string text, Language language) => language switch
    {
        Language.English => SyllabifyLatin(text),
        Language.Punjabi => SyllabifyIndic(text, gurmukhi: true),
        _ => SyllabifyIndic(text, gurmukhi: false),
    };

    // ---------- Latin ----------

    private const string LatinVowels = "aeiouy";

    private static List<Syllable> SyllabifyLatin(string text)
    {
        var result = new List<Syllable>();
        foreach (string word in text.Split([' ', '\t', ',', '.', ';', ':', '!', '?'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string w = word.ToLowerInvariant();
            int i = 0;
            var chunkStart = 0;
            bool sawVowel = false;

            while (i < w.Length)
            {
                bool isVowel = LatinVowels.Contains(w[i]);
                if (isVowel)
                {
                    sawVowel = true;
                    // consume the whole vowel group
                    int gs = i;
                    while (i < w.Length && LatinVowels.Contains(w[i])) i++;
                    // one following consonant stays with this syllable when more letters follow
                    int consonantRun = i;
                    while (consonantRun < w.Length && !LatinVowels.Contains(w[consonantRun])) consonantRun++;
                    int keep = consonantRun - i;
                    int end = consonantRun < w.Length && keep > 1 ? i + keep - 1 : consonantRun;

                    string piece = w[chunkStart..Math.Max(end, i)];
                    result.Add(MakeLatinSyllable(piece, w[gs]));
                    i = Math.Max(end, i);
                    chunkStart = i;
                }
                else i++;
            }

            if (!sawVowel && w.Length > 0)
                result.Add(new Syllable(w, Vowel.A, ClassifyLatin(w[0])));
        }
        return result;
    }

    private static Syllable MakeLatinSyllable(string piece, char vowelChar)
    {
        Vowel v = vowelChar switch
        {
            'a' => Vowel.A,
            'e' => Vowel.E,
            'i' or 'y' => Vowel.I,
            'o' => Vowel.O,
            _ => Vowel.U,
        };
        OnsetClass onset = piece.Length > 0 && !LatinVowels.Contains(piece[0])
            ? ClassifyLatin(piece[0])
            : OnsetClass.None;
        bool longVowel = piece.Length > 1 && LatinVowels.Contains(piece[^1]);
        return new Syllable(piece, v, onset, longVowel);
    }

    private static OnsetClass ClassifyLatin(char c) => c switch
    {
        'p' or 'b' or 't' or 'd' or 'k' or 'g' or 'c' or 'q' => OnsetClass.Plosive,
        'f' or 'v' or 's' or 'z' or 'h' or 'x' or 'j' => OnsetClass.Fricative,
        'm' or 'n' => OnsetClass.Nasal,
        'l' or 'r' or 'w' => OnsetClass.Liquid,
        _ => OnsetClass.Fricative,
    };

    // ---------- Devanagari / Gurmukhi ----------
    // Both are abugidas: a consonant carries an inherent 'a' unless a matra
    // (vowel sign) replaces it or a halant suppresses it. One akshara = one
    // sung syllable, which is exactly the unit the melody needs.

    private static readonly Dictionary<char, (Vowel V, bool Long)> DevanagariVowelSigns = new()
    {
        ['ा'] = (Vowel.A, true),   // aa
        ['ि'] = (Vowel.I, false),  // i
        ['ी'] = (Vowel.I, true),   // ii
        ['ु'] = (Vowel.U, false),  // u
        ['ू'] = (Vowel.U, true),   // uu
        ['ृ'] = (Vowel.I, false),  // vocalic r
        ['े'] = (Vowel.E, false),  // e
        ['ै'] = (Vowel.E, true),   // ai
        ['ो'] = (Vowel.O, false),  // o
        ['ौ'] = (Vowel.O, true),   // au
    };

    private static readonly Dictionary<char, (Vowel V, bool Long)> GurmukhiVowelSigns = new()
    {
        ['ਾ'] = (Vowel.A, true),
        ['ਿ'] = (Vowel.I, false),
        ['ੀ'] = (Vowel.I, true),
        ['ੁ'] = (Vowel.U, false),
        ['ੂ'] = (Vowel.U, true),
        ['ੇ'] = (Vowel.E, false),
        ['ੈ'] = (Vowel.E, true),
        ['ੋ'] = (Vowel.O, false),
        ['ੌ'] = (Vowel.O, true),
    };

    private static readonly Dictionary<char, (Vowel V, bool Long)> IndependentVowels = new()
    {
        // Devanagari
        ['अ'] = (Vowel.A, false), ['आ'] = (Vowel.A, true),
        ['इ'] = (Vowel.I, false), ['ई'] = (Vowel.I, true),
        ['उ'] = (Vowel.U, false), ['ऊ'] = (Vowel.U, true),
        ['ए'] = (Vowel.E, false), ['ऐ'] = (Vowel.E, true),
        ['ओ'] = (Vowel.O, false), ['औ'] = (Vowel.O, true),
        // Gurmukhi
        ['ਅ'] = (Vowel.A, false), ['ਆ'] = (Vowel.A, true),
        ['ਇ'] = (Vowel.I, false), ['ਈ'] = (Vowel.I, true),
        ['ਉ'] = (Vowel.U, false), ['ਊ'] = (Vowel.U, true),
        ['ਏ'] = (Vowel.E, false), ['ਐ'] = (Vowel.E, true),
        ['ਓ'] = (Vowel.O, false), ['ਔ'] = (Vowel.O, true),
    };

    private const char DevanagariHalant = '्';
    private const char GurmukhiHalant = '੍';

    private static bool IsConsonant(char c, bool gurmukhi) => gurmukhi
        ? c >= 'ਕ' && c <= 'ਹ'
        : c >= 'क' && c <= 'ह';

    private static bool IsCombining(char c) =>
        CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark;

    private static List<Syllable> SyllabifyIndic(string text, bool gurmukhi)
    {
        var signs = gurmukhi ? GurmukhiVowelSigns : DevanagariVowelSigns;
        char halant = gurmukhi ? GurmukhiHalant : DevanagariHalant;
        var result = new List<Syllable>();

        foreach (string word in text.Split([' ', '\t', ',', '.', '।', '!', '?'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            int i = 0;
            while (i < word.Length)
            {
                char c = word[i];

                if (IndependentVowels.TryGetValue(c, out var iv))
                {
                    var sb = new StringBuilder().Append(c);
                    i++;
                    var (v, lng) = iv;
                    while (i < word.Length && IsCombining(word[i]))
                    {
                        if (signs.TryGetValue(word[i], out var m)) (v, lng) = m;
                        sb.Append(word[i]);
                        i++;
                    }
                    result.Add(new Syllable(sb.ToString(), v, OnsetClass.None, lng));
                    continue;
                }

                if (!IsConsonant(c, gurmukhi)) { i++; continue; }

                // Consonant cluster: consume consonant + halant pairs.
                var text2 = new StringBuilder();
                OnsetClass onset = ClassifyIndic(c, gurmukhi);
                Vowel vowel = Vowel.A;
                bool longVowel = false;
                bool explicitVowel = false;
                bool suppressed = false;

                while (i < word.Length && IsConsonant(word[i], gurmukhi))
                {
                    text2.Append(word[i]);
                    i++;
                    suppressed = false;

                    while (i < word.Length && IsCombining(word[i]))
                    {
                        char mark = word[i];
                        if (signs.TryGetValue(mark, out var m))
                        {
                            (vowel, longVowel) = m;
                            explicitVowel = true;
                        }
                        else if (mark == halant) suppressed = true;
                        text2.Append(mark);
                        i++;
                    }

                    if (!suppressed) break;   // vowel reached, syllable closes
                }

                // A word-final consonant with a suppressed inherent vowel attaches
                // to the previous syllable rather than becoming a silent one.
                if (suppressed && !explicitVowel && result.Count > 0 && i >= word.Length)
                {
                    var prev = result[^1];
                    result[^1] = prev with { Text = prev.Text + text2 };
                    continue;
                }

                result.Add(new Syllable(text2.ToString(), vowel, onset, longVowel));
            }
        }
        return result;
    }

    private static OnsetClass ClassifyIndic(char c, bool gurmukhi)
    {
        int baseCp = gurmukhi ? 0x0A15 : 0x0915;
        int idx = c - baseCp;   // ka kha ga gha nga cha chha ja jha nya ta ... ha
        return idx switch
        {
            >= 0 and <= 4 => OnsetClass.Plosive,        // ka-varga
            >= 5 and <= 9 => OnsetClass.Plosive,        // cha-varga (affricates)
            >= 10 and <= 14 => OnsetClass.Plosive,      // ta-varga (retroflex)
            >= 15 and <= 19 => OnsetClass.Plosive,      // ta-varga (dental)
            >= 20 and <= 24 => OnsetClass.Plosive,      // pa-varga
            25 or 26 => OnsetClass.Nasal,               // ma, ya boundary
            >= 27 and <= 30 => OnsetClass.Liquid,       // ra, la, va
            _ => OnsetClass.Fricative,                  // sha, sa, ha
        };
    }
}
